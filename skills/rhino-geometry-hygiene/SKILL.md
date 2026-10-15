---
name: rhino-geometry-hygiene
description: Diagnose and repair bad Rhino geometry. Use when a boolean fails, an object has naked or non-manifold edges, a solid will not close, join fails, normals are flipped, curves are bloated, or before exporting a mesh for 3D printing, CNC or rendering. Gives a diagnostic ladder and repair recipes.
---

# Geometry hygiene

Diagnose cheaply first, fix second, verify third. Never repair blind.

## Diagnostic ladder (stop at the first rung that explains the problem)

1. **Counts by type.** `objects_query` with `group_by` type on the affected set. Are they breps, extrusions, meshes, polysurfaces, curves?
2. **Validity.** `geometry_validate` on the set: validity log, closed/solid, manifold, naked edge count, face count. This is the main tool; call it once on the whole set, not per object.
3. **Bad objects.** If a count of invalid objects is nonzero, `run_command` `-_SelBadObjects` (selects them) or use the script below.
4. **Naked edges per brep.** Many naked edges means missing faces or loose tolerance; a few means small gaps.
5. **`IsSolid`.** Booleans need closed, manifold inputs with consistent normals.
6. **Context.** Tolerance and origin distance (see `rhino-units-tolerances`). Coplanar or coincident faces between boolean inputs.
7. Only then fix.

## Repair recipes (smallest first)

Rhino commands go through `run_command` with `-_` prefix and explicit options. Select the set first with `objects_select` or work through a tool.

| Problem | Try |
|---|---|
| Surfaces not joined | `-_Join` (tolerance-driven). If it fails, `-_JoinEdge` with a tolerance, or `-_ExtractBadSrf` then rebuild or `-_Patch` |
| Naked edges, tiny gaps | `-_ShowEdges` (naked), then `-_Join` / `-_MergeAllFaces` / `-_RemoveAllNakedMicroEdges` |
| Normals inconsistent | `-_UnifyNormals`, or `-_Flip` on selected faces; `-_Dir` shows direction |
| Bad or invalid surfaces | `-_ExtractBadSrf`, rebuild the extracted face, `-_Join` back |
| Trimmed surface with bloated untrimmed control net | `-_ShrinkTrimmedSrf` |
| Duplicate objects | `-_SelDup`, then `objects_delete` |
| Coplanar faces blocking a boolean | Nudge the cutter by 2x tolerance, or `-_MergeAllFaces` first |
| Dirty file | `-_Audit3dmFile` (repairs), `-_Purge` for unused definitions (ask before purging) |
| Curve too heavy | `-_FitCrv` keeps shape within tolerance; `-_Rebuild` changes shape and sets degree/point count; `-_SimplifyCrv` for lines and arcs |

Verify after each recipe with `geometry_validate`. If it got worse, `history_undo` once.

## RhinoCommon snippets (IronPython 2.7: no f-strings)

Count validity problems on a layer, return compact JSON:

```python
import Rhino
import scriptcontext as sc

layer_name = "Model::Walls"
bad = []
n = 0
for obj in sc.doc.Objects.FindByLayer(layer_name) or []:
    n += 1
    g = obj.Geometry
    ok, log = g.IsValidWithLog()
    if not ok:
        bad.append({"id": str(obj.Id)[:8], "log": log[:80]})
    if len(bad) >= 10:
        break
result = {"checked": n, "invalid": len(bad), "first": bad}
```

Naked edge count for a brep:

```python
import Rhino.Geometry as rg
def naked_edges(brep):
    return sum(1 for e in brep.Edges if e.Valence == rg.EdgeAdjacency.Naked)
```

Try to repair and report:

```python
def try_repair(brep, tol):
    b = brep.DuplicateBrep()
    b.JoinNakedEdges(tol)
    b.Repair(tol)
    return b, b.IsValid, b.IsSolid
```

Always replace or add geometry through `sc.doc.Objects.Replace(id, newbrep)` and report counts. Keep results small.

## Booleans: pre-flight

- Inputs closed and manifold, normals outward.
- No coplanar or coincident faces between operands; offset one by 2x tolerance.
- Bounding boxes actually overlap.
- Result check: validity, `IsSolid`, volume between max(Vi) and the sum for a union.
- On failure: one cutter at a time, `geometry_validate` inputs, then tolerance review with the user.

## Degree guidance

- Degree 1: straight edges. Degree 2: true arcs and circles. Degree 3: default for free-form. Avoid above 5.
- Use `-_Rebuild` to a fixed degree and point count only when shape change is acceptable; use `-_FitCrv` to preserve shape.

## Mesh export presets

Set per process, then check the mesh is closed (watertight) and has no naked edges. Values are starting points; verify with the fabrication partner.

| Process | Max edge length | Edge to surface distance | Notes |
|---|---|---|---|
| 3D print FDM | 0.5 to 1 mm | 0.01 to 0.02 mm | Closed mesh, wall thickness above nozzle width |
| 3D print SLA / resin | 0.2 to 0.5 mm | 0.005 to 0.01 mm | |
| CNC / laser (curves) | n/a | 0.01 mm | Prefer curves or DXF, not mesh |
| Visualisation / rendering | 2 to 10 percent of object size | 0.1 to 0.5 mm | Smooth normals, density control |
| Quick preview | coarse (Rhino default) | 0.1 mm or more | |

Convert with `-_Mesh` and `DetailedOptions` (set in the dialog or via `run_python` with `MeshingParameters`), then `-_ShowEdges` or `geometry_validate` to confirm closedness.

Return to `rhino-mcp-efficient-use` for the general loop.
