# AIA / US National CAD Standard (NCS) layers

Format: `Discipline-Major-Minor-Status`, separated by hyphens, for example `A-WALL-FULL`. Quoted from general knowledge: verify against the current NCS Uniform Drawing System / CAD Layer Guidelines for contract work.

## Structure
- Discipline designator: one or two letters. `A` architectural, `S` structural, `M` mechanical, `E` electrical, `P` plumbing, `C` civil, `L` landscape, `I` interiors, `G` general.
- Major group: four characters, for example `WALL`, `DOOR`, `GLAZ`, `COLS`, `FLOR`, `ANNO`.
- Minor group (optional): four characters, for example `FULL`, `PRHT`, `DIMS`, `NPLT`.
- Status (optional, one character, appended): `N` new, `E` existing, `D` demolition, `F` future, `T` temporary.

## Common architectural layers

| Layer | Meaning |
|---|---|
| `A-WALL` | Walls |
| `A-WALL-FULL` | Full-height walls |
| `A-WALL-PRHT` | Partial-height walls |
| `A-DOOR` | Doors |
| `A-GLAZ` | Glazing, windows |
| `A-FLOR` | Floor, stairs |
| `A-FLOR-STRS` | Stairs |
| `A-CLNG` | Ceilings |
| `A-ROOF` | Roof |
| `A-COLS` | Columns (structural: `S-COLS`) |
| `A-AREA` | Areas, room boundaries |
| `A-ANNO-DIMS` | Dimensions |
| `A-ANNO-TEXT` | Text |
| `A-ANNO-SYMB` | Symbols |
| `A-ANNO-NPLT` | Non-plotting annotation |
| `A-ANNO-TTLB` | Title block |
| `A-GRID` | Column grid |
| `S-COLS`, `S-BEAM`, `S-SLAB` (verify `S-FNDN` etc.) | Structural |

## Rhino notes
- The hyphen is part of the layer name; use a flat layer list, or put the discipline as parent (`A::WALL::FULL`) if the user wants a tree. Ask which.
- Status suffix: append only when the user tracks phases this way.
- Plot widths by layer: see `rhino-drawing-production` references/lineweights.md.
