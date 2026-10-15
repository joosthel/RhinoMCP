"""Detail 4: engrave HELIOT EMIL (TM) 0.3 mm deep into the top face of the front band.
Text curves came from geometry_create text + native _Explode via run_command (no text->curves tool)."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpx import call
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
st = json.load(open(STATE))
DEPTH, TOP = 0.3, 7.0
curves = ["o32707", "o32708", "o32709", "o32710", "o32711", "o32712", "o32713",
          "o32716", "o32717", "o32718", "o32719", "o32720", "o32721"]
O_OUT, O_IN = "o32711", "o32712"
body = sys.argv[1]
r = call("geometry_create", {"items": [{"kind": "extrusion", "profile": c, "height": 0.8, "cap": True}
                                        for c in curves], "layer": "_mcp_eval::carabiner::detail"},
         "letter cutters (extrude 13 curves)")
items = r["data"]["items"]
up, down, hmap = [], [], {}
for c, it in zip(curves, items):
    hmap[c] = it["handle"]
    (up if it["bbox"]["max"][2] > TOP + 0.4 else down).append(it["handle"])
# place every cutter at z TOP-DEPTH .. TOP+0.5 (extrusion direction follows curve orientation)
if up:
    call("geometry_transform", {"target": up, "op": "move", "vector": [0, 0, -DEPTH]}, "cutters extruded +Z: down %.1f" % DEPTH)
if down:
    call("geometry_transform", {"target": down, "op": "move", "vector": [0, 0, 0.8 - DEPTH]}, "cutters extruded -Z: up")
r = call("geometry_boolean", {"op": "difference", "a": hmap[O_OUT], "b": hmap[O_IN]}, "letter O: outer minus counter")
o_cut = r["data"]["created"][0]
cutters = [hmap[c] for c in curves if c not in (O_OUT, O_IN)] + [o_cut]
r = call("geometry_boolean", {"op": "difference", "a": body, "b": cutters}, "engrave 12 letters")
if r["ok"]:
    st["body_engraved"] = r["data"]["created"]
    json.dump(st, open(STATE, "w"), indent=1)
