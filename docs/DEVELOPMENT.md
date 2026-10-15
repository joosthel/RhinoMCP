# Development

## Requirements
- Rhino 7 for Mac (7.39 tested). Its bundled Mono 6.12 and Roslyn compiler are the whole toolchain.
- `python3` (system 3.9 is fine) for tests. `curl`.
- Optional: the `claude` CLI.

## Build
```sh
scripts/build.sh            # → dist/RhinoMCP.rhp  +  dist/RhinoMCP.Tools.<stamp>.dll
```
`build.sh` runs Rhino's bundled `mono csc.exe`. It references `RhinoCommon.dll`, `Rhino.UI.dll`, `Eto.dll` and `Newtonsoft.Json.dll` from the Rhino bundle. Nothing from McNeel is copied into `dist/`.

## Install (once)
1. In Rhino, run `_PlugInManager` → Install… → choose `dist/RhinoMCP.rhp`, or drag the file onto the Rhino window.
2. Restart Rhino. The plugin loads at startup and the server starts automatically. Check with `MCPStatus`.
3. Connect Claude Code. `MCPConnect` prints the exact command:
   ```sh
   claude mcp add --transport http rhino http://127.0.0.1:7340/mcp --header "Authorization: Bearer <token>"
   ```

## Dev loop
- **Tools change** → `scripts/build.sh`, then `scripts/mcp.sh dev_reload`. The new tools DLL is loaded from bytes, so no restart is needed.
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
- Shared schema fragments (`target`, `point`, `plane`) live in `Core/Schemas.cs` and are referenced by name.
- Keep descriptions short. State what the tool does, when to use it, and how it fails.

## Troubleshooting
- **The plugin doesn't load.** Check Rhino's command history for `RhinoMCP` lines and run `MCPStatus`.
- **Port in use.** The server tries ports 7340–7359. The discovery file shows which one it picked.
- **401 errors.** The token changed. Run `MCPConnect` again.
