# Tool map by intent

Source of truth: `docs/PLAN.md`. Milestone tags: M1 core, M2 drawings, M3 scale and architecture/product ops, M4 extensions. Tools from M2 onward are "if available": call `tools/list` once and check before planning around them. If a tool is missing, fall back to the listed alternative.

## Orient and understand

| Intent | Tool | Notes / fallback |
|---|---|---|
| Document facts (units, tolerance, counts) | `context_get` | Call first, once. |
| Layer tree with counts | `layers_list` | Counts before objects. |
| What is in the model | `objects_query` with `group_by` | Counts and aggregates before lists. |
| Hierarchical summary of a big model | `model_overview` (M3, if available) | Fallback: `layers_list` + `objects_query` group-by. |
| What changed since last look | `model_changes(since)` (M3, if available) | Fallback: compare `state_token`/counts. |
| Details of a few objects | `objects_get` | By handle. |

## Select, group, address

| Intent | Tool |
|---|---|
| Find objects by layer/type/name/user text/bbox | `objects_query` |
| Select in the Rhino UI for the user | `objects_select` |
| Name a result for later | `sets_save`; then `{set: "name"}` |
| List / delete sets | `sets_list`, `sets_delete` |

## Create and edit

| Intent | Tool |
|---|---|
| Points, lines, curves, boxes, extrusions, lofts, sweeps, text | `geometry_create` |
| Move, copy, rotate, scale, mirror, orient, arrays | `geometry_transform` |
| Union, difference, intersection | `geometry_boolean` (pre-flight and retry ladder built in) |
| Join, explode, offset, fillet, shell, cap, split, trim | `geometry_edit` |
| Closed smooth curve through control points | `geometry_create` curve `{mode: control_points, closed: true}` (periodic) |
| Curve with end tangents | `geometry_create` curve `{start_tangent, end_tangent}` (interpolate) |
| Straight span + G1/G2 blend + smooth curve | `geometry_create` `line_blend {from, to, points, continuity}` (exact LineCurve first segment) |
| Round the corners of a polyline | `geometry_edit` `fillet_corners {radius}` (reports arc radii, kinks) |
| Text as closed outline curves (letters, counters grouped) | `geometry_create` text `{as: "curves", center?}` -> `glyphs[{outer, holes}]`, `area_total` |
| Engrave / emboss text or closed curves into a solid | `geometry_edit` `engrave` / `emboss {text, text_height, depth, face?/at?/plane?}` (counters of O/A/R kept; reports removed/added volume) |
| Extrude in a stated direction | `geometry_create` extrusion `{direction (default +Z), both_sides, start_offset}`; the used direction is reported |
| Split/trim and keep one piece | `geometry_edit` split/trim `{keep: inside\|outside\|largest\|smallest\|containing_point\|nearest_point, point}` |
| Project / pull a curve onto a surface | `geometry_edit` `project_curve {onto, direction}` / `pull_curve {onto}` (reports max_deviation) |
| Freeform NURBS: control-point / through-point grids, loft (rebuild/refit), sweep2, network, patch, edge surface, G1/G2 blend, curve blend | `freeform_surface` |
| SubD: from box/cylinder/mesh/loft/surface, subdivide, crease, offset, to NURBS brep | `subd` |
| Rename, relayer, colour, user text | `objects_set_props` |
| Delete | `objects_delete` (destructive) |
| Layers: create, rename, colour, linetype, weight, lock | `layers_manage` |
| Apply a layer scheme | `layers_apply_scheme` (see `rhino-layer-standards`) |
| Replayable operation sequences | `recipe_save`, `recipe_run` (M3, if available) |

## Measure and verify

| Intent | Tool |
|---|---|
| Length, area, volume, centroid, bbox (by set, group-by) | `measure` |
| Distances between objects | `measure_distance` |
| Interference | `clash_check` |
| Is it valid / closed / manifold | `geometry_validate` |
| G0/G1/G2 between two surfaces or curves (gap, normal angle, curvature difference) | `analyze_continuity` |
| Curvature extremes, degree, control points, kinks, edge list of a surface | `analyze_surface` |
| Whole-model audit after edits | `verify_audit` (M3, if available) |
| Layer conformance | `layers_audit` |
| Area schedule, GFA | `area_schedule` (M3, if available) |

## Look

| Intent | Tool |
|---|---|
| Screenshot | `view_capture` (small size, one view) |
| Change active view, projection, display mode | `view_set` |
| Named views | `views_named` |

## Drawings and output (M2, if available)

| Intent | Tool | Fallback |
|---|---|---|
| Clipping plane for a section | `drawing_clipping_plane` | `run_command` `ClippingPlane` |
| Clean 2D drawing from 3D | `drawing_make2d` | `run_command` `Make2D` (unclean, needs manual cleanup) |
| Paper layout with details at scale | `layout_create` | `run_command` `Layout`, `Detail` |
| Dimensions, text, hatch | `annotate` | `geometry_create` text; `run_command` |
| PDF, DWG, DXF, SVG, STEP, STL export | `export` | `run_command` `-_Export` |

## Architecture and product ops (M3, if available)

`arch_levels`, `arch_wall`, `arch_slab`, `arch_opening`, `arch_stair`, `arch_facade_grid`, `area_schedule`; `product_draft_check`, `product_continuity`, `product_thickness`, `product_fillet_max`. Fallback: `geometry_*` plus `run_python` snippets from the domain skills.

## Long work (M3, if available)

`jobs_status`, `jobs_cancel`, `variants_run`. A call expected to exceed about 5 s returns a `job_id`: poll `jobs_status`, do not resubmit.

## Extensions (M4, if available)

`gh_list_definitions`, `gh_inspect`, `gh_solve`, `gh_call_component`; `run_python3` (sidecar with numpy, shapely, trimesh).

## Escape hatches

| Tool | Use for |
|---|---|
| `run_command` | A Rhino command with options, e.g. `-_Purge`. Use `-_` prefix and explicit options so no dialog opens. |
| `run_python` | IronPython 2.7: RhinoCommon snippets. Return a compact `result`. |
| `run_csharp` | Roslyn C# for heavy loops. |
| `commands_search` | Find a Rhino command name by keyword. |
| `history_undo` / `history_redo` | Revert or restore MCP call records. |
