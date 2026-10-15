# Architecture

RhinoMCP is a research MCP server for **Rhino 7 on macOS**, including Intel/Rosetta setups. Rhino 8 and Windows are optional add-ons. The design rationale is in [`research/00-synthesis.md`](research/00-synthesis.md), and the Rhino 7 Mac findings are in [`research/11-r7-mac-inventory.md`](research/11-r7-mac-inventory.md).

## Components

```
Claude Code / Cursor / Codex ──── Streamable HTTP ────┐
Claude Desktop ── stdio ── rhinomcp-bridge (Swift, M5) ┤
                                                       ▼  http://127.0.0.1:7340/mcp  (Bearer token)
┌──────────────────────── Rhino 7 process (Mono 6.12, x86_64) ────────────────────────┐
│ RhinoMCP.rhp  (core, rarely changes)                                                 │
│   Server/   HttpListener · MCP JSON-RPC · auth · discovery file                     │
│   Core/     ITool contract · ToolRegistry · UiDispatcher · UndoScope · Handles      │
│   Commands/ MCPStart · MCPStop · MCPStatus · MCPConnect                             │
│        │ loads (from bytes, hot-reloadable)                                         │
│        ▼                                                                             │
│ RhinoMCP.Tools.<stamp>.dll  (all tools; discovered by reflection)                   │
│   Tools/Context · Objects · Sets · Layers · Geometry · Measure · View · Drawing ·   │
│         Script · History · Jobs · Verify · Grasshopper · Guidance                   │
└──────────────────────────────────────────────────────────────────────────────────────┘
        optional M4: Python 3 sidecar (arm64, uv-managed) via stdio JSON-lines
```

### Why it is built this way
- **In-process plugin.** On Rhino 7 Mac, a plugin is the only way to control a running Rhino.
- **HTTP served directly from the plugin.** Claude Code supports it natively, so no runtime is needed for the main path. The hand-written JSON-RPC layer has no SDK dependency, which avoids assembly conflicts under Mono/net48. McNeel's RhinoAI uses the same approach.
- **Core and tools split.** The tools assembly is loaded from bytes under a unique name per build. `dev_reload` swaps it without restarting Rhino, which keeps the development loop fast. Core changes still need a Rhino restart.
- **Toolchain.** Rhino's bundled Mono and Roslyn `csc.exe` (C# 7.3+) compile everything. No .NET SDK is required.

## Server

| Item | Value |
|---|---|
| Bind | `127.0.0.1:7340`. If the port is busy, the next free port in `7340–7359` is used. |
| Endpoint | `POST /mcp` (JSON-RPC 2.0, `application/json` response). `GET /mcp` returns 405. `GET /health` returns `{ok, version}`. |
| Auth | `Authorization: Bearer <token>`. The token is 32 random bytes as hex, persisted in `~/Library/Application Support/RhinoMCP/config.json` (mode 0600). |
| Host/Origin | Requests are rejected unless Host is `127.0.0.1:<port>` or `localhost:<port>`. Requests carrying any `Origin` header are rejected. |
| Discovery | `~/Library/Application Support/RhinoMCP/instances/<pid>.json`, containing `{pid, port, version, startedAt}`. The file is removed on exit. |
| Batching | JSON-RPC batch arrays are rejected with -32600 (not needed by Claude Code). |
| Methods | `initialize` (echoes a supported `protocolVersion`: 2025-11-25, 2025-06-18 or 2025-03-26) · `notifications/*` (202) · `ping` · `tools/list` · `tools/call` · `resources/list` · `resources/read` · `prompts/list` · `prompts/get` |
| Startup | The server starts on the first `RhinoApp.Idle` after load, if `autoStart` is set (default true). |

Server `instructions`: about 300 tokens of "how to use this server" text, generated from `skills/rhino-mcp-efficient-use`.

## Tool contract (`Core/ITool.cs`)

```csharp
public interface ITool {
    string Name { get; }            // snake_case, [a-z0-9_], ≤ 48 chars (Claude tool-name safe; no dots)
    string Description { get; }     // brief to a new hire: what, when, limits, failure modes
    string InputSchema { get; }     // JSON Schema (draft 2020-12) as a string
    ToolKind Kind { get; }          // Read | Mutate | Destructive | Script  → annotations + undo
    ToolResult Execute(ToolContext ctx, JObject args);   // runs on the UI thread
}
```

- **Discovery.** `ToolRegistry` reflects over the tools assembly and registers every concrete non-abstract `ITool` that has a public parameterless constructor. Duplicate names fail loudly.
- **Dispatch.** Each call goes HTTP thread → `UiDispatcher.Invoke` (`RhinoApp.InvokeOnUiThread` + `TaskCompletionSource`, default timeout 60 s) → `Execute`.
- **Undo.** If `Kind` is `Mutate`, `Destructive` or `Script`, the registry wraps the call in `doc.BeginUndoRecord("MCP: <name>")` / `EndUndoRecord`. It then calls `doc.Views.Redraw()` and attaches a `state_token`.
- **Annotations.** `Read` maps to `readOnlyHint: true`. `Destructive` and `Script` map to `destructiveHint: true`.
- **Own undo.** A tool implementing the marker interface `IManagesOwnUndo` is not wrapped in an undo record (used by `run_command`, whose commands, including `_Undo`, manage undo themselves). The registry still redraws and bumps `state_token`.
- **Schemas.** Tools write `$ref: "#/$defs/target|point|plane"`; `tools/list` injects the `$defs` block.
- **Core tools.** `server_info` and `dev_reload` (devMode only) live in the core. Both are `Read` kind because they do not touch the model.
- **Argument errors.** `ArgReader` gathers *all* argument errors into one `INVALID_ARGS` result.

### ToolContext
`Doc` (`RhinoDoc.ActiveDoc`) · `Units` ("mm") · `Tol` (absolute tolerance) · `AngleTolDeg` · `Handles` (session handle ↔ GUID map) · `Sets` (named-set store) · `Log(string)`.

### ToolResult
```json
{ "ok": true, "data": { … }, "units": "mm", "state_token": 42,
  "warnings": ["…"], "next_cursor": null }
{ "ok": false, "error": { "code": "BOOLEAN_FAILED", "message": "…", "guidance": "…", "details": { … } } }
```
- The payload is serialized compactly as a single `text` content block. Images are added as `image` blocks (PNG or JPEG, base64).
- `isError: true` is set whenever `ok` is false. JSON-RPC errors are used only for protocol faults.
- Common codes: `INVALID_ARGS`, `NOT_FOUND`, `NO_DOCUMENT`, `TIMEOUT`, `GEOMETRY_FAILED`, `BOOLEAN_FAILED`, `SCRIPT_ERROR`, `UNSUPPORTED`, `INTERNAL`.

### Conventions every tool follows
1. **Addressing.**
   - Objects are referenced by handle (`o12`), GUID, or `{set: "name"}` / `{query: {...}}`.
   - Tools return handles by default. GUIDs only when `include_guids: true`.
2. **Bounded output.**
   - Lists default to `limit: 50`, with `total`, `returned` and `next_cursor`.
   - Counts and aggregates come before lists.
3. **Units.**
   - All lengths are in document units, and the unit is echoed back.
   - Points are `[x, y, z]`.
   - Planes are `{origin, x_axis, y_axis}` or a name: `world_xy`, `world_yz`, `world_zx`, a named CPlane, or a level.
4. **Mutating tools** return the handles they created, modified or deleted, plus the result bbox.
5. **Failure handling.** Never fail silently. Validate geometry results (null, `IsValid`, sanity checks) and report the failure.
6. **Errors teach.** An error explains what failed, why, which values are valid, and which tool to call next.

## Threading & long work
- RhinoDoc access happens only on the UI thread.
- Pure geometry on duplicated objects may run on worker threads, via `Jobs` (M3).
- Jobs process their work in chunks across `RhinoApp.Idle` ticks of about 50 ms, and check `RhinoApp.CheckForEscape`. Calls expected to exceed about 5 s return a `job_id`.

## Scripting
| Tool | Engine |
|---|---|
| `run_python` | IronPython 2.7.12 via `Rhino.Runtime.PythonScript`. `result` comes back JSON-serialized and stdout is captured. |
| `run_csharp` | Roslyn 3.9 `CSharpScript` from the Mono 4.5 lib folder, with the Mono.CSharp evaluator as fallback. |
| `run_command` | `RhinoApp.RunScript(cmd, echo)` with captured command history. |
| Python 3 (M4) | Optional sidecar. numpy, shapely, trimesh, networkx, rhino3dm. |

Script tools are gated by `allowScripts` in `config.json` (default true for this research project).

## Repository layout
```
src/Core/          RhinoMCP.rhp: plugin, server, contracts, dispatcher, commands
src/Tools/         RhinoMCP.Tools.dll: one folder per tool family
skills/            Agent skills (SKILL.md + references/), also served as MCP resources
scripts/           build.sh · install.sh · mcp.sh (curl helper) · dev-test.sh (locked integration run)
tests/             integration tests (python3 stdlib, talk to the live server)
bridge/            Swift stdio↔HTTP bridge (M5)
docs/              research/ · ARCHITECTURE.md · PLAN.md · DEVELOPMENT.md · DECISIONS.md
```

## Security notes
- The server listens on loopback only, requires a token, and checks Host and Origin.
- Script tools execute arbitrary code by design. Keep the token private.
- `dev_reload` exists only when `devMode` is true.

## Interactive commands (learned on Rhino 7.39 Mac)
- **Commands waiting at a prompt** (GetPoint/GetOption) keep pumping UI callbacks. `run_command` arms a watchdog with `input_timeout_s` (default 10 s) that injects Escape (`!`). It returns `COMMAND_NEEDS_INPUT` with the exact `last_prompt` and its options, so the agent can retry with every input inline.
- **Background cancel:** `RhinoApp.SendKeystrokes` does nothing while Rhino is not the frontmost app. The watchdog therefore posts a native Escape key event into Rhino's own AppKit queue (`[NSApp postEvent:]` via the Objective-C runtime), which works in the background. It retries every 2 s, up to 4 times.
- **Modal windows** (`What`, `Options`, …, and non-dash `Export`/`Print`/`Hatch`/…) do *not* pump callbacks. They freeze Rhino until a person closes them. `run_command` refuses them up front with `COMMAND_BLOCKED`.
- **`_Undo` via `RunScript` is a no-op.** Use `history_undo` / `history_redo`, which call `RhinoDoc.Undo()` / `Redo()`.
- **A cancelled command can leave an undo record open.** While it is open, `Undo()` fails and later edits pile into it. The tools close a stray record (`CurrentUndoRecordSerialNumber`) when no command is running.
- **`CapturedCommandWindowStrings` is empty on Mac.** `run_command` falls back to diffing `CommandHistoryWindowText`.

## System.Drawing is unusable on Rhino 7 Mac
- libgdiplus is not usable in-process: `new Bitmap(w,h)` throws `TypeInitializationException` (GDIPlus), and half-built GDI+ objects with throwing finalizers can take Rhino down (Mono exits on an unhandled finalizer exception).
- `RhinoView.CaptureToBitmap`, `ViewCapture` and `ViewCaptureSettings` throw `MissingMethodException` from a hot-loaded tools DLL, and `ImageCodecInfo`/`EncoderParameters` do not exist, so there is no JPEG quality control. Do not reference `System.Drawing` types in tools.
- View tools instead drive the active viewport briefly (try/finally restore), run `-_ViewCaptureToFile` (options go **before** the quoted file name, otherwise it saves at once with the sticky defaults) to a temp PNG and read the bytes. `src/Tools/View/Png.cs` is a small managed PNG codec used to compose the 2x2 sheet. Output is PNG only.
- **Rhino keeps an undo record open outside commands.** While it is open, `BeginUndoRecord` returns 0 and edits merge into that shared record. Then `history_undo` reverts unrelated work. `UndoScope` therefore closes a stray record when no command is running, and only then opens its own.
- **`System.Drawing` duplicates.** Only one `System.Drawing` may be loaded: Rhino's own copy in `RhCore.framework/Resources/lib/mono/gac`. An `AssemblyResolve` handler that loads Mono's copy makes every Color-taking RhinoCommon call throw `MissingMethodException`. Resolvers must use an allow-list.

## Change journal and hot reload (`src/Tools/Model/ChangeJournal.cs`)
- The journal subscribes to `RhinoDoc` Add/Delete/Undelete/Replace/ModifyObjectAttributes and `LayerTableEvent`. Handlers never throw.
- **Tool DLLs are reloaded without unloading**, so event subscriptions of older DLLs stay alive. The journal therefore keeps its ring buffer (primitive arrays) and an "uninstall" `Action` in `AppDomain` data. A newer DLL adopts the buffer (tokens keep counting), runs the stored uninstall (unsubscribes the old delegates) and subscribes its own. Each handler also carries a generation number and returns at once if it is no longer the current one. Result: exactly one journal records each event.
- **Actor (agent vs user):** Core exposes no "agent call in progress" flag. Handlers run synchronously on the UI thread, so a `ToolRegistry` frame on the stack means an MCP tool (including `run_command` / `run_python`) caused the event. A Core hook (a static flag set around `tool.Execute`) would replace this stack walk.
- `objects_near` keeps an RTree over object bboxes, rebuilt when the journal epoch (bumped by add/delete/undelete/replace) or `Objects.Count` changes.
