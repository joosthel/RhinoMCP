# 05 — Clean, editable Make2D output

> Source: Sonnet 5.5 research subagent, 2026-10-15. The API section was verified against the local Rhino 7 `RhinoCommon.xml`. Web sourcing for complaints and plugins was thin. **[U]** = unverified.

## Bottom line
Rhino 7 exposes everything needed for a better-than-Make2D pipeline. `HiddenLineDrawing` gives per-segment visibility, the silhouette type (including `SectionCut`), side fills and the source-object link. Cleanliness has to come from our own post-processing. The API has no hatch or section-style output, so we generate hatches ourselves.

## 1. Known pain points
- Duplicate and overlapping curves. SelDup catches exact duplicates only. McNeel's `Overlap` command (an AutoCAD OVERKILL equivalent, curves only, tolerance 0.001) is WIP only ([thread](https://discourse.mcneel.com/t/rhino-wip-feature-overlapping-geometry-cleanup/215871)).
- Manual recipe users fall back on: SimplifyCrv → SelDup → Join; community `RhinoKill2DV6.py` gets "most of the way" ([thread](https://discourse.mcneel.com/t/intersect-issue/126751)).
- Uniform, flat lineweights. There's a community [open-source line-weight tool](https://discourse.mcneel.com/t/open-source-line-weight-tool-for-architects-make2d-day-1-wip/219611).
- Hatches lost through Make2D. Rhino 8 Section Styles don't carry through **[U]** ([thread](https://discourse.mcneel.com/t/clipping-plane-sections/216896)).
- PDF widths under 0.04 mm become 0.12 pt in Illustrator ([thread](https://discourse.mcneel.com/t/rhino-line-weights-to-pdf/209714)). AI export distorts NURBS circles. DWG arrives exploded unless keep-joined is set.
- Expected from the API itself **[U as user complaints]**: curves split at every visibility change, NURBS where lines would do, jagged mesh outlines, tangent and seam clutter, and everything on one layer.
- Tips: [cleaner Make2D](https://novedge.com/blogs/design-news/rhino-3d-tip-preparing-rhino-models-for-cleaner-make2d-output), [hatch boundaries](https://novedge.com/blogs/design-news/rhino-3d-tip-clean-reliable-hatch-boundaries-from-make2d), [vector exports](https://novedge.com/blogs/design-news/rhino-3d-tip-crisp-vector-exports-from-rhino).

## 2. API (verified in Rhino 7.38 RhinoCommon.xml)
- **`HiddenLineDrawingParameters`**:
  - `AbsoluteTolerance`
  - `IncludeHiddenCurves`, `IncludeTangentEdges`, `IncludeTangentSeams` (all default true)
  - `Flatten`
  - `SetViewport(ViewportInfo|RhinoViewport)`
  - `AddClippingPlane(Plane)`
  - `AddGeometry(GeometryBase, [Transform], object tag)`
- **`HiddenLineDrawing`**:
  - `Compute(params, multithread[, IProgress, CancellationToken])`
  - Results: `Segments`, `FullCurves`, `Objects`, `Points`, `Viewport`, `WorldToHiddenLine`, `BoundingBox(includeHidden)`, `Flatten()`
  - `IDisposable`
- **`HiddenLineDrawingObjectCurve`**: `SourceObject`, `SourceObjectComponentIndex`, `SilhouetteType`, `ClippingPlaneIndex`, `Segments`.
- **`HiddenLineDrawingSegment`**: `CurveGeometry`, `ParentCurve`, `SegmentVisibility`, `IsSceneSilhouette`, `CurveSideFills[2]`.
- **`SilhouetteType`** (verified): None, Projecting, TangentProjects, Tangent, Crease, Boundary, NonSilhouetteCrease, NonSilhouetteTangent, NonSilhouetteSeam, DraftCurve, MiscellaneousFeature, **SectionCut**.
- **`Segment.Visibility`** (verified): Unset, Visible, Hidden, **Duplicate**, Projecting, Clipped.
- **`SideFill`**: SideUnset, SideSurface, SideVoid, OtherSurface. Use it to pick the hatch side.
- Rhino 8 Section Styles have no HiddenLineDrawing API **[U]**.

## 3. Cleanup pipeline
1. Drop segments that are `Duplicate`, `Clipped`, `Projecting`, or shorter than tolerance.
2. Reduce each curve to the simplest primitive: `LineCurve` / `ArcCurve` / polyline. Use `Curve.Simplify(Lines|Arcs)`. Refit free-form curves.
3. Weld endpoints, then `Curve.JoinCurves(tol)` and `PolyCurve.RemoveNesting`. Merge collinear pieces.
4. De-overlap with a spatial hash on rounded endpoints plus a midpoint check. Split partial collinear overlaps and keep one copy.
5. Group by (source layer tag, `SilhouetteType`, visibility). Map to layer, lineweight and linetype:
   - section = heavy
   - visible = medium
   - hidden = thin dashed
   - tangent = hairline
6. Hatch closed `SectionCut` loops per source object with `Hatch.Create`.
7. Transform to true scale at a chosen origin. Optionally add a scale bar and blocks per source object.

Existing tools: the Grasshopper Make2D component and the SDK class ([Rhino 6 Make2D](https://www.rhino3d.com/6/new/make2d/)). Cleanup claims for Clean2D, SectionTools, VisualARQ, Elefront and Pufferfish are **[U]**.

## 4. Export fidelity **[mostly U]**
- **DWG/DXF:** send clean lines, arcs and polylines with keep-joined on. Layer print widths carry over. Script `-_Export` with explicit options.
- **PDF/AI:** print widths ≥ 0.04 mm; vector output; true arcs and circles. Dashes may be lost in AI.
- **SVG:** untested.
- **Test matrix:** export the same drawing to DWG, PDF and AI, then check entity types and weights in AutoCAD and Illustrator.

## 5. Proposed tool: `drawing.make_clean`
- **Inputs:**
  - `objects` (ids or layers)
  - `view` (named view, plane or direction)
  - `clipping_planes[]`
  - `scale` (e.g. 1:100), `origin`
  - `tolerance`
  - `include_hidden`, `include_tangent`, `include_seams`
  - `layer_scheme` (visibility/type → layer, weight, linetype)
  - `hatch` (pattern, scale)
  - `group_by` (object, layer or none)
  - `blocks`, `scale_bar`
  - `export` (format and path)
- **Steps:**
  1. Collect geometry and resolve blocks with their transforms.
  2. Warn on meshes.
  3. Compute.
  4. Filter → simplify → weld/join → de-overlap.
  5. Hatch.
  6. Scale and place.
  7. Write layers and groups.
  8. Export.
- **Output layers:** `Drawing::<view>::{Visible, Hidden, Section, Silhouette, Tangent, Hatch, Annotation}`.
- **Quality report (numbers only):**
  - `segments_in` / `segments_out`
  - drops by reason
  - tiny segments before and after
  - `duplicates_removed`, `overlaps_removed`
  - curve counts by type (line, arc, polyline, NURBS)
  - `open_ends`, `closed_section_loops`, `hatches_created`
  - `bbox`, `scale_check`, `mesh_sources`

## Risks
- De-overlap needs a spatial index to stay fast on large drawings.
- Rhino 8 section styles may need a separate path.
- Mesh sources will stay jagged. Prefer brep or SubD sources.
