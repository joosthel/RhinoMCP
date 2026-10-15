# 04 — Designer & architect workflows: capability map

> Source: Sonnet 5.5 research subagent, 2026-10-15. Only 2 web searches were run; most content is from background knowledge. Items marked **[U]** are unverified.

## Sources consulted
- Rhino 8 features: https://www.rhino3d.com/features/ and https://novedge.com/blogs/design-news/unlocking-the-future-of-design-exploring-the-revolutionary-features-of-rhino-8 (confirm ShrinkWrap, PushPull, Gumball, SubD creases, improved clipping/sectioning, Metal Mac viewport)
- VisualARQ 3 is the Rhino 8 version; VisualARQ 2 does not run on R8: https://www.visualarq.com/rhino-8-released/
- Rhino.Inside.Revit supports Rhino 8: https://www.rhino3d.com/e-news/0124rir/ , https://www.rhino3d.com/e-news/0425rir/

## How Rhino is used
- **Concept/massing:** extrusions, booleans, area/volume checks; often Grasshopper-driven.
- **Surface modelling:** NURBS facades, roofs, double-curved skins.
- **Organic forms:** SubD (creases in R8).
- **Documentation:** plans/sections/elevations via Make2D or clipping planes → hatch, dimension, layout at scale → PDF/DWG.
- **Interchange:** DWG/DXF/IFC/SKP in; Revit (Rhino.Inside.Revit), fabrication (3DM/STEP/STL/3MF), rendering out.
- **Data:** layers, User Text, blocks act as informal BIM. Elefront/VisualARQ/Human add structure **[U on R8 status]**.
- **Analysis plugins:** Ladybug/Honeybee, Karamba — mostly via Grasshopper.

## Drawing production flow
1. Clipping planes / section tools (R8: section styles per object/layer **[U]**).
2. Make2D (visible/hidden, section lines; R8 clipping-plane drawings **[U]**).
3. Section hatching.
4. Layouts with details at scale; title blocks as blocks with User Text/fields.
5. Print to PDF; export DWG/DXF with layer/linetype mapping.

Scripting caveat: Make2D and parts of printing are reliably driven only via `RhinoApp.RunScript`; layouts/details are in RhinoCommon (`RhinoPageView`, `DetailViewObject`).

## Pain points an agent can address
Cleanup/import hygiene · layer standardisation · quantity reports (area/volume by layer/user text → CSV) · massing variants · floor-plate slicing (Contour) · panelization · batch export · drawing-set production · Grasshopper input/solve/bake · dimension and tag generation.

## Proposed tool taxonomy
Legend: **R7** = works in 7 and 8; **R8** = 8 only; **T** typed tool; **S** script execution; **T+S** typed core + script escape hatch.

| # | Family | Contents | Version | Style |
|---|---|---|---|---|
| 0 | Core runtime | run Python / C#, run command string, undo grouping, doc info/units, selection | R7 (IronPython 2.7) / R8 (CPython 3) | S + few T |
| 1 | Scene query | filtered object listing, details, bbox, validity, viewport capture, doc tree | R7 | T |
| 2 | Curves | primitives, offset, fillet, join, project, intersect, divide | R7 | T+S |
| 3 | Surfaces & solids | extrude, loft, sweep, revolve, boolean, cap, shell, fillet, trim | R7 | T+S |
| 4 | SubD | create, convert, crease | R7 basic / R8 more **[U]** | S |
| 5 | Mesh | convert, reduce, repair, quad remesh, ShrinkWrap (R8) | R7 / R8 | T+S |
| 6 | Transform & array | move/rotate/scale/mirror/orient, arrays, flow | R7 | T |
| 7 | Analysis | area/volume/centroid, curvature, draft, distance, validity, contour | R7 | T / S |
| 8 | Organization | layers, linetypes, print widths, groups, names | R7 | T |
| 9 | Blocks | define/insert/redefine/explode/count, linked | R7 | T |
| 10 | Attributes & data | User Text everywhere, bulk set, CSV import/export | R7 | T |
| 11 | Materials & render | materials, display modes, environment, sun, render to file | R7 (+R8 PBR **[U]**) | T+S |
| 12 | Views | named views, camera, projection, capture to image | R7 | T |
| 13 | Annotation | text, leaders, dimensions, styles, hatches | R7 | T+S |
| 14 | Drawing production | clipping planes, Make2D, section hatching, layouts/details, title blocks, PDF/DWG | R7; section styles R8 **[U]** | S (Make2D/print) + T (layouts/export) |
| 15 | I/O | open/save/import/export many formats, worksessions | R7 | T |
| 16 | Architectural recipes | massing, slicing, schedules, panelization, layer remap | R7 | prompts/skills, not tools |
| 17 | Grasshopper bridge | open def, set inputs, solve, bake | R7 / R8 better **[U]** | S + small T |
| 18 | Plugin bridges | VisualARQ, Elefront, Human, Ladybug, R.I.R | varies | S; probe presence |
| 19 | Safety & session | confirm destructive ops, undo, dry-run, output caps | R7 | T |

## Recommendations
1. Script-first; typed tools where output should be small, deterministic and validated.
2. Make the query layer excellent — agents fail most from not seeing the model.
3. Expose an `engine` parameter; report detected Rhino/Python version.
4. Run everything on the UI thread; use `-_Command` forms to avoid dialogs.
5. One undo record per tool call; dry-run for destructive batch ops.
6. Always return units and tolerance.
7. Expect Mac/Windows differences in plugins and printing **[U]**.
8. Ship architectural workflows as prompts/skills to stay unopinionated.

## To verify before building
R8 clipping/section-style scriptability · R8 SubD API · R8 CPython + rhinoscriptsyntax completeness on Mac · GH automation path · plugin R8 status · Make2D changes in R8.
