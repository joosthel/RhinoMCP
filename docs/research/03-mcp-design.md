# 03 — MCP design for a large capability surface

> Source: Sonnet 5.5 research subagent, 2026-10-15. **[V]** = read primary source; **[U]** = unverified.

## 1. Spec essentials
- **Current spec: 2026-07-28** [V] ([versioning](https://modelcontextprotocol.io/specification/versioning)); previous 2025-11-25. 2026-07-28 goes stateless: no `initialize`, no `Mcp-Session-Id`, metadata in `_meta`, mandatory `server/discover`, list cache hints, multi-round-trip elicitation/sampling, Tasks and MCP Apps as extensions [U on details]. Clients will straddle both versions — choose an SDK that supports both.
- **Tools** [V]: names allow `.` → namespaced names like `objects.query` are valid. `outputSchema` + `structuredContent` (also send JSON as text). Image blocks (base64 + mimeType, `audience` annotation). `resource_link` in results. `tools/list_changed` for dynamic sets.
- **Errors** [V]: validation/business errors → `isError:true` tool result so the model self-corrects; protocol errors only for malformed requests.
- **Annotations**: `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint` — UX hints, not security.
- **Elicitation** [V]: use for destructive confirms; fallback `confirm_token` two-step since client support varies.
- **Tasks** (experimental) [V]: suits Make2D, big exports, renders. Fallback: `jobs.status/result/cancel`.
- **Transport** [V]: localhost HTTP must validate `Origin` (403) — DNS-rebinding risk. Bind 127.0.0.1, check Origin/Host, require bearer token.

## 2. Handling a huge API
- Anthropic, [Code execution with MCP](https://www.anthropic.com/engineering/code-execution-with-mcp) [V]: progressive disclosure / `search_tools`; ~150k → 2k tokens.
- Cloudflare, [Code Mode](https://blog.cloudflare.com/code-mode/) [V]: model writes code against tool bindings in an isolate.
- Anthropic, [Writing tools for agents](https://www.anthropic.com/engineering/writing-tools-for-agents) [V]: workflow-level tools, namespacing, `response_format` concise/detailed, pagination, actionable errors, unambiguous params (`object_id`).
- Claude Code defers tool definitions via tool search [U].

**Recommended three layers:**
1. **Core typed tools (~25–40, <8k tokens)**, workflow-level.
2. **Escape hatches:** `script.run_python` (+ optional `run_csharp`), `command.run` (any Rhino command via `RunScript`). This is the non-opinionated core — `run_python` already *is* code mode inside the app.
3. **Discovery:** `api.search(query, detail)` / `api.describe` built from reflection over loaded RhinoCommon (accurate per version), `rhino://cookbook/{topic}` resources.

Optional later: a `batch` tool with `$ref` to earlier results.

## 3. Visual feedback
- [Blender MCP](https://github.com/ahujasid/blender-mcp) [V]: consolidated screenshot into one flexible `look` tool (multi-angle sheets, display modes); compact one-line-per-object scene summary; socket addon + Python server; optional safe mode.
- Rhino proposals:
  - `view.capture` — view/named view/custom camera, display mode, size (~768–1024px), zoom target, `highlight`/`isolate` GUIDs; returns image + camera pose/scale text.
  - `view.capture_sheet` — labelled 2×2 Top/Front/Right/Perspective in one image.
  - Annotated overlays (IDs, scale bar, highlights) [U].
  - Numbers beat pixels: `objects.get_info`, `objects.query`, `measure.*`, `geometry.validate` (with `IsValidWithLog` reason).
  - `doc.get_summary`: units, tolerance, layer tree with counts, bbox, named views.

## 4. Safety & reliability
- One undo record per mutating call (`try/finally`); `undo`/`redo`/`history`; optional `txn.begin/commit/rollback`; snapshot before `run_python`.
- Single serialized work queue on the UI thread; per-call timeout; long ops as jobs. In-process scripts can't be force-killed.
- Code execution can't be truly sandboxed in-process. Offer modes (`safe`/`standard`/`unrestricted`) with honest docs; localhost + token; capture stdout/stderr/return with size caps.
- Tag truly irreversible ops (save-over, purge, close w/o save) as destructive.
- Teaching errors: "Layer 'Wall' not found. Existing: A-WALL… Use layers.list."
- GUIDs everywhere; every creation returns GUIDs + bbox; always report units/tolerance.

## 5. SDK & hosting
- **C# SDK** [V] ([repo](https://github.com/modelcontextprotocol/csharp-sdk), NuGet `ModelContextProtocol` 2.2.0, 2026-08-13): targets netstandard2.0/net8+/net9/net10; packages Core, hosting, AspNetCore, Extensions.Tasks/Apps. netstandard2.0 *could* load into Rhino 7 net48 — but `Microsoft.Extensions.*` 10.x may conflict with Rhino's shared AppDomain [U]. AspNetCore transport is awkward on net48 [U].
- TypeScript SDK: strongest ecosystem, npx/MCPB-friendly [U].
- Python: official `mcp` (FastMCP 1.0 inside) vs standalone `fastmcp` [U].
- Existing Rhino MCPs (snippet-level): [reer-ide/rhino_mcp](https://lobehub.com/mcp/reer-ide-rhino_mcp), [rhino-gh-mcp](https://pypi.org/project/rhino-gh-mcp/), [dongwoosuk/rhino-grasshopper-mcp](https://glama.ai/mcp/servers/dongwoosuk/rhino-grasshopper-mcp), [alfredatnycu/grasshopper-mcp](https://playbooks.com/mcp/alfredatnycu-grasshopper-parametric-design), [SerjoschDuering/rhino-mcp](https://github.com/SerjoschDuering/rhino-mcp). Mostly Python stdio → socket → run-Python; none typed, layered and vision-aware.

| | A. MCP server inside plugin (HTTP) | B. External stdio server + bridge |
|---|---|---|
| Install | one Yak package + URL | two parts, version-matched |
| Stdio-only clients | need shim [U] | native |
| Dependency risk | assembly conflicts in Rhino | isolated |
| Lifecycle | lives with Rhino | must report "Rhino not connected" |

Agent's recommendation: transport-agnostic core in the plugin, HTTP on loopback + tiny stdio shim; fall back to B if the net48 dependency test fails.

## 6. Distribution
- **MCPB** (ex-DXT): zip + `manifest.json`, server types node/python/uv/binary; `user_config` for port/token [U on versions].
- **Claude Code:** `claude mcp add --transport http rhino http://127.0.0.1:PORT/mcp` or `claude mcp add rhino -- uvx rhino-mcp` [U on flags].
- Copy Blender MCP's `uvx … setup` UX: one command writes client configs and installs the in-app half.
- **Yak** [V, [anatomy](https://developer.rhino3d.com/guides/yak/the-anatomy-of-a-package)]: Rhino 8 packages may contain `net48/` + `net7.0/` (8.2+); Mac uses net7.0 only. Rhino 7 needs its own net48 package. Add `_RhinoMCP` command (start/stop, show port/token, copy config), auto-start on load.

## Proposed tool taxonomy
- **Read** (`readOnlyHint`): `doc.get_summary`, `layers.list`, `objects.query`, `objects.get_info`, `views.list`, `view.capture`, `view.capture_sheet`, `measure.*`, `geometry.validate`, `selection.get`
- **Create/modify** (undoable): `geometry.create` (typed union), `geometry.transform`, `geometry.edit`, `layers.manage`, `objects.set_properties`, `blocks.manage`, optional thin `architecture.*` helpers
- **Drawings/export:** `drawing.make2d`, `layout.create_and_place_details`, `annotation.add_dimensions`, `drawing.export`, `jobs.*`
- **Power:** `script.run_python`, `script.run_csharp`, `command.run`, `api.search`, `api.describe`
- **Safety/state:** `undo.*`, `txn.*`, `file.save/open/snapshot`
- **Resources:** `rhino://doc/summary`, `rhino://layers`, `rhino://object/{guid}`, `rhino://view/{name}`, `rhino://cookbook/{topic}`, `rhino://api/{symbol}`
- **Prompts:** `design_floor_plan`, `generate_section_set`, `export_drawing_package`, `fix_invalid_geometry`, `explain_scene`

Rules: `response_format` + pagination everywhere; mutating tools return GUIDs + bbox + optional preview; descriptions state units, Z-up, world vs CPlane, version differences.

## Verify before committing
1. C# MCP SDK Core hosting HTTP inside net48 Rhino 7, and assembly conflicts.
2. 2026-07-28 spec details and client support.
3. Claude Desktop support for local HTTP vs stdio/MCPB.
4. MCPB manifest version.
5. Rhino 7 C# scripting (Roslyn) availability.
6. Existing Rhino MCPs' licenses and maintenance.
