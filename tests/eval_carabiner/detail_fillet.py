"""Detail 1: copy the basic solid to ::detail and soften its four long edges (fillet_edges)."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpx import call
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
st = json.load(open(STATE))
R = float(os.environ.get("FILLET_R", 2.0))
EDGES = json.loads(os.environ.get("EDGES", "[0, 2, 3, 4]"))  # v2 (straight front): [0,2,4,5,6,7,8,9]
r = call("geometry_transform", {"target": [st["body"]], "op": "move", "vector": [0, 0, 0], "copy": True}, "copy basic solid")
h = (r["data"].get("created") or r["data"].get("copies") or r["data"].get("modified"))[0]
call("objects_set_props", {"target": h, "layer": "_mcp_eval::carabiner::detail", "name": "body_detail"}, "copy to ::detail")
r = call("geometry_edit", {"target": h, "op": "fillet_edges", "radius": R, "edges": EDGES}, "fillet horizontal loop edges %s r=%.1f" % (EDGES, R))
if r["ok"]:
    st["detail"] = h
    json.dump(st, open(STATE, "w"), indent=1)
