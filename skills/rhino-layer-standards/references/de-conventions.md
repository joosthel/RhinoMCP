# German office layer conventions

There is no binding national layer standard. DIN 1356-1 defines drawing content (what a Bauzeichnung shows), not a layer scheme. Offices therefore use a house standard, a translated AIA scheme, or a scheme keyed to the HOAI Leistungsphase (LPH). **Ask the user which one applies.** Also ask whether consultants (Tragwerksplanung, TGA) impose a scheme.

## Common names (ASCII-safe spelling)

| Layer name | Meaning |
|---|---|
| `Wand` or `Wand_Tragend`, `Wand_Nichttragend` | Walls, load-bearing and non-load-bearing |
| `Decke` | Slabs |
| `Stuetze` | Columns |
| `Traeger`, `Unterzug` | Beams |
| `Fenster` | Windows |
| `Tuer` | Doors |
| `Treppe` | Stairs |
| `Dach` | Roof |
| `Fassade` | Facade |
| `Boden`, `Bodenbelag` | Floor |
| `Achsen`, `Raster` | Grid, axes |
| `Raum`, `Raumstempel` | Rooms, room stamps |
| `Bemassung` | Dimensions |
| `Text` | Text |
| `Schraffur` | Hatches |
| `Schnitt` | Cut geometry |
| `Ansicht` | Elevation lines |
| `Bestand`, `Abbruch`, `Neubau` | Existing, demolition, new (status convention used in permit drawings) |
| `Hilfslinien` (non-plotting) | Helper lines |

## Practical patterns
- Rhino tree: `00_Modell::Wand`, `01_Make2D::...`, `02_Anno::Bemassung`.
- Status through sublayers or a suffix: `Wand_Bestand`, `Wand_Abbruch`, `Wand_Neu`.
- Phase keyed layouts: `LPH3_Entwurf`, `LPH4_Genehmigung`, `LPH5_Ausfuehrung` for sheet sets or layer states.
- Umlauts: prefer `ae/oe/ue/ss` when the file goes to DWG consumers.
- Drawing units: metres with decimal comma in Germany is typical for plans; mm for details. Ask.
- DIN 1356-1 hatch and line conventions are content rules; see `rhino-drawing-production`.
