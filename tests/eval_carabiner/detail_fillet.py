"""Detail 1: copy the basic solid to ::detail and soften its four long edges (fillet_edges)."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpx import call
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
st = json.load(open(STATE))
R = float(os.environ.get("FILLET_R", 2.0))
r = call("geometry_transform", {"target": [st["body"]], "op": "move", "vector": [0, 0, 0], "copy": True}, "copy basic solid")
h = (r["data"].get("created") or r["data"].get("copies") or r["data"].get("modified"))[0]
call("objects_set_props", {"target": h, "layer": "_mcp_eval::carabiner::detail", "name": "body_detail"}, "copy to ::detail")
r = call("geometry_edit", {"target": h, "op": "fillet_edges", "radius": R, "edges": [0, 2, 3, 4]}, "fillet 4 loops r=%.1f" % R)
if r["ok"]:
    st["detail"] = h
    json.dump(st, open(STATE, "w"), indent=1)
