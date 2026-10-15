# Install

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
