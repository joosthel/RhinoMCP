---
name: rhino-units-tolerances
description: Check and set document units and absolute tolerance in Rhino before modelling, importing, scaling, or exporting. Use when booleans fail, results look wrong in size, imported geometry mismatches, objects sit far from the origin, or the user asks about tolerance, units, scale or model size.
---

# Units and tolerances

Operations fail for numeric reasons far more often than for logical ones. Check before modelling, not after the first failure.

## Checklist (run at project start and before any import or export)

1. `context_get`: read unit system, absolute tolerance, angle tolerance. Record them.
2. Does the unit fit the task? Architecture in mm or m, product in mm, urban in m, jewelry in mm. See [references/tolerance-table.md](references/tolerance-table.md).
3. Is the tolerance at least 10x smaller than the smallest feature you will model, and not below about 1e-5? If you need finer, switch to smaller units rather than a tinier tolerance.
4. Is the model near the origin? Bounding box far away (beyond roughly 10 km from origin in model units, much less in mm) causes numeric noise. Use a local origin and keep the georeference as user text.
5. Is the largest to smallest feature ratio sane (under about 1e6)? If not, split the model.
6. Report the result to the user in one line: "mm, tol 0.01, angle 1 deg, model within 50 m of origin".

## Rules

- **Never change tolerance or units silently.** Changing them changes which operations succeed and can invalidate existing geometry. Propose the change, state why, get consent, then do it.
- Set the tolerance once at project start. Do not tweak it per operation to force a boolean through; if an operation needs a different tolerance, say so and ask.
- Changing the unit system has two modes: scale the model (geometry changes size) or keep geometry as is (only the label changes). They are different operations: choose deliberately. With `run_command` use `-_Units` with explicit options (`ModelUnits` / scale choice) and verify with `context_get` plus a bbox `measure` before and after.
- Blocks and linked/worksession files carry their own units. Check them after insertion.
- Tools in this server echo the unit with every number. If a number looks off by 10, 100, or 1000, suspect the unit first.

## Imports

1. Before import, ask the file's unit (DWG/DXF/STEP/OBJ often differ from the document).
2. After import, `measure` the bbox of the imported set and compare against an expected known dimension (a door, a storey height).
3. If the size is off by a clean factor (25.4, 1000, 100, 10), the import unit was wrong. Undo with `history_undo`, re-import with the right unit option, or scale the set with `geometry_transform` using the known factor, from a stated origin.
4. If imported objects fail `geometry_validate` while native ones do not, the source file had a looser tolerance. Report it; do not tighten or loosen the document tolerance without consent. Fix geometry per `rhino-geometry-hygiene`.
5. Multiple sources: agree one unit system and one tolerance before merging.

## Why operations fail (quick diagnosis)

| Symptom | Likely numeric cause | Cheap check |
|---|---|---|
| Boolean returns nothing or garbage | Tolerance too loose for feature size, or too tight for imported geometry; coplanar faces; far from origin | tolerance vs smallest feature; bbox distance from origin |
| Naked edges after join | Tolerance too tight for the gaps | `geometry_validate`, edge gap sizes |
| Slivers, collapsed small features | Tolerance too loose | smallest feature size / tolerance should exceed 10 |
| Fillet or offset fails | Radius larger than adjacent features or tolerance related | `product_fillet_max` if available |
| Shaky display, jitter | Far from origin | bbox |
| Mesh looks coarse | Mesh settings, not tolerance | see mesh presets in `rhino-geometry-hygiene` |

## Reading tolerance with a script (when `context_get` is not enough)

```python
import scriptcontext as sc
doc = sc.doc
result = {
    "unit": str(doc.ModelUnitSystem),
    "abs_tol": doc.ModelAbsoluteTolerance,
    "angle_tol_deg": doc.ModelAngleToleranceDegrees,
    "rel_tol": doc.ModelRelativeTolerance,
}
```

Setting (only after the user agreed): `doc.ModelAbsoluteTolerance = 0.001`. Do not do this inside loops.

Return to `rhino-mcp-efficient-use` for the general loop.
