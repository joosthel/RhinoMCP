# RhinoMCP for Windows (Rhino 7)

An MCP server inside Rhino 7 plus a small stdio bridge (`rhinomcp-bridge.exe`) for clients that cannot speak HTTP.
Needs Windows 10/11 (.NET Framework 4.8 is built in) and Rhino 7. Nothing is signed (personal research project), so
Windows SmartScreen may warn about the bridge: choose "More info" > "Run anyway".

## Install (5 minutes)

1. Unzip this folder somewhere (for example your Downloads folder).
2. Open PowerShell in the folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   It copies the plugin, tools DLL and bridge under `%APPDATA%\RhinoMCP\` (`plugin\`, `tools\`, `bin\`) and removes
   the "downloaded from the internet" mark from them. It does not touch Claude/Cursor config files; it prints the
   lines to copy.
3. Load the plugin into Rhino 7 once, either way:
   - drag `%APPDATA%\RhinoMCP\plugin\RhinoMCP.rhp` from Explorer onto the Rhino window, or
   - in Rhino run `_PlugInManager`, click **Install...** and pick that file.
   Then restart Rhino once. From then on it loads at startup and starts the server.
   (Alternative: the `.yak` package from the release, `yak install rhino-mcp-<version>-rh7_0-win.yak`, or
   Rhino's `_PackageManager`.)
4. Connect a client with what `install.ps1` printed, for example:

   ```powershell
   claude mcp add rhino -- "$env:APPDATA\RhinoMCP\bin\rhinomcp-bridge.exe"
   ```

   Claude Desktop / Cursor: add `{ "mcpServers": { "rhino": { "command": "C:\\Users\\YOU\\AppData\\Roaming\\RhinoMCP\\bin\\rhinomcp-bridge.exe" } } }`
   (the install script prints this with your real path).

## Check it

With Rhino running (window not minimized): `powershell -ExecutionPolicy Bypass -File .\verify.ps1`, then send
back `verify-report.txt`.

## Troubleshooting

- **Plugin fails to load / "blocked".** Right-click the `.rhp` > Properties > Unblock, or re-run `install.ps1`.
- **Only a `rhino_status` tool is listed.** Rhino is not running or the plugin is not loaded. Check that
  `%APPDATA%\RhinoMCP\instances\<pid>.json` exists while Rhino runs.
- **Server will not start / port.** The server listens on `127.0.0.1:7340` (falls back through 7359; the bridge follows).
  Run `netstat -ano | findstr 7340` to see if something else holds it. If binding `127.0.0.1` is refused the plugin
  falls back to `localhost`.
- **Captures are blank.** Do not minimize Rhino.
- **Diagnostics.** The bridge writes to stderr only; set `RHINOMCP_DATA_DIR` to point it at another data folder.
