"""Extrude the plan curves and cut the opening: one clean closed solid on ::base (z 0..THICK)."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan
from mcpx import call

L = "_mcp_eval::carabiner::"
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
st = json.load(open(STATE))
t = plan.THICK_MM
r = call("geometry_create", {"items": [
    {"kind": "extrusion", "profile": st["outer"], "height": t, "cap": True, "layer": L + "base", "name": "plate"},
    {"kind": "extrusion", "profile": st["inner"], "height": t + 2, "cap": True, "layer": L + "base", "name": "opening_cutter"},
]}, "extrude outer + inner cutter")
(plate, pb), (cutter, cb) = [(i["handle"], i["bbox"]) for i in r["data"]["items"]]
# the extrusion direction follows the curve orientation (can be -Z): place by the returned bboxes
call("geometry_transform", {"target": [plate], "op": "move", "vector": [0, 0, -pb["min"][2]]}, "plate to z 0..t")
call("geometry_transform", {"target": [cutter], "op": "move", "vector": [0, 0, -1 - cb["min"][2]]}, "cutter to z -1..t+1")
r = call("geometry_boolean", {"op": "difference", "a": plate, "b": cutter}, "plate minus opening")
st["body"] = r["data"]["created"][0]
json.dump(st, open(STATE, "w"), indent=1)
print(st)
