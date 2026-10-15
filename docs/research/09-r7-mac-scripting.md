# 09 — Modern scripting & compute on Rhino 7 Mac

> Source: Sonnet 5.5 research subagent, 2026-10-15. Web sourcing was shallow and nothing was tested on a live Rhino. **[U]** = unverified.

## Bottom line
We can't make Rhino.Inside or Compute exist on a Mac. We can still get most of their value with our own code:
- IronPython 2.7 in-process gives the full rhinoscriptsyntax.
- An **opt-in native CPython 3 "compute pack" sidecar** adds numpy, shapely, trimesh, networkx and COMPAS.
- A C# snippet runner is possible.
- Running Grasshopper in the background is feasible but experimental.

## Ranked plan
| # | Capability | Verdict |
|---|---|---|
| 1 | IronPython 2.7 via `Rhino.Runtime.PythonScript.Create()` | **Build now** |
| 2 | CPython 3 sidecar (out of process, arm64-native) | **Build next**: largest gain, isolated from Rhino crashes |
| 3 | C# snippet runner (`CSharpCodeProvider`/mcs → Roslyn spike) | Build, expect friction |
| 4 | Grasshopper solve without the canvas (`GH_DocumentIO.Open` + `NewSolution(…Silent)`) | Spike, experimental |
| 5 | Second Rhino 7 instance for batch jobs | Don't: license manager blocks it **[U]** |
| 6 | pythonnet inside Rhino's Mono | Don't: high crash risk, no precedent |

## 1. C# snippets
- **`Mono.CSharp.Evaluator`**: the cheapest REPL, if Rhino's embedded Mono exposes it. It supports a subset of C# and isn't thread-safe **[U]**.
- **`CSharpCodeProvider` (mcs)** has a Rhino Mac precedent. mcs once broke on the space in the "Rhino 7.app" path ([thread](https://discourse.mcneel.com/t/rename-to-rhino-7-app-breaks-mono-c-compiler/115161)). Language support is about C# 6/7.
- **Roslyn (net48)**: no Mac precedent. It risks clashes with other plugins over `System.Collections.Immutable` and `System.Memory`, it's ~15 MB, and it starts slowly under Rosetta. Load it from a private folder via `AssemblyResolve`.
- Compiled assemblies can't be unloaded on net48. Load them from bytes and accept the memory growth.

## 2. IronPython from C#
- Call `PythonScript.Create()`, which returns null if the Python plugin is unavailable. Then `ExecuteScript(code)` and `GetVariable("result")`. Return results as `json.dumps` strings.
- **Fallback:** if `ExecuteScript` misbehaves, write a temp file and call `ExecuteFile` ([thread](https://discourse.mcneel.com/t/executing-python-3-script-in-c/190803)).
- **Stdout:** set `PythonScript.Output` or redirect `sys.stdout` inside the script **[U]**.
- Main thread only. The Python plugin loads lazily on Mac.

## 3. CPython 3 sidecar ("compute pack")
- **Transport:** stdio JSON-lines or loopback HTTP with a token.
- **Geometry exchange:**
  - `rhino3dm` 8.17: universal2 wheels for Python 3.8–3.13 on macOS 14/15 ([PyPI](https://pypi.org/project/rhino3dm)) **[U on exact files]**.
  - Alternatives: 3dm temp files, `CommonObject.ToJSON`, or mesh vertex/face arrays.
- **Runs arm64-native**, so it isn't limited by Rosetta. Launch it with `arch -arm64` from the x86_64 host **[U]**.
- **Libraries by value:**
  - shapely: 2D plan booleans, offsets
  - numpy and scipy
  - trimesh: mesh repair and booleans
  - networkx: adjacency and circulation
  - sympy
  - COMPAS: pure-Python core, runs in the sidecar without compas_rhino
  - ladybug: needs EnergyPlus or Radiance for simulation
  - cadquery/OCP: real B-rep and STEP, but heavy
- **Shipping:** opt-in. Download `uv` on first use, then `uv python install` and pinned wheels into `~/Library/Application Support/RhinoMCP/`. This avoids signing ~40 MB of native code. A bundled python-build-standalone is the offline alternative.
- **Security:** arbitrary code needs an explicit user setting.

## 4. Grasshopper without the canvas
- `GH_DocumentIO.Open` → find params by a naming convention (`IN_*` / `OUT_*`) → `SetSliderValue` / `PersistentData` → `NewSolution(true, GH_SolutionMode.Silent)` ([API](https://developer.rhino3d.com/api/grasshopper/html/M_Grasshopper_Kernel_GH_Document_NewSolution_1.htm)).
- Risks:
  - GH may need to be loaded first. Hiding its window is **[U]**.
  - Components that depend on `RhinoDoc.ActiveDoc` fail.
  - Mac GH plugins can crash.
- **Hops:** no Rhino 7 Mac build. Don't plan around it ([thread](https://discourse.mcneel.com/t/hops-not-working-on-mac/157477)).

## 5. Multiple instances
`open -n` with `-nosplash -runscript` exists ([docs](https://docs.mcneel.com/rhino/7mac/help/en-us/information/startingrhino.htm)), but the license manager reportedly blocks a second instance ([thread](https://discourse.mcneel.com/t/different-rhinos-running-as-separate-processes/109117)). Use the sidecar plus rhino3dm for background file-level work.

## 6. Legal boundary (not legal advice)
- The [Rhino 7 EULA](https://www.rhino3d.com/7/eula) **prohibits reverse engineering, decompiling and redistributing** Rhino.
- The SDK (RhinoCommon, Grasshopper API, openNURBS, rhino3dm) is MIT-style ([license](https://developer.rhino3d.com/license/)).
- **Our rule:**
  - Use documented and public APIs only, and reflection over public types.
  - No decompiling of Rhino or Grasshopper binaries.
  - Never bundle `Rhino*.dll` or `Grasshopper.dll`: reference them with `CopyLocal=false`.
  - Avoid undocumented APIs such as `Python3Script`.

## Test on the machine
1. Is `Mono.CSharp.dll` present? Does an Evaluator hello-world run?
2. `PythonScript.Output` capture; `ExecuteScript` vs `ExecuteFile`.
3. Does the rhino3dm universal2 wheel install in a uv venv?
4. Can the Rosetta plugin spawn an arm64 sidecar?
5. Silent GH solve with the window hidden.
