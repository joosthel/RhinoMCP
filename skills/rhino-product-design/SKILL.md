---
name: rhino-product-design
description: Check and prepare product-design geometry in Rhino for manufacturing. Use for draft angles, surface continuity G0/G1/G2, wall thickness, fillet strategy, shelling, undercuts, injection moulding, or exporting STEP/IGES to a supplier. Gives numeric checks before any visual inspection.
---

# Product design

Product work is judged by numbers: gaps, angles, thicknesses, radii. Check numerically, show images last. Use the `product_*` tools (M3, check `tools/list`); otherwise use the snippets below (IronPython 2.7, no f-strings). Tolerance: product work typically 0.001 mm (see `rhino-units-tolerances`).

## 1. Setup

1. `context_get`: mm, tolerance 0.001 (propose, do not change silently).
2. Put the part in a set (`sets_save`), for example `part`. Check `geometry_validate`: closed, manifold, no naked edges (see `rhino-geometry-hygiene`).
3. Ask the process: injection moulding, CNC, 3D printing, sheet metal, casting. Rules differ.

## 2. Draft check

`product_draft_check`: face normals against the pull direction. Report the count of faces below the minimum draft, with handles, not images.
Typical minimum draft (verify with the moulder): 1 to 3 degrees, 3+ for textured surfaces, 0.5 for polished. Fix by editing faces (`-_DraftAngle` via `run_command`) or by reworking the design.

## 3. Continuity G0 to G2

`product_continuity` samples along shared edges and reports max values:
- G0: position gap (should be below tolerance).
- G1: normal angle between adjacent surfaces (below about 0.5 to 1 degree for visual tangency).
- G2: curvature difference (match for visually smooth highlights).

Interpretation: G0 gap above tolerance means not joined; G1 angle above 1 degree is a visible crease; G2 mismatch shows as highlight breaks. G3 by finite differences is unverified: do not promise it. Zebra gives no numbers; use these reports and, last, `view_capture` with a curvature or zebra display mode.

Fix suggestions: `-_MatchSrf` (tangency or curvature), `-_BlendSrf`, rebuild to matching degree.

## 4. Wall thickness

`product_thickness`: sampled minimum and distribution. Typical minimums (verify with the process owner): injection moulding 1.0 to 3.0 mm (uniform walls, ribs at 50 to 60 percent of wall), FDM 1.2 mm (3 nozzle widths), SLA 0.8 mm, sheet metal per material gauge, CNC aluminium 0.8 to 1.5 mm. Report min, max, and the count of samples below the limit.

Shell: `geometry_edit` shell (or `Brep.CreateOffsetBrep` in script). After shelling, validate and re-measure thickness.

## 5. Fillet strategy

- Fillet order: largest radii first, then small; fillet convex and concave edges in one operation where possible.
- Avoid fillets larger than adjacent face width; `product_fillet_max` finds the largest radius that works for an edge (binary search).
- Keep a radius vocabulary (for example 0.5, 1, 2, 5 mm) for toolability.
- Inside corners for moulding: radius at least 0.5 x wall thickness (verify); outside corners inside radius + wall.
- If a fillet fails: check the edge chain is tangent, try a smaller radius, split the chain, or switch to blend surfaces.

Script fallback to test one radius:

```python
import Rhino.Geometry as rg
import scriptcontext as sc
brep = sc.doc.Objects.FindId(guid).Geometry  # guid resolved by caller
radii = [1.0] * len(edge_indices)
res = rg.Brep.CreateFilletEdges(brep, edge_indices, radii, radii,
    rg.BlendType.Fillet, rg.RailType.DistanceFromEdge, sc.doc.ModelAbsoluteTolerance)
result = {"ok": bool(res), "count": len(res) if res else 0}
```

## 6. STEP export checks

1. Validate: closed solid, no naked edges, no tiny faces (`geometry_validate`, `rhino-geometry-hygiene`).
2. Units: confirm the supplier unit (STEP is mm unless stated).
3. Export through `export` (M2) or `run_command` `-_Export` with explicit file name and options. For assemblies, name parts and use layers/blocks.
4. Round-trip: re-import into a scratch document, compare volume, face count and bbox against the original within tolerance. Report differences.
5. Do not send meshes where a supplier expects B-rep.

Return to `rhino-mcp-efficient-use` for the general loop.
