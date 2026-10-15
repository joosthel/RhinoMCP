# Custom layer scheme format (JSON)

`layers_apply_scheme` accepts this format inline (or as a file path in a later version). It is also what the built-in schemes expand to.

```json
{
  "name": "my-office-2026",
  "delimiter": "::",
  "layers": [
    { "path": "00_Model",               "color": "#808080", "linetype": "Continuous", "print_width_mm": 0.18, "plot": true },
    { "path": "00_Model::Walls",        "color": "#000000", "linetype": "Continuous", "print_width_mm": 0.50, "plot": true },
    { "path": "00_Model::Windows",      "color": "#0070c0", "linetype": "Continuous", "print_width_mm": 0.25, "plot": true },
    { "path": "02_Annotation::Dims",    "color": "#ff0000", "linetype": "Continuous", "print_width_mm": 0.13, "plot": true },
    { "path": "99_Helpers",             "color": "#c0c0c0", "linetype": "Continuous", "print_width_mm": 0.0,  "plot": false }
  ]
}
```

## Fields

| Field | Type | Required | Meaning |
|---|---|---|---|
| `name` | string | yes | Scheme id. Stored in document user text `rhino-mcp.layer-scheme`. |
| `delimiter` | string | yes | Separator between hierarchy levels in `path`. Rhino's native sublayer separator is `::`. If another delimiter is used (for example `-` as in AIA `A-WALL-FULL`), layers are created flat with the full path as the name. |
| `layers` | array | yes | Layers to create or update. |
| `layers[].path` | string | yes | Full layer path using `delimiter`. Parents are created implicitly if not listed. |
| `layers[].color` | string | no | `#rrggbb`. Default: keep existing or Rhino default. |
| `layers[].linetype` | string | no | Name of a linetype in the document, for example `Continuous`, `Hidden`, `Dashdot`. Unknown names produce a warning and fall back to `Continuous`. |
| `layers[].print_width_mm` | number | no | Plot width in millimetres. `0` means default/hairline. Use ISO 128 steps: 0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0. |
| `layers[].plot` | boolean | no | Whether the layer plots. Default true. |

## Behaviour

- Existing layers are updated in place (colour, linetype, width, plot) only for the listed fields. Nothing is renamed or deleted. Objects stay where they are.
- Missing layers are created. The result reports counts: created, updated, unchanged, warnings.
- Mapping existing layers to the scheme (renames) is a separate, confirmed step.
- Validation errors (bad colour, duplicate path, empty name) are returned all at once.
