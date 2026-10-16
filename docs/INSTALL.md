# Install

macOS instructions first; [Windows](#windows-rhino-7) is further down.

RhinoMCP is a Rhino 7 (macOS) plugin that runs an authenticated MCP server on `127.0.0.1`, plus a small native stdio bridge (`rhinomcp-bridge`) for clients that cannot speak HTTP. No Node, Python or JSON editing is needed at runtime. Nothing is signed or notarized (personal research project); see Gatekeeper below.

## 1. Install the plugin and bridge

From a source checkout (needs Xcode Command Line Tools for the bridge, and Rhino 7 for its bundled compiler):

```sh
scripts/install.sh
```

It builds if needed, then copies:

| What | Where |
|---|---|
| plugin | `~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/RhinoMCP.rhp/RhinoMCP.rhp` |
| tools DLL | `~/Library/Application Support/RhinoMCP/tools/` |
| bridge | `~/Library/Application Support/RhinoMCP/bin/rhinomcp-bridge` |

It is safe to re-run, and it never edits your Claude or Cursor configs. It prints the client lines below. **Restart Rhino once** after the first install; from then on the plugin loads at startup and the server starts by itself.

Yak package (alternative): `packaging/build_yak.sh` produces `dist/rhino-mcp-<version>-rh7_0-mac.yak`. Rhino 7 for Mac cannot drag-and-drop `.yak` files; install it with the `_PackageManager` command or `yak install`. Not published.

## 2. Connect a client

**Claude Code** (stdio, recommended):

```sh
claude mcp add rhino -- ~/"Library/Application Support/RhinoMCP/bin/rhinomcp-bridge"
```

HTTP alternative (no bridge; reads the token from your config):

```sh
claude mcp add --transport http rhino http://127.0.0.1:7340/mcp \
  --header "Authorization: Bearer $(python3 -c 'import json,os;print(json.load(open(os.path.expanduser("~/Library/Application Support/RhinoMCP/config.json")))["token"])')"
```

The port is the one in `config.json` (default 7340; the server falls back through 7359 if busy, in which case use the stdio bridge, which follows it automatically).

**Claude Desktop:** run `packaging/build_mcpb.sh`, double-click `dist/rhino-mcp.mcpb`, choose Install, and reopen Claude. Or add to `~/Library/Application Support/Claude/claude_desktop_config.json`:

```json
{ "mcpServers": { "rhino": { "command": "/Users/YOU/Library/Application Support/RhinoMCP/bin/rhinomcp-bridge" } } }
```

**Cursor** (`~/.cursor/mcp.json`):

```json
{ "mcpServers": { "rhino": { "command": "/Users/YOU/Library/Application Support/RhinoMCP/bin/rhinomcp-bridge" } } }
```

If Rhino is closed, the bridge still starts and exposes a single tool, `rhino_status`, explaining "Open Rhino 7 with the RhinoMCP plugin". Once Rhino is running, requests go through; the bridge re-discovers the instance (new port or pid) by itself.

## Troubleshooting

- **Gatekeeper: "rhinomcp-bridge cannot be opened" / killed on launch.** The binary is unsigned (ad-hoc only). Remove the quarantine flag: `xattr -d com.apple.quarantine ~/"Library/Application Support/RhinoMCP/bin/rhinomcp-bridge"`. For the `.mcpb`, run it on the installed copy (Claude Desktop extracts under `~/Library/Application Support/Claude/Claude Extensions/`) or on the downloaded `.mcpb` before opening it. `scripts/install.sh` already clears the flag on its copy.
- **Only `rhino_status` is listed.** Rhino is not running, or the plugin did not load. Start Rhino 7, check the command line for the RhinoMCP startup line, and check that `~/Library/Application Support/RhinoMCP/instances/<pid>.json` exists. Stale files from dead pids are ignored.
- **Rhino window must not be minimized for captures.** `view_capture` and other viewport captures return blank or fail when the Rhino window is minimized or hidden.
- **401/403 from HTTP.** The token is in `config.json` (mode 0600). The server rejects requests with an `Origin` header or any `Host` other than `127.0.0.1:<port>`.
- **Plugin did not load at startup.** Rhino honours `AtStartup` only after the first manual load: run `_PlugInManager` and load RhinoMCP once.
- **Logs.** The bridge writes diagnostics to stderr only; stdout carries nothing but JSON-RPC lines. Crashes inside Rhino are logged to `~/Library/Application Support/RhinoMCP/crash.log`.

## Tools DLL location

Core (0.2.0+) loads the newest `RhinoMCP.Tools.*.dll` from `~/Library/Application Support/RhinoMCP/tools/`, falling back to the plugin's own folder, so Yak installs that ship the DLL next to the `.rhp` work too.

## Windows (Rhino 7)

Windows support is new and has been verified in CI only for the bridge (see `docs/TESTING-WINDOWS.md` for what still needs a real Rhino 7 on Windows). Requirements: Windows 10/11 (the bridge is a .NET Framework 4.8 exe, which ships with Windows; no runtime install) and Rhino 7.

**From a release zip** (`RhinoMCP-<version>-windows.zip`): unzip, then in PowerShell

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

**From a source checkout:** build the plugin (`scripts\build_win.ps1`) and the bridge (`msbuild bridge-win\RhinoMcpBridge.csproj -p:Configuration=Release`, output `dist\rhinomcp-bridge.exe`), then run `scripts\windows\install.ps1`. Package builders: `packaging\build_zip_win.ps1`, `packaging\build_yak_win.ps1` (writes `dist\rhino-mcp-<version>-rh7_0-win.yak`).

The script copies (current user, no admin):

| What | Where |
|---|---|
| plugin | `%APPDATA%\RhinoMCP\plugin\RhinoMCP.rhp` |
| tools DLL | `%APPDATA%\RhinoMCP\tools\` |
| bridge | `%APPDATA%\RhinoMCP\bin\rhinomcp-bridge.exe` |

It also removes the "downloaded from the internet" flag (`Unblock-File`); Rhino will not load a blocked `.rhp`. It never edits Claude or Cursor configs and prints the lines to copy.

**Load the plugin once** (either way), then restart Rhino: drag `RhinoMCP.rhp` onto the Rhino window, or run `_PlugInManager` > Install... and pick the file. Yak alternative: `yak install rhino-mcp-<version>-rh7_0-win.yak` or `_PackageManager`.

**Connect a client:**

```powershell
claude mcp add rhino -- "$env:APPDATA\RhinoMCP\bin\rhinomcp-bridge.exe"
```

Claude Desktop: install `rhino-mcp.mcpb` (one extension for macOS and Windows; `platform_overrides.win32` selects the `.exe`), or add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{ "mcpServers": { "rhino": { "command": "C:\\Users\\YOU\\AppData\\Roaming\\RhinoMCP\\bin\\rhinomcp-bridge.exe" } } }
```

Cursor (`%USERPROFILE%\.cursor\mcp.json`): same JSON.

The server listens on `http://127.0.0.1:<port>/mcp` (port 7340, falling back through 7359) or, if that cannot be bound, `http://localhost:<port>/mcp`; the bridge tries both. Data lives in `%APPDATA%\RhinoMCP` (`config.json`, `instances\<pid>.json`); set `RHINOMCP_DATA_DIR` to relocate it (tests).

Windows troubleshooting: SmartScreen on the unsigned exe ("More info" > "Run anyway"); blocked `.rhp` (re-run `install.ps1` or Properties > Unblock); only `rhino_status` listed means Rhino is not running or the plugin is not loaded (check `%APPDATA%\RhinoMCP\instances`); `netstat -ano | findstr 7340` shows who holds the port; run `scripts\windows\verify.ps1` for a full self-check.
