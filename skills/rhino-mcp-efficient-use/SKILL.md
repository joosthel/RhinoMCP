---
name: rhino-mcp-efficient-use
description: Hub skill for working in Rhino 7 through the RhinoMCP server. Use at the start of any Rhino task, or when asked to model, edit, query, measure, select, undo, fix a failed boolean, or take a viewport screenshot. Covers the overview, query, act, verify loop and token rules.
---

# Efficient use of RhinoMCP

Rule of thumb: cheap before expensive. Counts before lists, numbers before pictures, one batched call before many small ones.

## 1. Session start

1. Call `context_get` once. It returns document name, units, absolute tolerance, angle tolerance, layer count, object count, and the active view. Remember the units and tolerance.
2. If units or tolerance look wrong for the task, stop and read `rhino-units-tolerances` before modelling. Never change tolerance silently.
3. On a non-trivial model call `model_overview` (if available, check `tools/list`) before `objects_query`.

## 2. The loop

1. **Overview.** Counts by layer and type (`layers_list`, `objects_query` with `group_by`). No object lists yet.
2. **Query by set.** Narrow with filters (layer, type, name, user text, bbox). Save the result with `sets_save` so later calls use `{set: "walls_L2"}` instead of ids.
3. **Act, batched.** One mutating call per logical step, targeting a handle list or a set. Prefer one call on 200 objects over 200 calls.
4. **Verify numerically.** `measure`, `geometry_validate`, `clash_check`, `objects_query` counts. Compare with what you expected ("expected 12.0 m3, got 11.97").
5. **Look only if the check is visual.** `view_capture` at small size (for example 640 px wide), one view, with `zoom_to` on the changed set. Never use screenshots to count or measure.

## 3. Token rules

- Ask for counts and aggregates first. Lists default to 50 rows: read `total`, `returned`, `next_cursor`, and page only when you need to.
- Use handles (`o17`). Do not request `include_guids` unless an external tool needs GUIDs. Never paste GUID lists back into calls; use `{set:}`.
- Request only the fields you need. Do not re-list after every mutation: mutating tools already return the handles they touched and the result bbox.
- Do not call `view_capture` after every step. One capture per milestone.
- Handles are per session. A `NOT_FOUND` on a handle means it is stale: re-query by set or filter.

## 4. Undo discipline

- Every mutating call is exactly one undo record named `MCP: <tool>`. `history_undo` reverts the last record; pass a count to revert several.
- Before a risky or large operation, note the `state_token` of the last result. After a bad result, `history_undo` once and re-verify, instead of patching on top of damage.
- `run_python` / `run_csharp` / `run_command` are also one record each, but a command may create several internal records. After scripting, check with `history_undo` count 1 and re-verify if unsure.
- Tell the user what you changed in terms of counts and handles, not raw lists.

## 5. Failure playbook

| Symptom | First cheap step | Then |
|---|---|---|
| `BOOLEAN_FAILED` | `geometry_validate` on each input (closed, manifold, normals) | Fix hygiene (see `rhino-geometry-hygiene`); check distance from origin and tolerance (see `rhino-units-tolerances`); try one cutter at a time; avoid coplanar faces by nudging 2x tolerance |
| Empty selection / `total: 0` | Re-run the query without the last filter; check layer path spelling with `layers_list` | Check hidden/locked layers and block contents |
| `NOT_FOUND` | Handle is stale | Re-query by set or filter |
| `TIMEOUT` | The call exceeded the UI-thread limit (60 s). Do not retry blindly | Reduce scope (smaller set, chunk), or use jobs (`jobs_status`, `jobs_cancel`, M3 if available) |
| `INVALID_ARGS` | Read the message: it lists all argument errors at once | Fix every listed error in one retry |
| `GEOMETRY_FAILED` | Read `guidance` and `details` | Validate inputs; simplify; adjust only with the user's consent |
| `SCRIPT_ERROR` | Read the traceback line | IronPython 2.7: no f-strings, `print` as function needs `from __future__`, use `%` formatting |

Never report success on an unverified geometry result. Never retry the identical failing call more than once.

## 6. Tool map (summary)

Intent to tool, abbreviated. Full table in [references/tool-map.md](references/tool-map.md). Tools marked M2+ exist only if the server build includes them: check `tools/list`.

- Orient: `context_get`, `layers_list`, `objects_query`, `model_overview` (M3).
- Select / group: `objects_select`, `sets_save`, `sets_list`, `sets_delete`.
- Create / edit: `geometry_create`, `geometry_transform`, `geometry_boolean`, `geometry_edit`, `objects_set_props`, `objects_delete`.
- Measure / check: `measure`, `measure_distance`, `clash_check`, `geometry_validate`, `verify_audit` (M3).
- Look: `view_capture`, `view_set`, `views_named`.
- Escape hatches: `run_python`, `run_csharp`, `run_command`, `commands_search`.
- Undo: `history_undo`, `history_redo`.

## 7. Related skills

- Units, tolerance, import problems: `rhino-units-tolerances`.
- Failed booleans, bad objects, mesh export: `rhino-geometry-hygiene`.
- Layer naming and audits: `rhino-layer-standards`.
- 2D drawings and sheets: `rhino-drawing-production`.
- Buildings: `rhino-architectural-massing`. Products: `rhino-product-design`.

Prefer a dedicated tool over `run_python`. Use scripts only when no tool fits, and keep snippets short, with a numeric `result`.

## Rhino commands via run_command

- Use the scripted dash form and pass every input inline in one string, ending with `_Enter`: `-_Box 0,0,0 10,10,0 5`, `-_Layer _New Walls _Enter`.
- If the result is `COMMAND_NEEDS_INPUT`, read `last_prompt`. It lists the prompt and its options. Retry with the missing inputs; don't loop blindly.
- `COMMAND_BLOCKED` means the command opens a modal window that would freeze Rhino. Use the dash form or a dedicated tool.
- Never run `_Undo` as a command. Use `history_undo`.

## Language

English, always. Every name and text the agent writes into the model is English and ASCII: layers, objects, user text, sets, annotations, sheet titles and file names. This holds even when the user writes in another language.
