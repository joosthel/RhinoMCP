# 07 — McNeel RhinoAI: reference teardown

> Source: Sonnet 5.5 subagent read a shallow clone of [mcneel/RhinoAI](https://github.com/mcneel/RhinoAI) (branch `rhino-9.x`, HEAD 27a8d64, MIT). Read-only; nothing was built or run. 2026-10-15.

## Bottom line
**Copy RhinoAI's architecture. Rewrite the plugin for Rhino 7.** RhinoAI is a two-hop design:
1. An external stdio "router" (.NET 8 NativeAOT, official C# MCP SDK) spawns and discovers Rhino.
2. The router forwards each call over loopback HTTP to a hand-written JSON-RPC listener inside the plugin.

McNeel's docs state that .NET Framework and Intel Macs are not supported, so it cannot run on Rhino 7. Its in-plugin server uses no SDK (plain `HttpListener`), so the pattern itself ports cleanly to net48/Mono.

## Architecture
- **Plugin** `rhino/plugin/RhinoAI.csproj`:
  - Targets `net8.0;net48`, with net48 built only on Windows. RhinoCommon 8.19 / 9.0 WIP.
  - Server is `McpServer.ai.cs` plus `Server/*` on `System.Net.HttpListener`, POST only, no SSE.
  - Port 10500 and up, **one listener per RhinoDoc**.
  - **No auth, no Origin check.**
- **Router** `rhino/router` (`rhino-mcp-router`):
  - net8.0, `ModelContextProtocol` 1.2.0, stdio transport, SQLite slot table.
  - NativeAOT builds: osx-arm64, win-x64, win-arm64.
  - Tools: `spawn_slot`, `close_slot`, `list_slots`. Calls auto-spawn or reuse Rhino.
  - Errors: `rhino_crashed` (with crash-report path), `rhino_closed`, `wrong_rhino_version`, …
- **Codegen:** a Roslyn source generator emits router proxies from the plugin's `[McpServerTool]` methods, so the router never drifts from the plugin's tool list.
- **Discovery:**
  - The plugin writes `<temp>/rhino-mcp-listeners/<pid>-<port>.json`.
  - It re-announces every 15 s from `RhinoApp.Idle`.
  - It writes a `.gone` tombstone on close, which separates a user close from a crash.
- **Launcher:** `shared/router-launcher.mjs` (Node) finds the router inside the Yak package and copies it to a stable per-user directory before running it.
  - If the plugin is missing, it serves a **placeholder MCP server** with an `install_rhino_mcp_platform` tool that runs `yak install`.
- **Clients:**
  - `connector/` is the Claude Desktop `.mcpb` (node type).
  - `cc-plugin/` is the Claude Code plugin (`.mcp.json` → node launcher).
  - `.claude-plugin/marketplace.json` is the marketplace entry.
  - The `MCPConnect` dialog writes configs for Claude Code, Cursor, Gemini, Windsurf, OpenCode and Codex, but only where the config already exists, and never overwrites one.
- Out of scope for us: `rhino/sdk`, `rhino/acp`, `rhino/panel` (in-Rhino chat panel).

## Tools
- **Core:**
  - `get_context` (one-shot orientation)
  - `list_objects`, `get_selection`, `set_selection`
  - `get_commands`, `run_command`
  - `run_python` (Python 3), `run_csharp`
  - `get_viewport_image`, `set_camera`, `zoom_to_object`, `zoom_to_layer`
  - `set_layer_material`
  - `open_doc`, `save_doc`, `close_doc`
  - `manage_plugin_commands` (R9)
  - `ask_user` (panel only)
- **Grasshopper:** 13 `g1_*` tools (GH1) and 13 `g2_*` tools (GH2, R9).
- **Resources:**
  - `rhino://commands/{name}/help`
  - `rhino://host/environment`
  - `rhino://host/plugins`
- **Declaration and arguments:**
  - In-house `[McpServerTool(readOnly, destructive)]` attributes, discovered by reflection, with schemas built from method parameters.
  - Lenient argument binding. Coercions and ignored arguments are reported back, and all bad arguments come back in one reply.
- **Results:**
  - `IToolResult` = `ToolError` code + `Message` + `Guidance` + content blocks.
  - JSON goes in a text block. There is no `structuredContent` or `outputSchema`.
  - `isError` marks a call that ran and failed.
- **Viewport capture:**
  - `CaptureToBitmap`, JPEG, 480×270 by default, clamped to 1280×720.
  - Returns a metadata block (camera, scene bbox, visible/total counts) plus the image.
  - **It mutates the live view and never restores it.**

## Threading / undo / timeouts
- Every tool runs on the UI thread through `InvokeOnUiThread` + `TaskCompletionSource`. `[BackgroundThread]` opts a tool out.
- Heartbeat uses `RhinoApp.Idle`, not timers: on macOS, timers starved the thread pool.
- **No undo record for external MCP calls.** Only the in-panel agent turn gets one.
- **No per-tool timeout or cancellation in the plugin.** The router's HTTP timeout is 5 min, so a hung script blocks Rhino's UI.
- `run_python`/`run_csharp` drive `-_ScriptEditor _Run` and detect errors by matching "Traceback" or "error CS" in command-window text. This is brittle.

## Rhino 7 blockers (why we can't just fork it)
1. Version maps, the launcher and the connector know only Rhino 8, 9 and WIP.
2. The router has no `osx-x64` RID, and the launcher hard-codes osx-arm64.
3. The Mac plugin is net8.0 only. No Mono/net48 build exists for Mac.
4. net48 dependencies: System.Text.Json 8, Channels, PolySharp, and a 241-line polyfill file.
5. `run_python` requires Python 3 and the R8 ScriptEditor. Rhino 7 has IronPython 2.7.
6. GH2 is R9 only. The GH1 tools are mostly portable.
7. Several RhinoCommon calls need checking against R7. These include `CommandWindowCaptureEnabled`, `RunScript(docSerial,…)` and `PanelType.PerDoc`.
8. Multi-slot spawning relies on Rhino 8 Mac document behaviour.

## Borrow
- An external stdio process that stays connected across Rhino restarts and reports crashes.
- File-drop discovery with heartbeat and tombstone.
- A placeholder server that offers installation when the plugin is missing.
- Launcher staging to a stable per-user binary path.
- UI-thread by default, Idle-based background work.
- Typed result: error code + message + guidance. Report coercions; batch all argument errors into one reply.
- The viewport tool returns metadata alongside the image. Frame by bounding box, not camera maths.
- `get_context` as the first call; `get_commands` + a command-help resource.
- `readOnlyHint`/`destructiveHint` annotations (the Claude connector needs them).
- Generate the bridge's tool list from the plugin's declarations.

## Avoid (our differentiators)
- **Unauthenticated loopback.** Add a token in the discovery file plus Host/Origin validation.
- **No timeouts or cancellation.** Add per-call timeouts and Idle-sliced long jobs.
- **No undo grouping.** Use one undo record per mutating call.
- **Capture that mutates the view.** Save and restore the view, or capture off-screen. Support named views and PNG.
- Global command-window capture and parsing text for errors. Return structured results from our own Python runner instead.
- Stale docs and agent allow-lists naming tools that don't exist. Add a test that diffs them against the registry.
- Releases split across connector, plugin and Yak. Ship one installer.
- Heavy native stack (SQLite, NativeAOT, Node prerequisite). Keep the bridge tiny.
- Multi-slot complexity. Stay single-document unless we need more.
