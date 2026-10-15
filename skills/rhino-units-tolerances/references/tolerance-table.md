# Tolerance table

Starting points, not laws. Rhino 7 template defaults: Large Objects 0.01, Small Objects 0.001, angle 1 degree, relative 1 percent. Rule: tolerance at least 10x smaller than the smallest detail; do not go below about 1e-5 (switch to smaller units instead). Verify against the McNeel tolerance FAQ for your case.

| Use case | Unit | Absolute tolerance | Notes |
|---|---|---|---|
| Architecture, building | mm (EU) or m | 0.01 to 0.1 mm, or 0.001 m | Smallest modelled detail around 1 cm; 0.01 mm is plenty |
| Urban, site, terrain | m | 0.01 to 0.001 m | Use a local origin; keep the georeference in user text |
| Landscape, large infrastructure | m | 0.01 m | Split the model if extents exceed tens of kilometres |
| Interior, furniture | mm | 0.01 mm | |
| Product design, consumer | mm | 0.001 mm | Class A surfaces may need tighter continuity checks instead of tighter tolerance |
| Jewelry, watch | mm | 0.001 to 0.0001 mm | Smallest feature around 0.1 mm |
| 3D printing (FDM, SLA) | mm | 0.01 mm | Printer resolution is coarser than this |
| CNC, laser cutting | mm | 0.01 mm | Match kerf and machine resolution |
| Mould, tooling | mm | 0.001 mm | |
| Imported mesh-derived models | mm | 0.01 mm | Mesh noise defeats tighter values |

## Angle tolerance
1 degree default is fine for most work. Product surfacing for continuity checks: 0.1 to 0.5 degree. Do not use below 0.01 degree.

## Relative tolerance
Leave at 1 percent unless a McNeel article for the exact operation says otherwise.

## Too loose versus too tight

| Too loose | Too tight |
|---|---|
| Slivers, collapsed features, wrong boolean results | Naked edges, failed intersections, bloated control-point counts |
| Features smaller than tolerance vanish | Imported geometry with looser source tolerance fails to join |

## Scale ratio
Keep the ratio between the largest model dimension and the tolerance under about 1e7. Beyond that, expect numeric trouble: split the model or change units.

## Origin distance
As a rough guide, keep geometry within about 100 km (m units) or about 1 km (mm units) of the origin. Beyond that, translate to a local origin.
