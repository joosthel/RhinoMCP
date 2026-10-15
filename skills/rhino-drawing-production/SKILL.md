---
name: rhino-drawing-production
description: Produce clean 2D drawings from a Rhino 3D model: plans, sections, elevations, details, sheets. Use when asked to make a drawing, Make2D, section cut, elevation, layout, line weights, hatches, title block, scale, plot, or export PDF/DWG. Covers the clean pipeline, ISO 128 weights and text height by scale.
---

# Drawing production

Pipeline: views, clipping, clean Make2D, layers and weights, layout at scale, title block, export. The tools below are M2; check `tools/list`. If missing, use the listed `run_command` fallback and warn the user the result is not cleaned.

## 1. Prepare

1. `context_get`: units and tolerance (see `rhino-units-tolerances`). Make2D output is only as exact as the tolerance.
2. Confirm the scheme (`rhino-layer-standards`) and the output scale, paper size and region (ISO or ANSI). Ask if not stated.
3. Define the source set with `sets_save` (for example `drawing_src`). Exclude annotation, helpers and hidden layers. Warn if it contains meshes: output stays jagged. Prefer brep or SubD sources.
4. Clean the source first if booleans or joins are suspect (`rhino-geometry-hygiene`).

## 2. Views and clipping

- Plans: cut at about 1.0 to 1.2 m above floor (verify local convention; US often 4 ft / 1.2 m). Use `drawing_clipping_plane` with plane and depth. Section lines for sections and elevations.
- Elevations: no clipping, parallel projection, view direction by name or vector.
- `view_set` to a parallel projection in the matching named view (`views_named`).

## 3. Make2D, clean

Call `drawing_make2d` with the set, view or plane, clipping planes, scale, origin, and options. The server runs: filter duplicates and clipped segments, simplify to lines/arcs/polylines, weld and join, de-overlap, assign layers and weights, hatch closed section loops, place at true scale.

**Read the quality report before looking at the drawing.** It is numbers only:
- `segments_in` / `segments_out`, drops by reason
- `duplicates_removed`, `overlaps_removed`
- curve counts by type (line, arc, polyline, NURBS)
- `open_ends`, `closed_section_loops`, `hatches_created`
- `bbox`, `scale_check`, `mesh_sources`

Decision rules: `open_ends` above zero means gaps (raise tolerance only with consent, or fix the source). `closed_section_loops` lower than the number of cut solids means unclosed sections. `mesh_sources` above zero means jagged output. NURBS counts high means simplify failed on free-form edges (acceptable for organic shapes). Report the key numbers to the user in two lines.

## 4. Layers and weights

Output layers: `Drawing::<view>::{Visible, Hidden, Section, Silhouette, Tangent, Hatch, Annotation}`. Weights per ISO 128 steps: [references/lineweights.md](references/lineweights.md). Lighter at small scales, heavier at large scales. Colour ByLayer. Do not set widths below 0.13 mm for PDF: widths under about 0.04 mm are converted to hairlines in Illustrator.

## 5. Hatches

Closed section loops only; pattern by material. See [references/hatches.md](references/hatches.md). At 1:200 and smaller use solid poche instead of patterns.

## 6. Layout at scale

`layout_create` with paper size, details at scale and title-block fields. Standard scales and paper sizes: [references/scales-paper.md](references/scales-paper.md). Check `scale_check` in the report: a known dimension on the sheet must measure `real / scale`.

## 7. Annotation

Use `annotate` for dimensions, text and hatches. Text model height = printed height x scale denominator: [references/text-dims.md](references/text-dims.md). Dimension style per region (DE ticks with text above the line, US architectural fractions).

## 8. Title block

Fields and layout: [references/title-block.md](references/title-block.md). Fill from user answers; leave unknown fields blank, never invent project numbers or dates.

## 9. Export

`export` PDF (vector, line widths from layers), DWG/DXF (clean lines, arcs, polylines, keep-joined; plot widths carry over), SVG, AI. Pass explicit options. Verify: file exists, size plausible, and for DWG that entity types are not exploded NURBS. One look with `view_capture` of the layout at small size is enough.

## Failure notes

- Make2D slow or `TIMEOUT`: reduce the set (one level or zone), or use jobs (`jobs_status`) if available.
- Missing hatches: section loops not closed, or `SideFill` ambiguity. Check `closed_section_loops`.
- Wrong scale: origin or units mismatch; re-run `context_get`.

Return to `rhino-mcp-efficient-use` for the general loop.
