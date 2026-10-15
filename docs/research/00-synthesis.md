# 00 — Research synthesis & recommended architecture

2026-10-15. Combines reports [01](01-existing-mcp-servers.md)–[04](04-designer-workflows.md).

## Bottom line
Build a **C# Rhino plugin (net48 + net8.0) that does all Rhino work**, plus a **thin external MCP server** that speaks MCP to clients and forwards over an authenticated localhost connection. Expose a **hybrid tool surface**: ~30 typed tools for querying, viewing, organizing and exporting; `run_python` + `run_command` escape hatches for everything else; API search for discovery. Wrap every call in one undo record.

## Why
- **Rhino 7 on Mac rules out everything except an in-app plugin.** No `rhinocode`, no AppleScript dictionary, no Rhino.Inside, no Compute. Rhino 7 is Intel-only here, running under Rosetta, on Mono/.NET Framework 4.8.
- **Key APIs exist in the installed Rhino 7.38:** `InvokeOnUiThread`, `BeginUndoRecord`, `ViewCapture`, `HiddenLineDrawing` (Make2D), `FilePdf`, `AddPageView`/`AddDetailView`/`SetScale`, `AddClippingPlane`. Checked in the local `RhinoCommon.xml`.
- **The market gap is real.** McNeel's official [RhinoAI](https://github.com/mcneel/RhinoAI) (MIT) targets 8/9 and Apple Silicon. [rhinomcp](https://github.com/jingcheng-chen/rhinomcp) (MIT) is 8-only. Cordyceps needs 8.21+. None supports Rhino 7 cleanly, groups undo per call, or treats drawing production as first-class.
- **Hybrid tools are proven.** Blender MCP shows that a few tools plus code scale. Anthropic's tool-design guidance favours workflow-level, namespaced, paginated tools with actionable errors.

## Architecture

```
MCP client (Claude Code / Desktop / Cursor)
        │ stdio  (or Streamable HTTP later)
        ▼
rhino-mcp server  (Python, FastMCP; uvx-installable)
        │ HTTP/JSON on 127.0.0.1, bearer token, Origin check
        ▼
RhinoMCP.rhp  (C#, net48 for R7 / net8.0 for R8)
   ├─ request queue → RhinoApp.InvokeOnUiThread (timeout)
   ├─ undo record per request
   ├─ typed handlers (query, view, layers, geometry, drawings, export)
   ├─ run_python (IronPython 2.7 on R7; CPython 3 or IronPython on R8)
   ├─ run_command (RhinoApp.RunScript + captured command history)
   └─ api.search (reflection over loaded RhinoCommon)
```

**Why the server is external, not embedded:**
- Rhino 7 is net48 under Mono with a shared AppDomain. The C# MCP SDK depends on `Microsoft.Extensions.*` 10.x, which risks assembly conflicts.
- The external server keeps MCP-spec churn (2025-11-25 → 2026-07-28 stateless) out of the plugin.
- Keep the plugin's HTTP API clean. Embedding an MCP endpoint directly in the R8 build can come later.

## Tool taxonomy (v1 target ≈ 30 tools)

| Namespace | Tools | Notes |
|---|---|---|
| `doc` | `get_summary`, `get_info` | units, tolerance, layer tree with counts, bbox, named views, Rhino version |
| `objects` | `query`, `get_info`, `set_properties`, `delete`, `select` | filters by layer/type/name/user text/bbox; pagination; GUIDs |
| `geometry` | `create`, `transform`, `edit`, `validate` | typed unions: primitives, curves, surfaces, booleans, fillets, arrays |
| `measure` | `area_volume`, `distance`, `bbox` | numbers over pixels |
| `layers` / `blocks` / `data` | `manage`, `list`, user-text get/set/bulk | informal BIM layer |
| `view` | `capture`, `capture_sheet`, `set_camera`, `named_views` | image content; display mode; highlight/isolate |
| `drawing` | `make2d`, `clipping_plane`, `layout`, `add_details`, `annotate` | R8 section styles behind a version check |
| `file` | `open`, `save`, `import`, `export` | PDF, DWG, DXF, SVG, 3DM, STEP, OBJ, STL, IFC (R8) |
| `script` / `command` | `run_python`, `run_command` | stdout, return value, trimmed traceback |
| `api` | `search`, `describe` | per-version accuracy |
| `history` | `undo`, `redo` | one record per call |
| `gh` (phase 2) | open, list params, set inputs, solve, bake | GH_Document API |

Architectural workflows (massing, floor slicing, area schedules, sheet sets) ship as **MCP prompts and resources (cookbooks)**, not tools.

## Cross-cutting rules
- Every mutating call creates one undo record and returns the affected GUIDs, a bounding box and units.
- Errors use `isError: true` with the reason, the valid options and a suggested next call.
- `response_format: concise|detailed`, pagination, and response-size caps on every call.
- The server binds to localhost only and needs a token. Code execution can be toggled. An audit log records calls.
- Read-only tools carry `readOnlyHint`. Irreversible tools carry `destructiveHint`.

## Phase 0 spike (do first, on this Mac)
1. Build a minimal net48 `.rhp` that loads in Rhino 7.38 (Mono, x86_64).
2. Run an `HttpListener` on 127.0.0.1 inside Rhino 7 Mac, then marshal a call to the UI thread and back.
3. Add a box inside an undo record, capture the viewport to PNG, and run an IronPython snippet that returns a value.
4. Run Make2D through `HiddenLineDrawing` and export a PDF with `FilePdf`.
5. Connect a FastMCP stdio server to it from Claude Code.

If step 2 fails under Mono, fall back to a raw TCP socket with length-prefixed JSON.

## Open decisions for the owner
- **Build our own, or contribute to McNeel's RhinoAI (MIT)?** Our differentiators are Rhino 7 support, undo grouping, drawing production and the query depth.
- **External server language:** Python (FastMCP, uvx) is recommended. TypeScript is the alternative.
- **Rhino 8 access for testing:** none is installed locally.
