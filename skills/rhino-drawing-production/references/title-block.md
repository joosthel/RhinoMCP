# Title block

Content follows ISO 7200 in spirit (data fields in technical product documentation); adapt to the office template.

## Fields

| Field | Notes |
|---|---|
| Project name and number | Never invent; ask |
| Client | |
| Sheet title | What the sheet shows, for example "Floor plan level 2" |
| Sheet number | Per office numbering |
| Scale | Per detail viewport; list several if mixed |
| Date | ISO format `YYYY-MM-DD` unless the office differs |
| Revision | Letter or number, with a revision table |
| Drawn by / checked by | Initials |
| Phase | In Germany the HOAI Leistungsphase (LPH 1 to 9) or a name such as Entwurf, Genehmigung, Ausfuehrung |
| Office name and contact | |
| File name | |
| North arrow | On plans and site plans |

## Layout
- Bottom-right strip or right-hand column, 180 mm wide is common on A1/A0 (verify the office template).
- Fixed text height: titles 5 to 7 mm, labels 3.5 mm, notes 2.5 mm printed (see text-dims.md).
- Put the title block on the layout, not the model, on a layer `Layout::TitleBlock` set to plot.
- Use a block with text fields from user text so fields can be updated in one place.

## Process
1. Ask for the field values you do not have.
2. Fill via `layout_create` title-block fields.
3. Verify with one small `view_capture` of the layout.
