# Development

## Requirements
- Rhino 7 for Mac (7.39 tested). Its bundled Mono 6.12 and Roslyn compiler are the whole toolchain.
- `python3` (system 3.9 is fine) for tests. `curl`.
- Optional: the `claude` CLI.

## Build
```sh
scripts/build.sh            # → dist/RhinoMCP.rhp  +  dist/RhinoMCP.Tools.<stamp>.dll
DEBUG_SYMBOLS=1 scripts/build.sh   # also emit portable .pdb files (loaded with the tools DLL)
```
`build.sh` runs Rhino's bundled `mono csc.exe`. It references `RhinoCommon.dll` and `Newtonsoft.Json.dll` (add `Rhino.UI.dll` / `Eto.dll` to `compile` in `build.sh` when a tool needs them) from the Rhino bundle. Nothing from McNeel is copied into `dist/`.

## Install (once)
1. In Rhino, run `_PlugInManager` → Install… → choose `dist/RhinoMCP.rhp`, or drag the file onto the Rhino window.
2. Restart Rhino. The plugin loads at startup and the server starts automatically. Check with `MCPStatus`.
3. Connect Claude Code. `MCPConnect` prints the exact command:
   ```sh
   claude mcp add --transport http rhino http://127.0.0.1:7340/mcp --header "Authorization: Bearer <token>"
   ```

## Dev loop
- **Tools change** → `scripts/build.sh` (it also copies the tools DLL to `~/Library/Application Support/RhinoMCP/tools/`, keeping the newest 3), then `scripts/mcp.sh call dev_reload '{}'`. The new tools DLL is loaded from bytes, so no restart is needed.
- **Core change** → `scripts/build.sh`, then restart Rhino.
- **Integration tests** → `scripts/dev-test.sh tests/test_<family>.py`. It takes a lock (`/tmp/rhinomcp-test.lock`), builds, reloads and runs the tests.
- **Calling a tool by hand:**
  ```sh
  scripts/mcp.sh tools/list
  scripts/mcp.sh call context_get '{}'
  ```
  `scripts/mcp.sh` reads the port from the discovery file and the token from `config.json`.

## Writing a tool
```csharp
namespace RhinoMCP.Tools.Measure {
  public sealed class MeasureTool : ITool {
    public string Name => "measure";
    public string Description => "Length/area/volume/centroid/bbox of objects or a set. Use group_by for totals per layer.";
    public string InputSchema => @"{""type"":""object"",""properties"":{""target"":{""$ref"":""#/$defs/target""}}, ""required"":[""target""]}";
    public ToolKind Kind => ToolKind.Read;
    public ToolResult Execute(ToolContext ctx, JObject args) {
      var a = new ArgReader(args);
      var objs = a.Target("target", ctx);       // resolves handle | guid | set | query
      if (a.HasErrors) return a.ErrorResult();
      …
      return ToolResult.Ok(new { … });
    }
  }
}
```
- Shared schema fragments (`target`, `point`, `plane`) live in `Core/Schemas.cs`. Just write `{"$ref":"#/$defs/target"}`; the registry injects the `$defs` block when listing tools.
- A tool that runs Rhino commands which manage their own undo records (like `run_command`) also implements the marker interface `IManagesOwnUndo`, so the registry does not open an outer undo record.
- Keep descriptions short. State what the tool does, when to use it, and how it fails.

## Troubleshooting
- **The plugin doesn't load.** Check Rhino's command history for `RhinoMCP` lines and run `MCPStatus`.
- **Port in use.** The server tries ports 7340–7359. The discovery file shows which one it picked.
- **401 errors.** The token changed. Run `MCPConnect` again.

## Shared live Rhino: rules for parallel work
- **Touch the live Rhino only inside `scripts/dev-test.sh`,** which holds the lock. That includes manual `scripts/mcp.sh` calls during development. Use `scripts/dev-test.sh --` style runs, or keep exploratory calls to read-only tools.
- `dev-test.sh` reloads **this worktree's** `dist/` DLL through `dev_reload {"dll": …}`, so builds from other worktrees can't interfere.
- **Dispose everything deterministically.** Never let a finalizer or a thread-pool or timer callback throw. Mono exits Rhino on any unhandled exception. Traces go to `~/Library/Application Support/RhinoMCP/crash.log`.
- Known `-_Layer` quirks: `_New` needs a trailing `_Enter`; the current layer is set with `_Current`, not `_Set`.
