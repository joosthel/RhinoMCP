# 02 — Controlling Rhino 7 + 8 from an external MCP server

> Source: Sonnet 5.5 research subagent, 2026-10-15, plus local verification. **[U]** = unverified.

## Local machine (verified)
- `/Applications/Rhino 7.app` = **7.38.24338.17002**, bundle `com.mcneel.rhinoceros.7`, registers `rhino://`.
- Executable is **x86_64 only** → runs under **Rosetta 2** on this arm64 Mac. Rhino 8 for Mac is universal.
- `RhinoCommon.dll` targets **.NET Framework 4.8** (Mono / Xamarin.Mac). Bundles `Newtonsoft.Json.dll`, `Nancy.dll`, `Nancy.Hosting.Self.dll`.
- Grasshopper (`b45a29b1-4343-4035-989e-044e8580d9cf`) and IronPython plug-in settings folders exist.
- `Contents/Resources/bin/` has only `yak` and `rhinoscriptcompiler` — **no `rhinocode`** in R7.
- No AppleScript dictionary → AppleScript can only launch/activate Rhino.
- No Rhino 8 installed.
- These APIs were confirmed present in the local `RhinoCommon.xml`: `HiddenLineDrawing`, `FilePdf`, `RhinoApp.InvokeOnUiThread`, `ViewTable.AddPageView`, `RhinoPageView.AddDetailView`, `DetailView.SetScale`, `RhinoDoc.BeginUndoRecord`, `ViewCapture.CaptureToBitmap`, `ViewCaptureSettings`, `ObjectTable.AddClippingPlane`.

## Programming surfaces

| Surface | Rhino 7 | Rhino 8 |
|---|---|---|
| RhinoCommon | net48 (Win) / Mono (Mac) | .NET 7 → .NET 8 default from 8.20; Windows can fall back to net48 |
| C# plugin `.rhp` | net48 | multi-target `net48;net8.0` |
| Python | IronPython 2.7 | CPython 3.9 (`#! python3`, `# r: pkg` pip) + IronPython 2.7 (`#! python 2`) |
| Rhino.Inside | Windows only | Windows only; **no Mac** ([thread](https://discourse.mcneel.com/t/rhino-inside-on-macos/173311)) |
| rhino3dm | standalone file I/O, no doc/view/Make2D | same |
| Compute / Hops | Windows server | Windows server |
| `rhinocode` CLI | — | 8.11+ (`StartScriptServer`, `rhinocode command/script/list`) |
| Macros | `RhinoApp.RunScript` | same |

## External control channels (ranked)
1. **In-process localhost server in a plugin** (7+8, Mac+Win). JSON-RPC over TCP/HTTP. Marshal to UI thread via `RhinoApp.InvokeOnUiThread` with timeout; wrap edits in `BeginUndoRecord`/`EndUndoRecord`; `doc.Views.Redraw()`. **Recommended.**
2. **`rhinocode` CLI** — R8.11+ only; runs commands/script files, no structured return values **[U]**. Fallback only.
3. Startup scripts / `rhino://` / `open -a` — bootstrapping only.
4. AppleScript — not viable.
5. Rhino.Inside — Windows, separate process; not for live sessions.
6. Compute — stateless, Windows; Grasshopper solves only.

## One codebase for 7 and 8
- **A. C# plugin multi-targeting `net48;net8.0`** (recommended). Compile against RhinoCommon 7.x NuGet for net48, 8.x for net8.0, `#if RHINO8` for differences. Use Newtonsoft.Json (bundled). One `.yak` with rhino7/rhino8 + mac/win tags **[U on tag format]**.
- **B. IronPython 2.7 script listener** — no build step, but fragile threading and lifetime **[U]**. Good for prototyping.
- **C. Hybrid** — thin C# plugin owns socket + UI-thread dispatch + undo; typed C# handlers + `run_python` escape hatch.

Mac notes: R7 Mac runs under Rosetta here, so native deps need x86_64; R8 needs universal. No `rhinocode`, Rhino.Inside or Compute on R7 Mac.

## Grasshopper
- `GH_Document`, `GH_DocumentIO.Open/Save`, `Instances.ActiveCanvas.Document`, `Instances.DocumentServer`.
- Create components: `Instances.ComponentServer.EmitObject(guid)` → `doc.AddObject` → `param.AddSource` → `doc.NewSolution(true)`.
- Sliders: `GH_NumberSlider.SetSliderValue`. Bake: `IGH_BakeAwareObject.BakeGeometry`.
- Load GH: `-_Grasshopper` or `PlugIn.LoadPlugIn(b45a29b1-…)`.
- Headless GH = Windows only (Rhino.Inside/Compute); on Mac use the live session.

## Drawings & export
- **Make2D:** `-_Make2D` macro or `Rhino.Geometry.HiddenLineDrawing` API (verified present in R7). Only Make2D gives vector hidden-line output.
- **Clipping planes:** `ObjectTable.AddClippingPlane`. R8 section tools/styles **[U on API]**.
- **Layouts:** `ViewTable.AddPageView`, `RhinoPageView.AddDetailView`, `DetailView.SetScale`.
- **PDF:** `Rhino.FileIO.FilePdf` + `ViewCaptureSettings`; R8 PDF output differs from R7 (known linetype/weight issues, [thread](https://discourse.mcneel.com/t/exporting-to-pdf-display-mode-artifacts-rhino-8/170431)).
- **DWG/DXF/SVG/IFC:** scripted `-_Export "file.ext"` + option prompts. IFC native in R8 **[U]**.
- **Images:** `ViewCapture.CaptureToBitmap`, `RhinoView.CaptureToBitmap`.

## Recommended architecture
1. `RhinoMCP.rhp` C# plugin, `net48;net8.0`, shared source.
2. Inside: localhost JSON-RPC server, UI-thread dispatcher with timeout, undo record per request, `run_python` + typed handlers.
3. External MCP server (Python or Node) connects as client; version handshake.
4. Optional R8 fallback via `rhinocode`.
5. Don't build on Rhino.Inside, Compute or AppleScript for live Mac sessions.

## Links
- https://developer.rhino3d.com/guides/rhinocommon/moving-to-dotnet-core/
- https://developer.rhino3d.com/guides/scripting/advanced-cli
- https://discourse.mcneel.com/t/rhino-inside-for-rhino-8-and-wip/205360
- https://discourse.mcneel.com/t/make2d-in-rhinocommon/45113
- https://discourse.mcneel.com/t/rhino-print-to-pdf-vector-output/207306
