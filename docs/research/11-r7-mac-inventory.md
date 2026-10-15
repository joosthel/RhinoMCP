# 11 — Rhino 7.39 Mac: in-process capability inventory

> Source: a Sonnet 5.5 subagent did a read-only inspection of `/Applications/Rhino 7.app` (7.39.25314.15002) on 2026-10-15. Methods: assembly metadata, the bundled XML docs, and reflection over public types only. Nothing was decompiled or modified. **[U]** = unverified at runtime.
>
> Paths: `RES` = `Contents/Frameworks/RhCore.framework/Versions/A/Resources`; `MONO` = `…/Frameworks/Mono64Rhino.framework/Versions/6.12.0`.

## Bottom line
Rhino 7 Mac ships far more than its UI exposes:
- Mono 6.12 with a working `mcs` compiler.
- **Roslyn 3.9 with C# scripting**.
- The Mono C# evaluator.
- IronPython 2.7.12 through `Rhino.Runtime.PythonScript`.
- Grasshopper 7.39 with **NodeInCode**, which calls GH components as functions, plus Kangaroo2 and PanelingTools.
- About 21k public RhinoCommon members.

The only thing truly missing is Python 3. **We can build the plugin with Rhino's own bundled compiler, so no .NET SDK install is needed.**

## Runtimes and compilers
| Item | Location | Version |
|---|---|---|
| Mono | `MONO/` (x86_64, profiles 4.5–4.8) | 6.12.0 |
| `mcs` C# compiler | `MONO/Resources/lib/mono/4.5/mcs.exe` | runs standalone ✔ |
| Mono.CSharp evaluator | `MONO/Resources/lib/mono/4.5/Mono.CSharp.dll` | 4.0.0.0 |
| Roslyn + C# Scripting | `MONO/Resources/lib/mono/4.5/Microsoft.CodeAnalysis*.dll`, `csc.exe`, `csi.exe` | 3.9.0.0 |
| Roslyn (GH copy) | `RES/ManagedPlugIns/GrasshopperPlugin.rhp/Components/` | 3.7.0.0 (used by the GH C# Script component) |
| IronPython + DLR | `RES/ManagedPlugIns/RhinoDLR_Python.rhp/` | 2.7.12, Py 2.7 stdlib |
| Python 3 | — | **absent** |
| Rhino C# ScriptEditor / `rhinocode` | — | **absent** |

**Verified standalone build route.** No Rhino launch and no licensing involved:
```sh
V="/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Frameworks/Mono64Rhino.framework/Versions/6.12.0"
export MONO_PATH="$V/Resources/lib/mono/4.5" MONO_CFG_DIR="$V/Resources/etc"
"$V/Resources/bin/mono" "$V/Resources/lib/mono/4.5/mcs.exe" -out:r.exe r.cs
```
No system `mono` or `dotnet` is installed.

## Surprises
- **Nancy, Nancy.Hosting.Self and DocoptNet** are used only by `yak.exe`. There is no hidden Rhino server.
- **No Rhino.Compute, Hops or StartScriptServer.** `HostUtils.RegisterComputeEndpoint` exists as a hook only.
- **An HTTP listener does exist**, in `RhinoDLR_Python.rhp`: `RhinoHttpListener` on `System.Net.HttpListener`, behind `StartAtomEditorListener` / `StopAtomEditorListener` / `SetAtomEditorListenerPort`. It looks like an Atom-editor bridge for running Python files. Protocol **[U]**.
  - It also shows that `HttpListener` already runs inside Rhino 7 Mac's Mono.
- **NodeInCode** (`Rhino.NodeInCode`, `Components.FindComponent`, `ComponentFunctionInfo.Invoke`) lets C# call any GH component as a function. GH must be loaded first; Mac behaviour **[U]**.
- `Rhino.Runtime.InProcess.RhinoCore` (the Rhino.Inside type) is present in the API. It is unusable on Mac **[U]**.
- `websocket-sharp.dll` is bundled; its consumer is unknown.

## Grasshopper 7.39
- `Grasshopper.dll`, `GH_IO.dll` (+XML), `GH_Util.dll`.
- Components:
  - Curve, Surface, Mesh/Triangulation, Field, Math, Vector, Xform, IO
  - **Galapagos**: evolutionary solver
  - **Kangaroo2 2.5.3**: physics and form-finding
  - **PanelingTools**
  - GhPython (IronPython 2.7)
  - C# Script (Roslyn)
- No Hops.

## RhinoCommon surface
Reflection finds **1158 exported types and ~20,971 public members**. The XML documents 1052 types.

| Namespace | Types |
|---|---|
| Rhino.Geometry | 207 |
| Rhino.Render | 155 |
| Rhino.DocObjects | 115 |
| Rhino.FileIO | 65 |
| Rhino.UI | 61 |
| Rhino.Display | 59 |
| Rhino.Runtime | 43 |
| Rhino.DocObjects.Tables | 34 |

Confirmed present:
- **SubD:** create from mesh, surface, loft or sweep, offset, join, merge coplanar, `ToBrep`. No SubD booleans.
- **Breps:** booleans (union/difference/intersection/split), `CreateFilletEdges`, `CreateOffsetBrep`, `CreateShell`, pipe, loft, sweep. **No `CreateChamferEdges`**; use the `_ChamferEdge` command instead.
- **Meshes:** booleans, `QuadRemesh` (+ async, + from brep). **ShrinkWrap is absent** (R8 only).
- **Rendering:** materials, environments, textures, PBR, render pipeline, Cycles.
- **Tables:** Layer, Linetype, DimStyle, InstanceDefinition, NamedView, NamedLayerState, HatchPattern, NamedCPlane, NamedPosition, Snapshot, Group, Material.
- **Output:** `FilePdf` (layers as PDF OCGs), `ViewCapture`, `HiddenLineDrawing`.
- **`PythonScript`:** `ExecuteScript`, `ExecuteFile`, `EvaluateExpression`, `Compile`, `Get/SetVariable`, `Output`, `AddRuntimeAssembly`.
- **`RhinoApp`:** `RunScript` (4 overloads), `ExecuteCommand`, `InvokeOnUiThread`.

## Consequences for the plan
1. **Toolchain:** compile the plugin with Rhino's bundled `mcs`, referencing its RhinoCommon.dll. No SDK needed. Roslyn `csc.exe` (C# 7.3+) is the upgrade path.
2. **`run_csharp` is real:** use Roslyn 3.9 `CSharpScript`, or the Mono.CSharp evaluator as a fallback.
3. **Server transport:** `HttpListener` is proven in-process by Rhino's own Atom listener.
4. **Grasshopper as a function library:** NodeInCode exposes the GH component library to tools. Galapagos and Kangaroo2 enable optimisation and form-finding.
5. **The only real gap is Python 3**, closed by the optional sidecar ([09](09-r7-mac-scripting.md)).
