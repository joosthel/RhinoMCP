# Findings: how far can an MCP server drive Rhino 7 on a Mac?

*Research summary, 2026-10-15. RhinoMCP v0.2 on Rhino 7.39 for Mac, an Intel build running under Rosetta on Apple Silicon.*

## Bottom line
An MCP server can drive Rhino 7 on a Mac far enough to do real design work. It works well for precise, near-2D product parts, clean architectural drawings, model organization and large models. It holds up on freeform work when the agent picks a light strategy. It breaks down when the agent has to infer 3D form from a single photo, or when it picks a heavy method.

The limiting factors are not missing APIs. They are:
1. how well the agent reads the brief or photo;
2. which modelling strategy it picks;
3. Rhino 7's single-threaded speed under Rosetta;
4. a few macOS UI realities: the window must not be minimized, redraws happen asynchronously, and modal dialogs freeze Rhino.

## What was built
| | |
|---|---|
| Architecture | An in-process C# plugin with an SDK-free JSON-RPC MCP server on 127.0.0.1 (bearer token, Host and Origin checks), plus a hot-reloadable tools DLL. It is built with Rhino's own bundled Mono and Roslyn, so no .NET SDK is needed. |
| Tools | 52, including: context, objects and sets, layers and schemes, geometry, freeform and SubD, measurement and clash checks, views, clean Make2D, layouts and export, model overview, change journal and audit, scripting (IronPython 2.7, C# via Roslyn), resource guard |
| Agent guidance | 7 skills, packaged as a Claude Code plugin |
| Tests | 214 live integration tests against the running Rhino |
| Process | 34 merged pull requests, built by parallel Sonnet 5.5 agents with an Opus 5.5 orchestrator; Opus 5.5 ran the two hardest evaluations |

## Evaluations
| Task | Result | Effort | Report |
|---|---|---|---|
| Freeform primitives: vase, hood panel, SubD stool | All valid. G1 blends worked; true G2 against doubly curved edges isn't possible with the R7 API | 37 calls, 0 failures | [freeform](eval/freeform/REPORT.md) |
| Clean Make2D on a 2-storey building and a vase | **120 curves vs 320 from native Make2D**. Lines and arcs instead of NURBS, closed and hatched sections, exact 1:100 | 0.4–0.6 s per drawing | [drawing](eval/drawing/REPORT.md) |
| A2 sheet at 1:100 with PDF and DXF export | Scale exact, vector PDF, DXF with no SPLINEs from clean drawings | – | [layout](eval/layout/REPORT.md) |
| 20,000-object model | Overview in 0.9 s (~460 tokens); nearest-object query 4 ms warm; change journal 0.01 s | – | [scale](eval/scale/REPORT.md) |
| Two-element paper sculpture from one photo (Opus) | Plan and silhouette matched (camera fitted to 0.9 px). 3D form was guessed and over-engineered | 368 calls, 14 failed, 90 s timeouts | *(materials removed: third-party design)* |
| Flat product part from a photo (Opus), basic shapes then details | Two clean solids. Outline within about 1.3 mm of the trace, exact straight edge after owner feedback | 137 calls (about 40 for a clean replay), slowest 2.2 s | *(materials removed: third-party product)* |

## What we learned

### 1. Strategy matters more than tool count
The sculpture and the flat product part used the same server. The sculpture run used RBF height fields and doubly curved, laminated offsets: 368 calls, faces with 44×57 control points, and boolean timeouts. The product-part run used light plan curves, one extrusion, one boolean, and details as small cuts: about 40 calls on replay, 2 s at worst.

The owner's observation was decisive: "the original model is really light, no fancy curvature". Agents default to heavier geometry than designers use. The skills now push "basic shapes first, details after", and the resource guard enforces lightness.

### 2. Reading the photo is the bottleneck, and it happens outside the server
Both photo evaluations got their decisive step from local Python: camera fitting, homography or back-projection, and tracing. The server cannot see the reference image. A photo-matching or back-projection tool is the most valuable missing capability.

### 3. Numbers beat screenshots, but screenshots have to be reliable
Agents verify best with numbers: `geometry_validate`, `measure`, `clash_check`, curvature and continuity analysis, and the Make2D quality report.

Captures are needed for fidelity checks, and they were the least reliable part:
- Mac redraws are asynchronous, so a capture could return the previous frame.
- A minimized window means no capture at all.
- The 4-view sheet framed the whole document.

These are fixed now. Captures still cost about 0.3 s each to let the redraw settle.

### 4. Rhino 7 on Mac has sharp edges an agent can't see
Each of these was found live and handled in the server:

| Quirk | Effect | Handling |
|---|---|---|
| Modal dialogs (`What`, `Options`, …) don't pump UI callbacks | Rhino freezes until a person closes the dialog | Blocked up front |
| Commands waiting at a prompt | The call hangs | Native Escape after a timeout; the prompt and options are returned to the agent |
| `SendKeystrokes` while Rhino isn't frontmost | Ignored | Escape is posted as an NSEvent into Rhino's own queue |
| `_Undo` sent through `RunScript` | Does nothing | `history_undo` uses `RhinoDoc.Undo()` |
| Rhino keeps an undo record open between commands | MCP edits merged into unrelated undo steps | The stray record is closed first; no-op records are skipped |
| `System.Drawing` (GDI+) | A crash from finalizers; `MissingMethodException` once a second copy was loaded | Never used; assembly resolvers use an allow-list |
| Layer and layout edits | Can't be undone | Confirmation counts required |
| Clipping-plane side convention | Opposite between objects and the Make2D API | Normalized |

### 5. Hardware is the real ceiling
Rhino 7 runs as x86_64 under Rosetta, mostly single-threaded. Memory was never the problem on this 36 GB machine; CPU time was. A 40-point ribbon took 23–31 s to thicken, and its 16-point version took 2.2 s.

The resource guard estimates the cost before any heavy work: control points, SubD levels, mesh density, capture pixels, and the input size for booleans, clash checks and Make2D. Over a limit, it refuses with a concrete fix. Booleans, clash checks and fillets also have time budgets.

### 6. Token efficiency comes from structure, not compression
These carried most of the token savings:
- one `context_get` call at the start;
- counts before lists, bounded pages and cursors;
- short handles (`o17`) and named sets, so agents act on thousands of objects without seeing their IDs;
- numeric post-conditions;
- errors that list valid options and the closest layer names.

A 20k-object model is summarized in about 460 tokens.

### 7. Parallel agent development works, with discipline
Six to seven Sonnet agents built tool families in parallel against one live Rhino. That needed:
- a test lock, with each worktree loading its own DLL build;
- hot reload of the tools DLL, so only core changes need a Rhino restart;
- strict rules: dispose everything, never let a callback throw, never `pkill` by pattern.

Two failures are worth recording:
- An undisposed GDI+ object crashed Rhino.
- A minimized Rhino window cascaded into about 70 failing tests.

Both are guarded now.

## What's still missing (ranked)
1. **Photo matching:** fit a camera from known ground points and back-project traced pixels onto a plane or surface. This is where most of the effort went in both photo evaluations.
2. **Grasshopper:** load and solve definitions, and expose them as tools (NodeInCode, `GH_Document`). The research is done; it isn't built yet.
3. **Python 3 sidecar** (numpy, shapely, trimesh, COMPAS) for compute that IronPython 2.7 can't do.
4. **Job system** for work longer than about 60 s: chunk it across idle ticks, with progress reporting and Escape to cancel.
5. **Architecture and product helpers:** walls, slabs, stairs, façade grids, draft and thickness checks (designed in [research/10](research/10-complex-projects.md)).
6. **Off-screen capture,** so captures don't flicker in the user's viewport.

## Verdict
For a single designer on a Mac, an MCP server for Rhino 7 is a capable assistant today. It is strongest at organizing models, producing clean drawings at scale, exporting, measuring and checking, and building light parts from a clear brief. It can also go from a photo to a near-2D object with designer-level feedback in the loop.

It is not yet an autonomous sculptor. Inferring 3D intent from a single image, and choosing the light strategy unprompted, still need a person: in this project, the owner's two corrections ("it's light", "the edge is straight") changed the outcome more than any tool did.
