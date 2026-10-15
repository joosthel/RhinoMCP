# Line weights (ISO 128 pen steps)

Pen series (step factor sqrt 2): 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0 mm. Use at most 3 to 4 weights per drawing. Verify against the office or client standard.

| Line | Width (mm) | Linetype |
|---|---|---|
| Section cut | 0.5 to 0.7 | continuous |
| Beyond / elevation outline | 0.25 to 0.35 | continuous |
| Projection (visible, below cut) | 0.18 to 0.25 | continuous |
| Hidden | 0.13 to 0.18 | dashed |
| Hatch, fine detail, tangent edges | 0.13 | continuous |
| Grids, axes | 0.13 to 0.18 | chain (dash-dot) |
| Cut lines (section marks) | 0.5 | long dash, double dot |

## By scale
- 1:200 and smaller: shift one step lighter (cut 0.35, projection 0.13).
- 1:100 / 1:50: table above.
- 1:20 / 1:10 / 1:5: shift one step heavier (cut 0.7 to 1.0).

## Output-layer mapping (drawing_make2d)

| Layer | Width | Linetype |
|---|---|---|
| `Drawing::<view>::Section` | 0.5 | Continuous |
| `Drawing::<view>::Silhouette` | 0.35 | Continuous |
| `Drawing::<view>::Visible` | 0.25 | Continuous |
| `Drawing::<view>::Hidden` | 0.13 | Hidden |
| `Drawing::<view>::Tangent` | 0.13 | Continuous |
| `Drawing::<view>::Hatch` | 0.13 | Continuous |
| `Drawing::<view>::Annotation` | 0.18 | Continuous |

## Practical notes
- Widths under 0.04 mm become hairlines in some PDF consumers.
- Set widths on layers (print width ByLayer), not per object.
- Imperial offices often use pen numbers or 0.005 to 0.02 in; convert to the nearest ISO step and ask.
