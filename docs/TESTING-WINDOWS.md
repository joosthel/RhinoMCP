# Testing RhinoMCP on Windows (for the tester)

Thank you for testing. You need a Windows 10/11 PC with **Rhino 7** (any recent 7.x service release). About 20 minutes. Everything is per-user; no admin rights are needed (please test as a normal, non-admin user if you can, that is the case we cannot check in CI).

## What you get

`RhinoMCP-<version>-windows.zip` containing `RhinoMCP.rhp` (plugin), `RhinoMCP.Tools.<stamp>.dll`, `rhinomcp-bridge.exe`, `install.ps1`, `verify.ps1`, `tests\`, `INSTALL-windows.md`.

## Steps

1. Unzip anywhere (for example `Downloads`). If Windows asks, the files may show as "blocked"; `install.ps1` clears that.
2. Open PowerShell in the unzipped folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   Note anything unexpected. It prints three things you need: where files went, how to load the plugin, and client config lines.
3. Start Rhino 7 and load the plugin. Try **both** methods if you have time (use a fresh state in between: `_PlugInManager`, unload/remove RhinoMCP):
   - drag `%APPDATA%\RhinoMCP\plugin\RhinoMCP.rhp` onto the Rhino window;
   - `_PlugInManager` > Install... > select the same file.
   Restart Rhino once. Watch the Rhino command line for a RhinoMCP startup line (copy it).
4. Check `%APPDATA%\RhinoMCP\instances\<pid>.json` exists and `config.json` has a `token` (do not send the token). Open `http://127.0.0.1:7340/health` in a browser (use the port from the instance file): it should show `{"ok":true,...}`. Also try `http://localhost:7340/health`.
5. With Rhino running, **window not minimized**, and a new empty document, run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\verify.ps1
   ```
   It runs a smoke test (initialize, tool count, context, a box with volume 500 plus undo, a PNG view capture, a Make2D drawing), exercises the bridge exe against your Rhino, and, if Python 3 is installed, runs the full test suites in `tests\` (takes several minutes; Rhino must stay in front and idle; add `-SkipPython` to skip). Do not touch Rhino while it runs.
6. Optional but valuable: connect a real client, for example
   ```powershell
   claude mcp add rhino -- "$env:APPDATA\RhinoMCP\bin\rhinomcp-bridge.exe"
   ```
   then ask Claude "what is in my Rhino document?" and "make a 10 x 10 x 5 box and tell me its volume". Also try closing Rhino: the client should keep working and report that Rhino is not reachable (tool `rhino_status`); reopen Rhino and ask again without restarting the client.
7. Optional: install `rhino-mcp.mcpb` (double-click) in Claude Desktop and check the Rhino tools appear.

## What to send back

- `verify-report.txt` (created in the folder you ran `verify.ps1` from).
- The Rhino command-line startup lines from step 3 (or a screenshot), and which load method (drag-drop / `_PlugInManager`) worked.
- Your Windows version (`winver`), Rhino version (`_Version`), whether you are admin, antivirus / SmartScreen / firewall prompts you saw.
- Anything that failed, with the exact message. If the server did not start: contents of `%APPDATA%\RhinoMCP\crash.log` if present, and the output of `netstat -ano | findstr 7340`.
- Whether `http://127.0.0.1:<port>/health` and `http://localhost:<port>/health` both worked in the browser.

## Known limits

- Unsigned binaries: SmartScreen may warn about `rhinomcp-bridge.exe`.
- Rhino 8 is not supported yet. Rhino for Windows must be version 7.
- Screen captures need Rhino un-minimized.
