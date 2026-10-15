# Architecture

RhinoMCP is a research MCP server for **Rhino 7 on macOS**, including Intel/Rosetta setups. Rhino 8 and Windows are optional add-ons. The design rationale is in [`research/00-synthesis.md`](research/00-synthesis.md), and the Rhino 7 Mac findings are in [`research/11-r7-mac-inventory.md`](research/11-r7-mac-inventory.md).

## Components

```
Claude Code / Cursor / Codex ──── Streamable HTTP ────┐
Claude Desktop ── stdio ── rhinomcp-bridge (Swift, M5) ┤
                                                       ▼  http://127.0.0.1:7340/mcp  (Bearer token)
┌──────────────────────── Rhino 7 process (Mono 6.12, x86_64) ────────────────────────┐
│ RhinoMCP.rhp  (core, rarely changes)                                                 │
│   Server/   HttpListener · MCP JSON-RPC · auth · discovery file                     │
│   Core/     ITool contract · ToolRegistry · UiDispatcher · UndoScope · Handles      │
│   Commands/ MCPStart · MCPStop · MCPStatus · MCPConnect                             │
│        │ loads (from bytes, hot-reloadable)                                         │
│        ▼                                                                             │
│ RhinoMCP.Tools.<stamp>.dll  (all tools; discovered by reflection)                   │
│   Tools/Context · Objects · Sets · Layers · Geometry · Measure · View · Drawing ·   │
│         Script · History · Jobs · Verify · Grasshopper · Guidance                   │
└──────────────────────────────────────────────────────────────────────────────────────┘
        optional M4: Python 3 sidecar (arm64, uv-managed) via stdio JSON-lines
```

### Why it is built this way
- **In-process plugin.** On Rhino 7 Mac, a plugin is the only way to control a running Rhino.
- **HTTP served directly from the plugin.** Claude Code supports it natively, so no runtime is needed for the main path. The hand-written JSON-RPC layer has no SDK dependency, which avoids assembly conflicts under Mono/net48. McNeel's RhinoAI uses the same approach.
- **Core and tools split.** The tools assembly is loaded from bytes under a unique name per build. `dev_reload` swaps it without restarting Rhino, which keeps the development loop fast. Core changes still need a Rhino restart.
- **Toolchain.** Rhino's bundled Mono and Roslyn `csc.exe` (C# 7.3+) compile everything. No .NET SDK is required.

## Server

| Item | Value |
|---|---|
| Bind | `127.0.0.1:7340`. If the port is busy, the next free port in `7340–7359` is used. |
| Endpoint | `POST /mcp` (JSON-RPC 2.0, `application/json` response). `GET /mcp` returns 405. `GET /health` returns `{ok, version}`. |
| Auth | `Authorization: Bearer <token>`. The token is 32 random bytes as hex, persisted in `~/Library/Application Support/RhinoMCP/config.json` (mode 0600). |
| Host/Origin | Requests are rejected unless Host is `127.0.0.1:<port>` or `localhost:<port>`. Requests carrying any `Origin` header are rejected. |
| Discovery | `~/Library/Application Support/RhinoMCP/instances/<pid>.json`, containing `{pid, port, version, startedAt}`. The file is removed on exit. |
| Batching | JSON-RPC batch arrays are rejected with -32600 (not needed by Claude Code). |
| Methods | `initialize` (echoes a supported `protocolVersion`: 2025-11-25, 2025-06-18 or 2025-03-26) · `notifications/*` (202) · `ping` · `tools/list` · `tools/call` · `resources/list` · `resources/read` · `prompts/list` · `prompts/get` |
| Startup | The server starts on the first `RhinoApp.Idle` after load, if `autoStart` is set (default true). |

Server `instructions`: about 300 tokens of "how to use this server" text, generated from `skills/rhino-mcp-efficient-use`.

## Tool contract (`Core/ITool.cs`)

```csharp
public interface ITool {
    string Name { get; }            // snake_case, [a-z0-9_], ≤ 48 chars (Claude tool-name safe; no dots)
    string Description { get; }     // brief to a new hire: what, when, limits, failure modes
    string InputSchema { get; }     // JSON Schema (draft 2020-12) as a string
    ToolKind Kind { get; }          // Read | Mutate | Destructive | Script  → annotations + undo
    ToolResult Execute(ToolContext ctx, JObject args);   // runs on the UI thread
}
```

- **Discovery.** `ToolRegistry` reflects over the tools assembly and registers every concrete non-abstract `ITool` that has a public parameterless constructor. Duplicate names fail loudly.
- **Dispatch.** Each call goes HTTP thread → `UiDispatcher.Invoke` (`RhinoApp.InvokeOnUiThread` + `TaskCompletionSource`, default timeout 60 s) → `Execute`.
- **Undo.** If `Kind` is `Mutate`, `Destructive` or `Script`, the registry wraps the call in `doc.BeginUndoRecord("MCP: <name>")` / `EndUndoRecord`. It then calls `doc.Views.Redraw()` and attaches a `state_token`.
- **Annotations.** `Read` maps to `readOnlyHint: true`. `Destructive` and `Script` map to `destructiveHint: true`.
- **Own undo.** A tool implementing the marker interface `IManagesOwnUndo` is not wrapped in an undo record (used by `run_command`, whose commands, including `_Undo`, manage undo themselves). The registry still redraws and bumps `state_token`.
- **Schemas.** Tools write `$ref: "#/$defs/target|point|plane"`; `tools/list` injects the `$defs` block.
- **Core tools.** `server_info` and `dev_reload` (devMode only) live in the core. Both are `Read` kind because they do not touch the model.
- **Argument errors.** `ArgReader` gathers *all* argument errors into one `INVALID_ARGS` result.
- **Strict arguments (Core 0.2.0).** Before `Execute`, `ToolRegistry.ValidateArgs` runs `ArgValidator` over the tool's `inputSchema`: unknown top-level names (when `additionalProperties:false`, which every tool declares) with a "did you mean" and the valid names, missing `required` keys, and basic JSON types (`string|number|integer|boolean|array|object`). All problems are collected into one `INVALID_ARGS` (`details.errors|unknown|valid`) and the tool does not run. `null` counts as absent. Properties without a plain `type` (`$ref`, `oneOf`) and nested objects are left to the tool. Tools whose arguments are easy to confuse also reject unknown names themselves (`ArgCheck`, e.g. `objects_query` with filter keys placed beside `filters`), so they stay strict on an older Core.
- **Agent-call hook (Core 0.2.0).** `ToolRegistry.InToolCall` / `CurrentTool` (thread-static, set around `tool.Execute` in try/finally, nesting-safe) tell event handlers that the agent is acting. The change journal reads `InToolCall` by reflection and falls back to the stack walk on an older Core.

### ToolContext
`Doc` (`RhinoDoc.ActiveDoc`) · `Units` ("mm") · `Tol` (absolute tolerance) · `AngleTolDeg` · `Handles` (session handle ↔ GUID map) · `Sets` (named-set store) · `Log(string)`.

### ToolResult
```json
{ "ok": true, "data": { … }, "units": "mm", "state_token": 42,
  "warnings": ["…"], "next_cursor": null }
{ "ok": false, "error": { "code": "BOOLEAN_FAILED", "message": "…", "guidance": "…", "details": { … } } }
```
- The payload is serialized compactly as a single `text` content block. Images are added as `image` blocks (PNG or JPEG, base64).
- `isError: true` is set whenever `ok` is false. JSON-RPC errors are used only for protocol faults.
- Common codes: `INVALID_ARGS`, `RESOURCE_LIMIT`, `NOT_FOUND`, `NO_DOCUMENT`, `TIMEOUT`, `GEOMETRY_FAILED`, `BOOLEAN_FAILED`, `SCRIPT_ERROR`, `UNSUPPORTED`, `INTERNAL`.

### Conventions every tool follows
1. **Addressing.**
   - Objects are referenced by handle (`o12`), GUID, or `{set: "name"}` / `{query: {...}}`.
   - **`{query}` targets are resolved by one code path** (`ObjectQuery.Resolve` / `TargetOf`, same filter as `objects_query`) in every tool; do not use Core `ArgReader.Target` in tools. Filters combine with AND. **Hidden objects are included by default, in every tool** (read, measure and mutate alike); pass `include_hidden:false` inside the query to exclude them. `visible:true|false` selects by visibility explicitly.
   - Tools return handles by default. GUIDs only when `include_guids: true`.
2. **Bounded output.**
   - Lists default to `limit: 50`, with `total`, `returned` and `next_cursor`.
   - Counts and aggregates come before lists.
3. **Units.**
   - All lengths are in document units, and the unit is echoed back.
   - Points are `[x, y, z]`.
   - Planes are `{origin, x_axis, y_axis}` or a name: `world_xy`, `world_yz`, `world_zx`, a named CPlane, or a level.
4. **Mutating tools** return the handles they created, modified or deleted, plus the result bbox.
5. **Failure handling.** Never fail silently. Validate geometry results (null, `IsValid`, sanity checks) and report the failure.
6. **Errors teach.** An error explains what failed, why, which values are valid, and which tool to call next.

## Threading & long work
- RhinoDoc access happens only on the UI thread.
- Pure geometry on duplicated objects may run on worker threads, via `Jobs` (M3).
- Jobs process their work in chunks across `RhinoApp.Idle` ticks of about 50 ms, and check `RhinoApp.CheckForEscape`. Calls expected to exceed about 5 s return a `job_id`.

## Scripting
| Tool | Engine |
|---|---|
| `run_python` | IronPython 2.7.12 via `Rhino.Runtime.PythonScript`. `result` comes back JSON-serialized and stdout is captured. |
| `run_csharp` | Roslyn 3.9 `CSharpScript` from the Mono 4.5 lib folder, with the Mono.CSharp evaluator as fallback. |
| `run_command` | `RhinoApp.RunScript(cmd, echo)` with captured command history. |
| Python 3 (M4) | Optional sidecar. numpy, shapely, trimesh, networkx, rhino3dm. |

Script tools are gated by `allowScripts` in `config.json` (default true for this research project).

## Repository layout
```
src/Core/          RhinoMCP.rhp: plugin, server, contracts, dispatcher, commands
src/Tools/         RhinoMCP.Tools.dll: one folder per tool family
skills/            Agent skills (SKILL.md + references/), also served as MCP resources
scripts/           build.sh · install.sh · mcp.sh (curl helper) · dev-test.sh (locked integration run)
tests/             integration tests (python3 stdlib, talk to the live server)
bridge/            Swift stdio↔HTTP bridge (M5)
docs/              research/ · ARCHITECTURE.md · PLAN.md · DEVELOPMENT.md · DECISIONS.md
```

## Security notes
- The server listens on loopback only, requires a token, and checks Host and Origin.
- Script tools execute arbitrary code by design. Keep the token private.
- `dev_reload` exists only when `devMode` is true.

## Interactive commands (learned on Rhino 7.39 Mac)
- **Commands waiting at a prompt** (GetPoint/GetOption) keep pumping UI callbacks. `run_command` arms a watchdog with `input_timeout_s` (default 10 s) that injects Escape (`!`). It returns `COMMAND_NEEDS_INPUT` with the exact `last_prompt` and its options, so the agent can retry with every input inline.
- **Background cancel:** `RhinoApp.SendKeystrokes` does nothing while Rhino is not the frontmost app. The watchdog therefore posts a native Escape key event into Rhino's own AppKit queue (`[NSApp postEvent:]` via the Objective-C runtime), which works in the background. It retries every 2 s, up to 4 times.
- **Modal windows** (`What`, `Options`, …, and non-dash `Export`/`Print`/`Hatch`/…) do *not* pump callbacks. They freeze Rhino until a person closes them. `run_command` refuses them up front with `COMMAND_BLOCKED`.
- **`_Undo` via `RunScript` is a no-op.** Use `history_undo` / `history_redo`, which call `RhinoDoc.Undo()` / `Redo()`.
- **A cancelled command can leave an undo record open.** While it is open, `Undo()` fails and later edits pile into it. The tools close a stray record (`CurrentUndoRecordSerialNumber`) when no command is running.
- **`CapturedCommandWindowStrings` is empty on Mac.** `run_command` falls back to diffing `CommandHistoryWindowText`.

## System.Drawing is unusable on Rhino 7 Mac
- libgdiplus is not usable in-process: `new Bitmap(w,h)` throws `TypeInitializationException` (GDIPlus), and half-built GDI+ objects with throwing finalizers can take Rhino down (Mono exits on an unhandled finalizer exception).
- `RhinoView.CaptureToBitmap`, `ViewCapture` and `ViewCaptureSettings` throw `MissingMethodException` from a hot-loaded tools DLL, and `ImageCodecInfo`/`EncoderParameters` do not exist, so there is no JPEG quality control. Do not reference `System.Drawing` types in tools.
- View tools instead drive the active viewport briefly (try/finally restore), run `-_ViewCaptureToFile` (options go **before** the quoted file name, otherwise it saves at once with the sticky defaults) to a temp PNG and read the bytes. `src/Tools/View/Png.cs` is a small managed PNG codec used to compose the 2x2 sheet. Output is PNG only.
- **Rhino keeps an undo record open outside commands.** While it is open, `BeginUndoRecord` returns 0 and edits merge into that shared record. Then `history_undo` reverts unrelated work. `UndoScope` therefore closes a stray record when no command is running, and only then opens its own.
- **`System.Drawing` duplicates.** Only one `System.Drawing` may be loaded: Rhino's own copy in `RhCore.framework/Resources/lib/mono/gac`. An `AssemblyResolve` handler that loads Mono's copy makes every Color-taking RhinoCommon call throw `MissingMethodException`. Resolvers must use an allow-list.

## Drawings: Make2D and clipping planes (learned on Rhino 7.39 Mac)
- **`HiddenLineDrawing` works in a hot-loaded tools DLL** without System.Drawing: build a `ViewportInfo` by hand (`SetCameraLocation/Direction/Up`, `SetScreenPort(int...)`, `ChangeToParallelProjection(true)`, `SetFrustum`), `HiddenLineDrawingParameters.Flatten = true` returns curves in the view's XY plane (x right, y up, not mirrored), `AddGeometry(geom, xform, tag)` carries an `int` tag back through `ParentCurve.SourceObject.Tag`. Compute takes 0.2 to 0.6 s for small models and runs on the UI thread (the 60 s dispatcher limit applies).
- **Clipping side differs.** Rhino's clipping-plane *objects* keep the side the plane normal points to. `HiddenLineDrawingParameters.AddClippingPlane(plane)` keeps the opposite side, so `drawing_make2d` flips every plane it passes (verified against the native `-_Make2D` extents).
- **`ObjectTable.AddClippingPlane` returns `Guid.Empty` for an empty viewport list.** `drawing_clipping_plane` creates the plane on the active view and calls `RemoveClipViewport` so the plane clips nothing on screen.
- **Section lines shared by two solids** (wall standing on a slab) are flagged `Duplicate` on one solid. Dropping them leaves that solid's section loop open, so `Duplicate` segments of type `SectionCut` are kept and de-overlapped per source object only.
- **`Curve.GetBoundingBox(false)` of an `ArcCurve` is the loose control-polygon box** (a circle comes out 1.37 times too big). Use `GetBoundingBox(true)` for placement and extents.
- **Rhino already de-duplicates solids**: coincident edges, stacked copies and overlapping line inputs come back as `Duplicate` segments, so `overlaps_removed` is normally 0. Free-form section pieces can still arrive twice (seam and section plane coincide); the overlap pass removes them. `keep_engine_duplicates: true` feeds Rhino's duplicates through the overlap pass for diagnosis.
- **Native `-_Make2D` scripting:** select the objects first (no select prompt appears), then `-_Make2D _ShowTangents=No _CreateHiddenLines=No _CreateSceneSilhouette=No _CreateClippingPlaneIntersections=Yes _ShowViewRectangle=No _GroupOutput=No _Enter`. It uses the active viewport's projection and the clipping planes that clip that viewport. All its output curves are `NurbsCurve` (even straight lines), unjoined, one layer per visibility. Its option values are sticky in Rhino. It adds layers (`Make2D::...`) that must be deleted again.
- **Output curves are placed with `Transform.Translation * Transform.Scale`** about the drawing's lower-left corner; `scale` 1:100 shrinks in document units (1 m of model becomes 10 mm in a mm document).
## Layouts, annotation and export (learned on Rhino 7.39 Mac)
- **Page views.** `ViewTable.AddPageView(name, w, h)` takes page units (`doc.PageUnitSystem`); `RhinoPageView.AddDetailView(title, corner0, corner1, projection)` returns the `DetailViewObject`. `DetailView.SetScale(100, mm, 1, mm)` makes 1 paper mm = 100 model mm in any document units; read it back from `PageToModelRatio` (0.01) or by transforming two points with `WorldToPageTransform`. Page-space objects need `Attributes.Space = PageSpace` and `ViewportId = pageView.MainViewport.Id`. Creating a page view activates it, so tools restore the previous active view (view tools refuse page views).
- **Per-detail layers.** `Layer.SetPerViewportVisible(detailViewport.Id, bool)`; clean up with `DeletePerViewportSettings` when the layout goes.
- **Annotation scaling.** In a detail, a dimension style with `TextHeight` = printed mm and `DimensionScale` = denominator prints at exactly the printed size (model space shows `TextHeight x DimensionScale`). A style with `TextHeight` already multiplied by the denominator prints 100x too large. Hatch pattern scale is annotative the same way (printed spacing = base spacing x scale paper-mm, independent of the detail scale; measured in PDF).
- **Aligned dimensions** are only measured correctly when the plane's x axis runs along the dimension (`LinearDimension.Create(Aligned, ...)` otherwise measures the plane-x projection).
- **PDF.** `FilePdf.Create()` + `new ViewCaptureSettings(pageView, dpi)` + `AddPage` + `Write(path)` works from the hot-loaded tools DLL (no System.Drawing types in the signature used). Output is vector with a white background image. `FilePdf` is not `IDisposable`.
- **Scripted `-_Export`** after the file name: DWG/DXF prompt `Write file using scheme "Default" ( Scheme FromIni )` (one Enter; `_Scheme "<name>"` selects a named scheme, unknown names fall back); AI one Enter; STEP one Enter (`_Schema` lists AP203/AP214/AP214CC2); STL and OBJ two Enters (format option, meshing option); SVG and 3dm none. An extra Enter repeats the last command. Selected objects are exported when there is a preselection; hidden or locked objects cannot be selected. DXF is `AC1021` with 2D `POLYLINE`/`VERTEX`; NURBS curves become `SPLINE`.
- **SVG/AI** exports are view-based (duplicated paths, 2400 pt canvas), not a faithful selection export.
- `view_capture` does not render page views; render the exported PDF outside Rhino instead (macOS: `sips -s format png`).

## Change journal and hot reload (`src/Tools/Model/ChangeJournal.cs`)
- The journal subscribes to `RhinoDoc` Add/Delete/Undelete/Replace/ModifyObjectAttributes and `LayerTableEvent`. Handlers never throw.
- **Tool DLLs are reloaded without unloading**, so event subscriptions of older DLLs stay alive. The journal therefore keeps its ring buffer (primitive arrays) and an "uninstall" `Action` in `AppDomain` data. A newer DLL adopts the buffer (tokens keep counting), runs the stored uninstall (unsubscribes the old delegates) and subscribes its own. Each handler also carries a generation number and returns at once if it is no longer the current one. Result: exactly one journal records each event.
- **Actor (agent vs user):** Core >= 0.2.0 sets `ToolRegistry.InToolCall` around `tool.Execute`; handlers run synchronously on the UI thread, so the flag means an MCP tool (including `run_command` / `run_python`) caused the event. On an older Core the journal falls back to scanning the stack for a `ToolRegistry` frame.
- `objects_near` keeps an RTree over object bboxes, rebuilt when the journal epoch (bumped by add/delete/undelete/replace) or `Objects.Count` changes.

## Resource guard (`src/Tools/Shared/Resources.cs`)
Rhino 7.39 Mac runs x86_64 under Rosetta and is mostly single-threaded, so heavy or high-resolution geometry makes it sluggish or crash. Heavy tools estimate cost **before** computing and return `RESOURCE_LIMIT` (`ok=false`, numbers in `details`, concrete `guidance`) instead of working past the limits.

- **Limits** are read by the tools from the optional `limits` object in `~/Library/Application Support/RhinoMCP/config.json` (re-read when the file changes; Core `Config` is untouched, unknown keys are ignored). Defaults:

| Key | Default | Guards |
|---|---|---|
| `max_surface_control_points` | 2500 (50x50) | `freeform_surface` per item (grid size, `rebuild_surface` counts, patch spans, loft rebuild) |
| `max_batch_control_points` | 20000 | `freeform_surface` per call, summed over items |
| `max_subd_levels` | 2 | `subd` `subdivide` levels |
| `max_subd_faces` | 50000 | `subd` subdivide result, `to_brep`, `offset`, `create_from_mesh` input |
| `max_mesh_faces` | 500000 | `system_status` recommendation for mesh objects |
| `max_capture_pixels` | 1600x1200 (number = pixels, or `[w,h]`) | `view_capture` width x height |
| `make2d_max_input_faces` | 20000 | `drawing_make2d` source faces |
| `boolean_max_face_count` | 5000 | `geometry_boolean` total input faces (the retry ladder multiplies the cost) |
| `clash_max_pairs` | 5000 | `clash_check` `max_pairs` argument |
| `boolean_budget_ms` | 30000 | `geometry_boolean` `budget_ms`: checked between retry-ladder steps, structured `TIMEOUT` with step reached and diagnosis hints (a single kernel call cannot be interrupted) |
| `fillet_budget_ms` | 30000 | `geometry_edit` `fillet_edges` `budget_ms` default: checked between the two retry steps (DistanceFromEdge, RollingBall); structured `TIMEOUT` with `tried[]` (one kernel call cannot be interrupted) |
| `clash_budget_ms` | 30000 | `clash_check` `budget_ms` default; on expiry partial results plus `data.timeout {pairs_tested, pairs_total, hints}` |
| `warn_rss_mb` | 4000 | warning + `resources` block in results |
| `block_rss_mb` | min(12000, 40% of RAM) | all guarded heavy tools refuse; **never** forceable |
| `warn_call_ms` | 10000 | warning + `resources` block when a call is slower |

- **`geometry_edit` `max_cp`** (`offset_surface`, `shell`; default `max_surface_control_points`): reports face control-point counts (`items[].cp`) and rebuilds a result that is one untrimmed face above it; joined or trimmed results only get a warning.
- **`force: true`** (tool argument) overrides the count limits of that tool only. It never overrides `block_rss_mb`.
- **`view_capture`** over the pixel limit is refused (RESOURCE_LIMIT with a suggested smaller size), not silently clamped, so the caller knows what it gets. Default capture sizes are unchanged (768x512; sheet 1024x768).
- **Post-call accounting.** Guarded tools end with `Resources.Begin(tool)` ... `guard.Finish(result)`. `data.resources = {rss_mb, rss_delta_mb, call_ms, doc_cp_total?}` and a warning are attached only when notable (RSS growth > 200 MB, call > `warn_call_ms`, or RSS > `warn_rss_mb`), to keep tokens low.
- **Process stats.** RSS and private bytes come from `Process.GetCurrentProcess()` (`WorkingSet64`, `PrivateMemorySize64`; verified under Rhino's Mono 6.12), with `ps -o rss=` as fallback, cached 2 s. Free system memory is parsed from `vm_stat` (free + inactive + speculative + purgeable pages), cached 10 s. Rosetta is `sysctl sysctl.proc_translated`; RAM `hw.memsize`; cores `hw.physicalcpu` / `Environment.ProcessorCount`.
- **Document complexity.** `Resources.DocComplexity` totals NURBS control points (per `Brep.Surfaces`, so shared surfaces count once), brep faces, mesh vertices/faces, SubD faces and cached render-mesh faces (`RhinoObject.GetMeshes(MeshType.Render)`, which never creates meshes). The scan is time-boxed (2 s, `partial` flag) and cached per `ChangeJournal.Epoch` + object count (5 s without a journal).
- **`system_status` counts live objects only.** `document.objects` excludes deleted objects; `document.undo_held_deleted` reports how many deleted objects the undo history still holds in memory (`doc.Objects.Count` includes them). A large number comes with a recommendation to save and reopen the file.
- **Tools:** `system_status` (Read; process, system, document, top-5 heaviest with handles, limits, recommendations) and `performance_mode` (Mutate; `on` sets `RhinoDoc.MeshingParameterStyle` to `Fast` and the active model viewport to Shaded unless already light; the previous style and display mode are stored in `doc.Strings["rhinomcp.perfmode"]`, `off` restores them; a previous `Custom` style is remembered in AppDomain data only, lost on a tools reload).
- **Desired Core hooks** (not implemented, Core needs a Rhino restart): a per-call watchdog in `UiDispatcher` that aborts or warns on calls beyond `warn_call_ms`, `limits` exposed through `Config`, (`server_info` now carries `rss_mb` and `rosetta`, Core 0.2.0).
=======
- **Desired Core hooks** (not implemented, Core needs a Rhino restart): a per-call watchdog in `UiDispatcher` that aborts or warns on calls beyond `warn_call_ms`, `limits` exposed through `Config`, and `server_info` carrying the RSS and Rosetta flag.

## Designer curves, text and engraving (learned on Rhino 7.39 Mac)
- **Text to curves:** `Curve.CreateTextOutlines(text, font, height, style, closeLoops, plane, smallCaps, tol)` works in the hot-loaded DLL (no System.Drawing involved) and returns a flat array of closed loops. There is no per-glyph grouping, so `DesignerCurves.GroupGlyphs` nests loops by containment (even depth = outer, odd = counter of the smallest container; islands inside counters become their own glyph).
- **Engrave/emboss:** each glyph becomes a planar brep (`Brep.CreatePlanarBreps` handles counters), extruded with `BrepFace.CreateExtrusion(path, cap)` starting a margin outside (engrave) or inside (emboss) the face, then one `CreateBooleanDifference`/`CreateBooleanUnion`. The outward direction is confirmed with `IsPointInside` on both sides of the face point, so face orientation never matters.
- **Periodic control-point curves:** `NurbsCurve.Create(true, degree, points)` (do not repeat the first point). **End tangents:** `Curve.CreateInterpolatedCurve(pts, degree, Chord, t0, t1)` needs both ends; a missing one is taken from the free interpolation. Tangents are unit-normalized (Chord parametrization keeps unit speed).
- **line_blend:** `LineCurve` + `Curve.CreateBlendCurve(a, b, Tangency|Curvature, bulge, bulge)` + the curve through the points, appended into a `PolyCurve` so segment 0 stays an exact line.
- **Extrusion direction:** `Extrusion.Create(curve, h, cap)` follows the curve plane normal, so the result is checked against the requested direction by bbox centre and flipped when needed; non-parallel directions use `Surface.CreateExtrusion`.
- **Project/pull:** `Curve.ProjectToBrep(curve, breps, dir, tol)` and `Curve.PullToBrepFace` per face.
>>>>>>> d75fa02 (feat: designer curves (periodic/tangent curves, line_blend, fillet_corners, text as curves, engrave/emboss, extrusion direction, split keep, project/pull, fillet budget))
