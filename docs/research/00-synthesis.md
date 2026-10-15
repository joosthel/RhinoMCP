# 00 — Research synthesis & recommended architecture (v2)

2026-10-15. Combines reports [01](01-existing-mcp-servers.md)–[08](08-one-click-install.md) and the owner's [decisions](../DECISIONS.md).

## Bottom line
- Build a **C# Rhino plugin, Rhino 7 first** (net48, with a net8.0 build for Rhino 8). It does all the Rhino work behind an authenticated loopback HTTP endpoint.
- Pair it with a **tiny signed native stdio↔HTTP bridge**, shipped inside the Yak package and as a Claude Desktop `.mcpb`. Users install nothing else: no Python, Node or uv.
- Tools are a hybrid:
  - ~30 typed, token-lean tools;
  - `run_python` / `run_command` escape hatches;
  - a first-class **clean drawing** pipeline;
  - **skills** generated from one markdown source.

## What changed since v1
| v1 | v2 | Why |
|---|---|---|
| External Python FastMCP server | Native bridge (.NET NativeAOT or Go) | One-click install; Python/uv is the top install complaint ([08](08-one-click-install.md)) |
| — | `.mcpb` for Claude Desktop | Claude Desktop can't reach localhost HTTP; needs stdio ([08](08-one-click-install.md)) |
| Generic `drawing.make2d` | `drawing.make_clean` pipeline with quality report | Owner priority; R7 `HiddenLineDrawing` exposes everything needed ([05](05-clean-make2d.md)) |
| Prompts/cookbooks | Skills plugin + MCP resources/prompts from one source | Owner priority ([06](06-agent-skills.md)) |
| — | Patterns borrowed from McNeel RhinoAI | Discovery files, placeholder install tool, typed error+guidance ([07](07-rhinoai-reference.md)) |

## Architecture
```
Claude Desktop ──(.mcpb, binary)──┐
Claude Code / Cursor / Codex ─────┤ stdio            (or direct HTTP for clients that support it)
                                  ▼
                       rhinomcp-bridge  (native, signed; osx-arm64/x64, win-x64)
                          │ reads instances/<pid>.json → port + token
                          │ HTTP 127.0.0.1, Bearer token, Host/Origin checks
                          ▼
                RhinoMCP.rhp  (net48 → Rhino 7 Mono/Win  ·  net8.0 → Rhino 8)
                  ├─ HttpListener (SDK-free JSON-RPC, like RhinoAI)
                  ├─ UI-thread dispatcher (InvokeOnUiThread + timeout; Idle for background work)
                  ├─ one undo record per mutating call
                  ├─ typed handlers · run_python (IronPython 2.7 on R7) · run_command
                  ├─ drawing pipeline (HiddenLineDrawing → cleanup → layers/weights → hatch)
                  └─ guidance resources (skills content)
```
If the plugin is missing, the bridge serves an `install_rhino_mcp` tool. If Rhino is closed, it replies "Open Rhino".

## Tool surface (v1 ≈ 30 tools)
| Namespace | Tools | Notes |
|---|---|---|
| `context` | `get` | one call: version, units, tolerance, layer tree with counts, selection, bbox, named views |
| `objects` | `query`, `get_info`, `set_properties`, `delete`, `select` | filters + pagination, counts before lists, GUIDs |
| `geometry` | `create`, `transform`, `edit`, `validate` | typed unions; returns GUIDs + bbox |
| `measure` | `area_volume`, `distance`, `bbox` | numbers over pixels |
| `layers` | `list`, `apply_scheme`, `audit`, `manage` | AI organization: AIA/NCS, ISO 13567, DE house styles, custom |
| `data` | user-text get/set/bulk, `blocks` | informal BIM |
| `view` | `capture` | **only image tool**: low-res default, metadata, restores the view afterwards |
| `drawing` | `make_clean`, `clipping_plane`, `layout`, `annotate` | numeric quality report |
| `file` | `open`, `save`, `import`, `export` | DWG/DXF/PDF/SVG/AI/3DM/STEP/STL |
| `script` / `command` | `run_python`, `run_command`, `commands.search` | structured stdout/return/traceback |
| `history` | `undo`, `redo` | one record per call |
| `guidance` | `get(topic)` | skills content for non-Claude clients |

## Skills (ship as Claude Code plugin + MCP resources)
`rhino-mcp-efficient-use` (hub) · `rhino-units-tolerances` · `rhino-geometry-hygiene` · `rhino-layer-standards` · `rhino-drawing-production` · `rhino-architectural-massing` · later `rhino-fabrication-export`.

## Differentiators vs McNeel RhinoAI / rhinomcp
Rhino 7 and Intel Mac support · authenticated loopback · undo grouping · timeouts · capture that doesn't change the view · clean drawing pipeline · AI layer standards · skills · a single installer.

## Phase 0 spike (on Rhino 7.39, this Mac)
1. Build a net48 `.rhp` (RhinoCommon 7 NuGet). Confirm it loads under Mono x86_64.
2. Run `HttpListener` on 127.0.0.1 under Mono. Fall back to raw TCP if it fails.
3. Round-trip through the UI thread: add a box inside an undo record, capture the viewport, run IronPython and return a value.
4. `HiddenLineDrawing` on a test model, then clean and count.
5. Prototype the bridge (dotnet NativeAOT osx-arm64) and connect from Claude Code.
6. Install the Yak `rh7_0-mac` package and check that AtStartup works after a restart.

## Open items
- Apple Developer ID and Windows signing certificate (needed for the bridge).
- Rhino 8 testing machine or trial (optional add-on).
- Verify the standards tables in the skills against the primary codes.
