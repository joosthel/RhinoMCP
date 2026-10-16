# Demo media

`docs/media/demo.mp4`, `demo.gif`, `hero.png`, the stills and the sheet (`sheet.pdf`, `sheet.png`) are produced by two scripts. The model is an original design (a modular sideboard: white steel tubes, spherical connectors, flush navy panels, oak top). There are no logos, brands or text on it.

## Re-record
Needs a running Rhino 7 with RhinoMCP, a **visible** Rhino window, an empty document, Python 3.9 with Pillow, and `ffmpeg` at `/opt/homebrew/bin/ffmpeg`.

```sh
python3 tests/demo/record_demo.py all      # builds the sideboard through MCP tools, makes the drawings and the A3 sheet and captures the raw frames to /tmp/rhinomcp_demo
python3 tests/demo/compose.py              # titles, captions, fades, MP4, GIF, hero, stills, sheet.pdf and sheet.png into docs/media
```

`record_demo.py` works on the layer tree `_demo`. At the end it deletes everything it made (objects, layers, clipping planes, named set, render materials) and restores the viewport (it saves a temporary named view first). It also deletes the A3 layout (and the `Sheet` layers `layout_create` makes) and the `make2d_*` output sets. Use `--keep` to leave the model for inspection, then `record_demo.py cleanup`. `record_demo.py details` captures close-ups of a node, a panel corner and a knob for checking.

## Quality choices
- **Anti-aliasing.** The capture guard allows 1,920,000 pixels (`max_capture_pixels`), so 2x (2560 x 1440) is not possible. Captures are 1840 x 1035 and are scaled to 1280 x 720 with Lanczos. All text is drawn at 2x and scaled down, and the camera push-in uses a sub-pixel source box.
- **One look.** Every model shot uses the `Rendered` display mode (white ground, soft shadow, layer materials, no grain), a fixed lens and no grid, axes or selection. Never use `Raytraced`: it renders on all cores and blocks the capture and the MCP server.
- **No camera pulsing.** The turntable has 120 frames at a fixed distance, elevation and lens. Only the azimuth changes, eased.
- **Drawings.** Make2D runs at full size (scale 1:1); the three views sit far apart in model space and the A3 sheet shows each in a detail at exactly 1:10 (`layout_create`, per-detail layers). Dimensions and leaders are `annotate` calls at scale 1:10: dimension text 3.5 mm, label text 2.5 mm, titles 5 mm, tick marks, chained dimensions, one dimension style. For the video the three views appear in beats (linework, dimensions, labels) captured in `Rendered` on white; the black linework is dilated by about 1 px before the downscale, the coloured annotation keeps its colour. Only solid poché is used (no diagonal hatches, so no moire).
- **The sheet.** `view_capture` refuses page views, so the exported PDF is rendered with macOS `sips` (A3 at 300 dpi, 4961 x 3508). That image is vector-crisp; the video pans over it (push-in on the elevation, then section, plan and title block), and the hero and `docs/media/sheet.png` use it. `sheet.pdf` is 0.3 MB; the recorder also exports the drawing layers to DXF (27 DIMENSION, 18 MULTILEADER entities) into the work folder.
- **Transitions.** Scenes change through a 0.4 s dip to white. Captions only change at cuts.
- **Encoding.** MP4 H.264, crf 19, yuv420p, `+faststart`. GIF 960 px, 12 fps, `palettegen stats_mode=diff`, `paletteuse dither=sierra2_4a`, shortened to about 15 s; the MP4 has the full story.

## Known limits
- **Elevation silhouettes.** Rhino's hidden-line drops the tube silhouettes that sit tangent to the flush panels (also with `include_tangent_edges` and `include_seams`): the first elevation came out with only the outer uprights. The elevation is therefore made from the front-plane objects only (front tubes and connectors, knobs, top, front feet; the back ones are hidden behind them anyway), and the four front panels run through `drawing_make2d` on their own. The plan and the section use the full model.
- **Knob diameter.** `annotate` radial dimensions need a true circle, and Make2D joins a circle from two arcs, so the Ø22 dimension is placed on a circle added on the dimension layer.
- **Title block.** In the `DRAWN BY` cell, "RhinoMCP agent" runs slightly into the next cell's border (fixed cell widths in `layout_create`); the hero crops the title block above that row.
