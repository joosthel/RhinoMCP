# Text and dimensions by scale

## Formula

model text height = printed height x scale denominator (in model units, converted)

Example: 3.5 mm printed at 1:100 is 3.5 x 100 = 350 mm in a mm model, or 0.35 m in a metre model.

## Printed heights

| Use | Printed height |
|---|---|
| Notes | 2.5 mm (minimum about 1.8 mm) |
| Labels, room names | 3.5 mm |
| Sheet titles | 5 to 7 mm |

## Quick table (mm model)

| Scale | 2.5 mm | 3.5 mm | 5 mm |
|---|---|---|---|
| 1:20 | 50 | 70 | 100 |
| 1:50 | 125 | 175 | 250 |
| 1:100 | 250 | 350 | 500 |
| 1:200 | 500 | 700 | 1000 |
| 1:500 | 1250 | 1750 | 2500 |

Imperial: 1/8 in printed text (3.2 mm) is the usual minimum; at 1/4 in = 1 ft (1:48) that is 6 in model height.

## Alternative: annotation in layouts
Text placed in the layout (paper space) is already at printed size; only use the formula for text placed in model space. For multiple scales on one sheet, prefer annotation in layouts or dimension style scale set to the detail scale.

## Dimension styles
- Scale the dimension style by the drawing scale (model space) so printed arrow, text and offsets are consistent.
- DE: tick marks at dimension lines, text above the line, metres with decimal comma or millimetres (ask).
- US: architectural units, feet-inches with fractions, arrows or ticks per office.
- Dimension in the layout when possible: set model-space dimension styles to scale 1:1 with the unit conversion handled by the detail.
- Always verify one dimension against `measure_distance`.
