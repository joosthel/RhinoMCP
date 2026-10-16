# Demo media

`docs/media/demo.mp4`, `demo.gif`, `hero.png` and the stills are produced by two scripts. The model is an original design (a modular sideboard: white steel tubes, spherical connectors, flush navy panels, oak top). There are no logos, brands or text on it.

## Re-record
Needs a running Rhino 7 with RhinoMCP, a **visible** Rhino window, an empty document, Python 3.9 with Pillow, and `ffmpeg` at `/opt/homebrew/bin/ffmpeg`.

```sh
python3 tests/demo/record_demo.py all      # builds the sideboard through MCP tools and captures the raw frames to /tmp/rhinomcp_demo
python3 tests/demo/compose.py              # titles, captions, fades, MP4, GIF, hero and stills into docs/media
```

`record_demo.py` works on the layer tree `_demo`. At the end it deletes everything it made (objects, layers, clipping planes, named set, render materials) and restores the viewport (it saves a temporary named view first). Use `--keep` to leave the model for inspection, then `record_demo.py cleanup`. `record_demo.py details` captures close-ups of a node, a panel corner and a knob for checking.

## Quality choices
- **Anti-aliasing.** The capture guard allows 1,920,000 pixels (`max_capture_pixels`), so 2x (2560 x 1440) is not possible. Captures are 1840 x 1035 and are scaled to 1280 x 720 with Lanczos. All text is drawn at 2x and scaled down, and the camera push-in uses a sub-pixel source box.
- **One look.** Every model shot uses the `Rendered` display mode (white ground, soft shadow, layer materials, no grain), a fixed lens and no grid, axes or selection. Never use `Raytraced`: it renders on all cores and blocks the capture and the MCP server.
- **No camera pulsing.** The turntable has 120 frames at a fixed distance, elevation and lens. Only the azimuth changes, eased.
- **Drawings.** Curves and solid hatches are captured in `Rendered` on white. Thin lines are dilated by about 2 px before the downscale so they survive. Only solid poché is used (no diagonal hatches, so no moire).
- **Transitions.** Scenes change through a 0.4 s dip to white. Captions only change at cuts.
- **Encoding.** MP4 H.264, crf 19, yuv420p, `+faststart`. GIF 960 px, 12 fps, `palettegen stats_mode=diff`, `paletteuse dither=sierra2_4a`, shortened to about 15 s; the MP4 has the full story.

## Known limit
A Front elevation was tried and dropped: Rhino's hidden-line drops the tube silhouettes that sit tangent to the flush panels, so the elevation came out sparse. The video shows a plan cut and a section instead.
