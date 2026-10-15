# Section hatches

Practice follows DIN 1356 / ISO 128-50 conventions in spirit; office standards vary. Verify with the user. Rhino 7 ships few architectural patterns: custom `.pat` files may be needed.

| Material | Pattern |
|---|---|
| Concrete (unreinforced) | Aggregate / dotted pattern |
| Reinforced concrete | Aggregate plus diagonals |
| Masonry, brick | Diagonal lines at 45 degrees |
| Insulation | Zigzag / wave |
| Timber (cut across grain) | Grain pattern; lengthwise: parallel lines |
| Metal, steel | Dense diagonal lines (or solid at small scale) |
| Earth / soil | Diagonal with dots |
| Glass | Usually none; double thin outline |

## Scale rules
- 1:200 and smaller: solid poche (fill) for cut walls and slabs, no patterns.
- 1:100: simple patterns or solid, spacing around 2 mm printed.
- 1:50 and larger: full material patterns; pattern scale = printed spacing x scale denominator in model units.

## Technical
- Hatch only closed `SectionCut` loops per source object. Check `closed_section_loops`.
- Put hatches on `Drawing::<view>::Hatch` with 0.13 mm width.
- Use `annotate` hatch, or `Hatch.Create` in `run_python`. Pattern name must exist in the document, otherwise a warning and fallback to solid.
- Hatch rotation keeps patterns readable across adjacent materials (alternate 45 and 135 degrees).
