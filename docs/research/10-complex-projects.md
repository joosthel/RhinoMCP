# 10 — Capability layer for complex projects

> Source: Sonnet 5.5 research subagent, 2026-10-15. Written from background knowledge, with no live fetches. **[V]** = confident about the API · **[U]** = needs a spike · **[R8]** = Rhino 8 only. Statements about competitors are recalled, not verified.

## Bottom line
Real projects break agents through **unbounded output, blind placement, frozen UI and silent geometric failure**. The fix is a layer between the agent and RhinoCommon:
- **Sets, not GUIDs.**
- **Summaries before lists.**
- **Intent-level operations** that return numeric post-conditions.
- **A job system** for long work.
- **A change journal**, so the agent always knows what changed.

## 1. Failure modes on complex models
1. Enumeration blow-up: 100k GUIDs overflow the context.
2. Blind generation: wrong layers, units and coordinates, and weak 3D arithmetic.
3. UI-thread blocking: long booleans or Make2D trigger timeouts and retries.
4. Silent failure: a null or invalid boolean result gets reported as success.
5. Blocks and linked files: content lives in definitions, with their own units.
6. Unit and tolerance mismatch across sources.
7. Drift: no cheap way to ask "what changed?".

Lessons from other tools **[U]**:
- Revit MCPs use filtered collectors and one transaction per call.
- Fusion and Onshape edit parameters, not raw geometry.
- Zoo/KittyCAD generates replayable programs (KCL), not one-off mutations.

## 2. Understanding the model at scale
- **`model.overview(depth, scope)`** returns a bounded tree (~1–3k tokens): layers → sublayers → block definitions → zones. Each node carries a count, bbox, type histogram and user-text keys. Large lists are cut to top-K plus an "others" bucket with a cursor.
- **Handles** (`o17`) instead of GUIDs: about 3 tokens against ~20. Stale handles produce a typed error.
- **Named sets:** `set.create(name, query)`, `combine`, `get`. Persisted in `Doc.Strings` or user text. Tools take `{set: "walls_L2"}`. **This is the single most important scale feature.**
- **`objects.query`** filters by layer, type, block, user text, bbox, `within_distance`, name regex, material and properties. Aggregates: count, sum of area/volume, group-by. Built on `ObjectEnumeratorSettings` [V].
- **Spatial index:** `Rhino.Geometry.RTree` (`Insert`, `Search`, `SearchOverlaps`) [V]. Invalidated on document events. Build time at 1M objects is **[U]**.
- **Semantic tags:** an `ai.*` user-text namespace (`ai.role`, `ai.level`, `ai.space`, `ai.recipe_id`, `ai.rev`). `tags.infer` proposes tags for the agent to confirm. This works as informal BIM.
- **`model.scene_digest`** gives one line per group, plus a coarse occupancy grid and anomaly list. Its usefulness to the agent is **[U]**.
- **Change journal:** fed by `AddRhinoObject`, `DeleteRhinoObject`, `ReplaceRhinoObject`, `ModifyObjectAttributes` and similar events. `model.changes(since)` separates agent edits from user edits. Every mutating call returns a `state_token`.
- **Blocks:** report definitions with instance counts and nesting. Editing a definition requires explicit confirmation (`InstanceDefinitionTable.ModifyGeometry`). Worksessions are read-only in v2 **[U]**.

## 3. Precise and parametric construction
Rhino has no constraint solver. **v1 uses recipes; v2 adds a Grasshopper bridge.** Rhino history is too fragile to build on.
- **Recipes:** `{inputs, steps, outputs}` stored in `Doc.Strings`, with outputs tagged `ai.recipe_id`. `recipe.run` rebuilds the outputs inside one undo record, which gives parametric edits without a solver. Intent tools record recipes automatically.
- **Frames:**
  - Tools accept a plane, a named CPlane, a level, or `from_object`.
  - The server does all local↔world maths and echoes results in world coordinates.
  - Snap helpers return endpoints, midpoints and closest points.
- **Architecture operations** (each returns a numeric report and handles):
  - `wall_from_centerline`: `Curve.Offset` + `Extrusion`
  - `slab_from_outline`: `CreatePlanarBreps`
  - `opening`: boolean
  - `stair`: n = ceil(H / maxRise), plus a rule check
  - `facade_grid`: `IsoCurve` / `Split`, with planarity deviation and unique panel count
  - levels and grids registry
- **Product operations:**
  - `shell`: `Brep.CreateOffsetBrep`
  - `fillet`: `CreateFilletEdges`, with a binary search for the maximum radius **[U]**
  - `chamfer`: command fallback **[U]**
  - `draft_check`: face normals against the pull direction
  - `parting_line`: normal sign flip across edges
- **Booleans with diagnosis:**
  1. Pre-flight checks: `IsValidWithLog`, `IsSolid`, naked edges, bbox overlap.
  2. Attempt.
  3. Validate the result: validity, solidity, and volume sanity (a union lies between max(Vi) and ΣVi).
  4. Retry ladder:
     - tolerance ×0.1 and ×10
     - `Repair`, `JoinNakedEdges`
     - one cutter at a time
     - nudge 2× tolerance to break coplanar faces
     - split and join
     - command fallback
  5. Diagnosis: coplanar faces, non-manifold geometry, distance from origin.
  6. Roll back on failure.

## 4. Long-running and batch work
- The document is UI-thread only. Pure geometry on *duplicates* can run on worker threads with `Parallel.For`; per-API safety is **[U]**.
- **Jobs:** `submit`, `status`, `result`, `cancel`, `list`. A call switches to async automatically past ~5 s. Progress goes through `notifications/progress`.
- Idle-tick chunking (~50 ms per tick) keeps Rhino responsive. Cancellation uses `RhinoApp.CheckForEscape` plus a cancel flag.
- One undo record per chunk; `job.undo(id)` undoes the whole job.
- **Variants:** `variant.run(recipe, param_grid, metrics)` returns a comparison table at about 20 tokens per row. Scratch work runs in `RhinoDoc.CreateHeadless` to keep the user's undo history clean; `variant.apply(id)` brings the chosen one in.

## 5. Grasshopper as the parametric backbone
- **Definitions as tools (v2):**
  - Scan a folder of `.gh` files.
  - Discover inputs and outputs by nickname convention (`in:Width`, `out:Panels`), with schema ranges taken from slider min/max.
  - Solve, return numbers, and bake with `ai.*` attributes.
- **Agent-authored GH graphs (v3):** possible through `ComponentServer.EmitObject`, but LLM reliability is poor. Prefer GhPython or C# script components, or recipes.

## 6. Verification
Every check returns numbers and an issue list with handles, and can highlight results for `view.capture`.
- **Clearance:** RTree broad phase → `ClosestPoint`.
- **Clashes:** `RTree.SearchOverlaps` → `Intersection.BrepBrep`. Output a clash matrix and the top-K with intersection volume.
- **Wall thickness:** `Intersection.RayShoot` sampling.
- **Area schedule against targets:** `AreaMassProperties` grouped by `ai.space`.
- **Code rules** (stairs, headroom, landings): JSON rule sets. Values come from the skills content and are **[U]** until checked against the codes.
- **Section checks:** `CreateContourCurves` / `BrepPlane` give numbers first, then an image.
- **`verify.audit(changed_since)`** after edits: validity, duplicates, layer vs `ai.role`, distance from origin, tiny objects. Every mutating response carries a `health` line.
- Intent tools state post-conditions, e.g. "expected 12.0 m³, got 11.97".

## 7. Product design specifics
- **Continuity G0–G2:** sample along edges. Measure the gap, the normal angle via `NormalAt`, and the curvature difference via `CurvatureAt`. G3 by finite differences is **[U]**. Use `Curve.IsContinuous` for curves.
- Zebra has no numeric output. Use curvature and draft-angle sample maps instead.
- **Manufacturability:** minimum wall thickness, minimum concave radius, undercuts (ray casting), tiny or sliver faces, naked and non-manifold edges, watertight meshes.
- **STEP/IGES:** pre-flight checks, export through the command, then re-import into a headless document and compare volume, faces and bbox **[U]**.

## Tiers
**v1 — required for complex work**
- `model.overview`
- `objects.query` (filters, aggregates, cursor)
- Handles and named sets; bulk operations on sets, chunked
- `ai.*` tagging
- Change journal and `state_token`
- Job system (async, Idle chunking, Escape)
- Frame-aware construction
- Boolean retry ladder
- `verify.audit`
- Measurements on sets
- Clash detection
- Block report
- Section check
- `wall_from_centerline`, `slab_from_outline`, `opening`

**v2**
- Recipes with `recipe.run`
- RTree cache and `objects.near`
- Scene digest
- Variants in headless documents
- Worker-thread geometry
- Stairs, façade grid, levels and grids
- Area schedules and code rule sets
- Shell, fillet search, draft and parting line
- Grasshopper definitions as tools
- Wall-thickness analysis
- Linked-file report
- STEP pre-flight checks and round-trip

**v3**
- G0–G3 report
- Agent-authored GH graphs
- Undercut and mould analysis
- Snapshot diffs
- Compute offload
- Automatic tag inference
- R8 add-ons (SubD tools, ShrinkWrap, Python 3)

## Contract rules
1. Lists are bounded (default 50) with `total`, `returned` and `next_cursor`.
2. Mutating responses return `state_token`, counts and a `health` line.
3. Errors are typed and carry remediation, e.g. `BOOLEAN_FAILED{cause, tried, suggestion}`.
4. Set-based tools never require GUIDs.
5. Units are echoed with every number.
6. Skills teach the loop: overview → query by set → verify numerically → capture.

## Spikes to add to Phase 0
1. Idle-tick cadence and `CheckForEscape` under Mono.
2. RTree build time at 100k and 1M objects.
3. Worker-thread safety of booleans on duplicates.
4. Booleans and Make2D in `RhinoDoc.CreateHeadless`.
5. Loading and solving Grasshopper from the plugin on R7 Mac.
6. Event names and agent/user attribution.
7. STEP export route on R7 Mac.
