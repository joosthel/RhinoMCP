# 01 — Existing MCP servers for Rhino and other 3D apps

> Source: Sonnet 5.5 research subagent, 2026-10-15. Repo facts for McNeel/rhinomcp/cordyceps re-checked via GitHub API the same day. **[U]** = unverified.

## Rhino / Grasshopper

| Project | Stars | License | Rhino | Mac | GH | Architecture | Tools |
|---|---|---|---|---|---|---|---|
| [mcneel/RhinoAI](https://github.com/mcneel/RhinoAI) (official) | 363 | MIT | 8/9 **[U]**, default branch `rhino-9.x` | Apple Silicon only | GH1 | .NET plugin, `MCPStart` exposes HTTP port; Claude Desktop connector + Yak; docs for Claude Code, Codex, Gemini CLI, LM Studio | **[U]** — "quite barebones", Python-heavy, low-res viewport capture ([thread](https://discourse.mcneel.com/t/rhino-mcp-server/216568)) |
| [jingcheng-chen/rhinomcp](https://github.com/jingcheng-chen/rhinomcp) | 1.15k | MIT | **8 only** | yes (flaky, #27) | yes, ~27 `gh_*` tools | Python stdio server → TCP 127.0.0.1:1999 (no auth) → C# plugin; shared JSON Schema contracts | ~50 typed: create/modify/delete, booleans, loft/extrude/sweep, query, layers, attributes, `capture_viewport`, undo/redo, `run_command`, Python & C# exec, RhinoScript docs search |
| [brookstalley/cordyceps](https://github.com/brookstalley/cordyceps) | 105 | MIT | 8.21+ (.NET 8) | yes | primary focus | GH component hosts streamable HTTP MCP at `127.0.0.1:26929/mcp` in-process | 7 tools × 100+ actions (`action` param); guides as resources; undo disabled, 20 snapshots |
| [SerjoschDuering/rhino-mcp](https://github.com/SerjoschDuering/rhino-mcp) | 65 | — | 7+ | likely | via HTTP GH component | Blender-MCP fork; Rhino Python socket server :9876 | `execute_rhino_code`, `execute_code_in_gh`, inspection, screen capture |
| dongwoosuk/rhino-grasshopper-mcp, reer-ide/rhino_mcp, makais/aag25_mcp, SandMartin, Raven | — | — | some claim 7 | — | — | mostly Python socket bridges **[U]** | — |

McNeel staff (Apr 2026) were cautious on MCP longevity ([thread](https://discourse.mcneel.com/t/official-mcp-for-anthropic/218540)); by May 2026 an official server existed. A forum user noted the `rhinocode` CLI gives no feedback on script success — an argument for structured results.

**No existing project covers Rhino 7 + 8 + Mac + Grasshopper with typed tools.**

## Other apps

| App | Project | Architecture | Tool surface |
|---|---|---|---|
| Blender | [ahujasid/blender-mcp](https://github.com/ahujasid/blender-mcp) ~31k★ | addon TCP :9876 ↔ Python stdio server (uvx) | 9 tools: `execute_blender_code`, `look`, `get_scene_info`, assets… |
| Unity | [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp) ~15k★ | C# editor pkg ↔ WebSocket ↔ Python server (stdio + HTTP), multi-instance, Roslyn validation | ~50 entrypoints |
| Unreal | [chongdashu/unreal-mcp](https://github.com/chongdashu/unreal-mcp) | C++ plugin TCP :55557 ↔ FastMCP | actors, blueprints, viewport |
| Revit | [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) | TS stdio ↔ WebSocket ↔ C# plugin; net48 + net8 (Revit 2020–2026); ExternalEvent for UI thread | 26 tools + `send_code_to_revit` |
| Fusion 360 | official Autodesk MCP **[U]**; community faust-machines | stdio ↔ TCP ↔ Python add-in; CustomEvent + work queue | 80 (community) |
| SketchUp | [zinin/sketchup-mcp2](https://github.com/zinin/sketchup-mcp2), [darwin/supex](https://github.com/darwin/supex) | Ruby ext TCP ↔ Python; JSON-RPC | `eval_ruby` off in Warehouse build, on in GitHub build |
| FreeCAD | [neka-nat/freecad-mcp](https://github.com/neka-nat/freecad-mcp) | workbench XML-RPC :9875 ↔ stdio (avoids deps in FreeCAD Python) | doc/object creation, exec code |
| Cinema 4D | [kumoproductions/mcp-cinema4d](https://github.com/kumoproductions/mcp-cinema4d) | Node stdio ↔ TCP :18710 with **token auth** | **[U]** |
| Houdini | various **[U]** | TCP + `hdefereval` for GUI thread | — |
| Godot | tomyud1, youssof20 **[U]** | Node stdio ↔ WebSocket | 32–153; no undo |

## Patterns
- **~90% split architecture:** stdio server (Python/TS) ↔ localhost socket ↔ thin in-app plugin. Exceptions embed HTTP MCP in-process (Cordyceps, McNeel) to avoid Python/uv drift.
- **Main-thread queue everywhere** (Blender timers, Fusion CustomEvent, Revit ExternalEvent, Houdini hdefereval).
- **Tool surface spectrum:** minimal (Blender 9) → medium (Revit 26, Unity/rhinomcp ~50) → huge (Fusion/Godot 80–150+) → action-multiplexed (Cordyceps 7×100). Everyone keeps a code escape hatch.
- **State:** compact scene summary, targeted queries, screenshots, docs lookup.
- **Undo is the weakest area** — nobody groups one MCP call into one undo record.
- **Safety is thin:** unauthenticated sockets common. Better: Blender safe mode, C4D token auth, SketchUp eval-off build.
- **Complaints:** install friction, port clashes, single-instance, timeouts, Python drift, Mac/Intel gaps, token cost.

## Takeaways for us
1. Hybrid: strong read/query + viewport tools, a few generic write tools, guarded code escape hatch.
2. One undo record per call — a real differentiator.
3. Localhost + token; per-capability toggles; audit log.
4. Structured results and errors always.
5. Differentiate on **Rhino 7 support**, query depth, undo grouping, safety, drawing production.
