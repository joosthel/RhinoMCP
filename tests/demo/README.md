# Demo media

`docs/media/{demo.mp4, demo.gif, hero.png, step_*.png}` are produced by driving a live Rhino 7 through the MCP server and capturing frames with `view_capture`. There is no OS screen recording and no screen-capture permission involved. Everything else (titles, captions, chat bubble, crossfades) is drawn offline with Pillow.

## Re-record
1. Open Rhino 7 with a new, **empty** document. The Rhino window must be visible (captures need it). Leave the viewport as you like: it is saved to a named view and restored at the end.
2. Make sure the RhinoMCP server is running (`MCPStatus`).
3. Install the Python deps once: `pip3 install pillow numpy scipy`. `ffmpeg` must be on PATH or at `/opt/homebrew/bin/ffmpeg`.
4. Run:
   ```sh
   python3 tests/demo/record_demo.py all        # capture (about 90 s) + compose (about 1 min)
   python3 tests/demo/record_demo.py capture    # only the Rhino part
   python3 tests/demo/record_demo.py compose    # only the overlays and ffmpeg (re-uses the frames)
   ```
   Raw frames go to `$DEMO_FRAMES` (default `/tmp/rhinomcp_demo_frames`); delete that folder afterwards. Options: `DEMO_MODE` (display mode, default `Arctic`), `GIF_FPS` (12), `GIF_W` (800).

## What it does
- `record_demo.py` rebuilds the carabiner hoop on layer `_demo::part`, reusing the outline data and build steps of [`tests/eval_carabiner`](../eval_carabiner/): joined outline curves (straight front edge), extrusion minus opening, `fillet_edges` r 2, gate slots and latch notches in one boolean, then `geometry_edit engrave` ("HELIOT EMIL", 0.3 mm). After each step it captures the same fixed camera (1280x720). Then it captures a push-in on the engraving, a 96-frame turntable (170 degrees, fixed target), and `drawing_make2d` for a plan and a plan cut at z = 3.5 (`drawing_clipping_plane`) with hatch.
- When finished (also on errors) it deletes everything it made (objects, clipping plane, layers `_demo*`, named view) and restores the viewport.
- `compose.py` builds the timeline (33 s at 30 fps), blends the turntable captures for smooth motion, writes the MP4 (H.264, yuv420p, crf 23), the GIF (12 fps, 800 px, palettegen/paletteuse), the 1600x800 hero and the stills.

## Notes
- The start of the run asserts that the document has no objects.
- Captures cost about 0.5 s each (about 120 captures); the 1280x720 size stays under `limits.max_capture_pixels`.
- In Arctic mode, `highlight` draws a yellow mesh that looks messy, so the gate step uses a zoomed capture instead.
- Hatch patterns render as thin lines only when the pattern scale is large enough for the capture size; solid is the default.
