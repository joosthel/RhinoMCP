"""Round 4 fix: rebuild the inner-C rib of B further from the edge (inset 4 mm) and union it."""
import json
import mcpx, build, shape as s

st = build.load()
n = "B_rib_inner_C"
mcpx.call("objects_delete", {"target": st["ribs"][n]}, "delete failing inner-C rib solid")
base, top = s.rib_curves(n, inset=4.0)
r = mcpx.call("geometry_create", {"items": [
    {"kind": "curve", "points": build.pts(base), "layer": build.L_CON, "name": n + "_base_v2"},
    {"kind": "curve", "points": build.pts(top), "layer": build.L_CON, "name": n + "_top_v2"}]}, "inner-C rib rails, inset 4 mm")
h = [x["handle"] for x in build._items(r)]
r = mcpx.call("freeform_surface", {"items": [{"kind": "loft", "curves": h, "layer": build.L_CON, "name": n + "_ribbon_v2"}]},
              "inner-C ribbon loft")
rib = build._items(r)[0]["handle"]
r = mcpx.call("geometry_edit", {"target": rib, "op": "offset_surface", "distance": 1.5, "solid": True}, "inner-C ribbon -> 1.5 mm solid")
sol = r["data"]["created"][0]
mcpx.call("objects_set_props", {"target": sol, "layer": build.L_UP, "name": n}, "inner-C rib to element layer")
st["ribs"][n] = sol
build.save(st)
r = mcpx.call("geometry_boolean", {"op": "union", "a": [st["B_partial"], sol]}, "B: + inner-C rib v2")
if r["ok"]:
    st["B_solid"] = r["data"]["created"][0]
    mcpx.call("objects_set_props", {"target": st["B_solid"], "layer": build.L_FIN, "name": "upper_element"}, "B solid to final layer")
    build.save(st)
