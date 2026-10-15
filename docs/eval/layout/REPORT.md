# Evaluation: layouts, annotation and export

Run: `scripts/dev-test.sh tests/eval_layout.py`, on Rhino 7.39 Mac, 2026-10-15.

The sheet `EVAL-A2-Plan` is A2 landscape with one locked Top detail at 1:100 and a title block. It stays in the document. Dimensions, labels and a leader are on `_mcp_eval::annot`. The detail shows only that layer plus the source layer, via per-detail layer visibility.

Files: [`sheet.pdf`](sheet.pdf) (568 kB, vector) · `sheet.png` (OS-rendered preview) · `dxf_entity_summary.json` · `run_notes.json`.

## Source geometry
`_mcp_eval::drawing` holds the clean Make2D output plus a full-size 12 × 12 m house on `::model`. The Make2D sublayers (`plan_1200`, `section_y3000`, the elevations) are already scaled: the 12 m plan is 120 mm wide at y = −30000. The 1:100 detail therefore uses `_mcp_eval::drawing::model`.

## What works
- **Scale is exact.** The detail reads back 100.000 by both methods (`PageToModelRatio` and the world-to-page transform). The PDF page is 594 × 420 mm.
- **PDF output.** `FilePdf` + `ViewCaptureSettings(RhinoPageView, dpi)` works from the hot-loaded DLL. The output is vector (segments, text, dimension ticks) plus one white background image. The `-_Print` fallback was not needed.

## DXF entity types
The file is AC1021 and uses 2D `POLYLINE`/`VERTEX`, not `LWPOLYLINE`.

| Layer | Entities |
|---|---|
| clean `plan_1200` | HATCH 3, LINE 8, POLYLINE 4 |
| clean `section_y3000` | HATCH 11, LINE 6, POLYLINE 13 |
| clean elevations | LINE + POLYLINE only |
| annotations | DIMENSION 2, MTEXT 3, MULTILEADER 1 |
| raw 3D breps | LINE 195, **SPLINE 389** (`exploded_nurbs_risk`) |

None of the clean Make2D layers contains a SPLINE. Raw brep edges still export as SPLINEs, and `curves: "polylines"` does not touch brep edges.

## What's messy
1. **Annotation scaling.** `TextHeight = 3.5 × 100` with `DimensionScale 1` prints 350 mm text. The working recipe is `TextHeight` = printed mm and `DimensionScale` = scale denominator.
2. **Hatches are annotative in details.** The printed spacing is base × scale in paper mm, independent of the detail scale (Hatch1 base = 0.125). `annotate` takes `spacing_mm`. This was verified only for Hatch1 in a mm document.
3. **`view_capture` refuses page views.** The sheet was previewed by rendering the PDF with `sips`.
4. **SVG/AI exports are view-based,** with duplicated paths and a 2400 pt canvas. Use PDF or DXF/DWG for drawings.
5. **Export schemes can't be created by script on Mac.** Unknown scheme names fall back to the default.
6. **Dimension text has no unit suffix.** `LengthResolution` is set per document unit (0 decimals for mm).
7. **Label placement** comes from bounding boxes only, so the "Bedroom" label landed on the stair.
8. **The PDF always contains one 4961 × 3508 white background image,** even with `background: false`.
