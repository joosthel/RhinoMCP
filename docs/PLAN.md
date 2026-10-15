# Plan

The goal is a research prototype. It should find out how far an MCP server can drive **Rhino 7 on an Intel/Rosetta Mac** for real design work. Signing, installers and Windows are out of scope unless they become cheap.

Each milestone is broken into tasks. Every task gets its own branch (`feat/<task>`) and PR, which is merged once its build and tests pass. Tasks marked ∥ run in parallel as Sonnet subagents in separate worktrees.

## M0 — Foundation *(sequential)*
| Task | Done when |
|---|---|
| `feat/core-skeleton` | `scripts/build.sh` builds `dist/RhinoMCP.rhp` and the tools DLL with bundled Mono and csc. The plugin loads in Rhino 7.39 Mac, the server answers `/health` and `initialize`, and auth and the Host/Origin checks work. Commands `MCPStart`, `MCPStop`, `MCPStatus` and `MCPConnect` work, and `MCPConnect` prints the `claude mcp add` line. |
| | The ITool registry, UiDispatcher, UndoScope, ArgReader, Handles, ToolResult and `dev_reload` exist. Two tools ship: `context_get` and `run_command`. `scripts/mcp.sh` and `scripts/dev-test.sh` exist, plus `tests/test_core.py`. |
| *(owner, once)* | Install the plugin by dragging `dist/RhinoMCP.rhp` into Rhino, then run `claude mcp add`. |

## M1 — Core tools ∥
| Task | Tools |
|---|---|
| `feat/tools-objects` | `objects_query` (filters by layer, type, name, user text and bbox; group-by aggregates; cursor) · `objects_get` · `objects_set_props` · `objects_delete` · `objects_select` · `sets_save` / `sets_list` / `sets_delete` |
| `feat/tools-layers` | `layers_list` (tree with counts) · `layers_manage` (create, rename, move, color, linetype, print width, lock, visibility) · `layers_audit` · `layers_apply_scheme` (AIA/NCS, ISO 13567, DE, custom JSON) |
| `feat/tools-geometry` | `geometry_create` (point, line, polyline, curve, arc, circle, rectangle, box, sphere, cylinder, cone, plane surface, extrusion, loft, sweep, revolve, pipe, text) · `geometry_transform` (move, copy, rotate, scale, mirror, orient, array linear/polar/grid) · `geometry_boolean` (pre-flight, retry ladder, diagnosis, rollback) · `geometry_edit` (join, explode, offset, fillet, shell, cap, split, trim) · `geometry_validate` |
| `feat/tools-measure` | `measure` (length, area, volume, centroid, bbox, by object or set, with group-by) · `measure_distance` · `clash_check` (RTree broad phase + BrepBrep) |
| `feat/tools-view` | `view_capture` (view or named view, display mode, size, `zoom_to`, highlight; the view is restored afterwards; returns metadata + PNG/JPEG) · `view_set` · `views_named` |
| `feat/tools-script` | `run_python` (IronPython, JSON result, stdout) · `run_csharp` (Roslyn) · `commands_search` · `history_undo` / `history_redo` |

## M2 — Clean drawings ∥
| Task | Tools |
|---|---|
| `feat/drawing-clean` | `drawing_make2d` (HiddenLineDrawing → filter → simplify → join → de-overlap → layers and ISO weights → section hatches → scale and placement; returns a numeric quality report) · `drawing_clipping_plane` |
| `feat/drawing-layout` | `layout_create` (paper size, details at scale, title-block fields) · `export` (PDF via `FilePdf`, DWG/DXF/SVG/AI/3DM/STEP/STL via scripted `-_Export`) · `annotate` (dims, text, hatch) |

## M3 — Scale & complex projects ∥
| Task | Tools |
|---|---|
| `feat/model-overview` | `model_overview` (bounded hierarchical summary) · change journal + `model_changes(since)` · `verify_audit` |
| `feat/jobs` | job system (Idle chunking, Escape, progress) · `jobs_status` / `jobs_cancel` · `variants_run` (headless docs + comparison table) |
| `feat/arch-ops` | `arch_wall`, `arch_slab`, `arch_opening`, `arch_stair`, `arch_levels`, `arch_facade_grid`, `area_schedule` |
| `feat/product-ops` | `product_draft_check`, `product_continuity`, `product_thickness`, `product_fillet_max` |
| `feat/recipes` | `recipe_save` / `recipe_run`: replayable intent operations tagged `ai.recipe_id` |

## M4 — Extensions ∥
| Task | Scope |
|---|---|
| `feat/grasshopper` | `gh_list_definitions`, `gh_inspect`, `gh_solve` (`in:`/`out:` nickname convention, silent solve, bake with attributes) · `gh_call_component` (NodeInCode) |
| `feat/python3-sidecar` | opt-in uv-managed CPython 3 (arm64) · `run_python3` with rhino3dm geometry exchange |
| `feat/skills` | the skills from research/06 as `skills/*/SKILL.md`, served as MCP resources and prompts; a Claude Code plugin manifest |

## M5 — Polish *(optional)*
- Swift stdio bridge and `.mcpb` for Claude Desktop.
- Yak package `rh7_0-mac`.
- An evaluation suite: real tasks scored by numeric checks.
- A findings report: "how far can it go".

## Working rules for subagents
- Read `docs/ARCHITECTURE.md` and `docs/DEVELOPMENT.md` first. Tools follow the contract and conventions exactly.
- Add files only under your own `src/Tools/<Family>/` and `tests/test_<family>.py`. Touching `src/Core` requires a note in the PR.
- `scripts/build.sh` must pass. Run `scripts/dev-test.sh tests/test_<family>.py`, which takes a lock, hot-reloads your build and runs the tests against the live Rhino.
- Tests create their objects on a layer named `_mcp_test::<family>` and delete them afterwards.
- Open a PR with a summary and the test output. The orchestrator merges.
