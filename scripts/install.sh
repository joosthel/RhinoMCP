#!/bin/bash
# One-command install of RhinoMCP for this Mac (from a source checkout). Idempotent.
# Installs: plugin -> MacPlugIns/RhinoMCP.rhp/, tools dll -> ~/Library/Application Support/RhinoMCP/tools,
#           bridge -> ~/Library/Application Support/RhinoMCP/bin/rhinomcp-bridge.
# Prints (never runs) client config lines. Never touches Claude/Cursor config files.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

APPSUP="${RHINOMCP_APPSUP:-$HOME/Library/Application Support}"  # override only for testing
PLUGDIR="$APPSUP/McNeel/Rhinoceros/MacPlugIns/RhinoMCP.rhp"
DATA="$APPSUP/RhinoMCP"
BIN="$DATA/bin"

# Build only when something is missing or sources are newer than the build.
need_core=0
if [ ! -f dist/RhinoMCP.rhp ] || ! ls dist/RhinoMCP.Tools.*.dll >/dev/null 2>&1; then need_core=1
elif [ -n "$(find src -name '*.cs' -newer dist/RhinoMCP.rhp 2>/dev/null | head -1)" ]; then need_core=1; fi
[ "$need_core" = 1 ] && scripts/build.sh
if [ ! -x dist/rhinomcp-bridge ] || [ -n "$(find bridge/Sources bridge/Package.swift -newer dist/rhinomcp-bridge 2>/dev/null | head -1)" ]; then
  bridge/build.sh
fi

TOOLS="$(ls -1 dist/RhinoMCP.Tools.*.dll | sort | tail -n 1)"

mkdir -p "$PLUGDIR" "$DATA/tools" "$BIN"
cp -f dist/RhinoMCP.rhp "$PLUGDIR/RhinoMCP.rhp"
cp -f "$TOOLS" "$DATA/tools/"
[ -f "${TOOLS%.dll}.pdb" ] && cp -f "${TOOLS%.dll}.pdb" "$DATA/tools/"
# keep the 3 newest tools dlls
ls -1 "$DATA"/tools/RhinoMCP.Tools.*.dll | sort -r | tail -n +4 | while IFS= read -r old; do rm -f "$old" "${old%.dll}.pdb"; done
cp -f dist/rhinomcp-bridge "$BIN/rhinomcp-bridge"
chmod +x "$BIN/rhinomcp-bridge"
xattr -d com.apple.quarantine "$BIN/rhinomcp-bridge" 2>/dev/null || true

if pgrep -x Rhinoceros >/dev/null 2>&1 || pgrep -f "Rhino 7.app" >/dev/null 2>&1; then
  RHINO_NOTE="Rhino is running: restart it once so the new plugin loads (tools hot-reload via the dev reload tool otherwise)."
else
  RHINO_NOTE="Start Rhino 7; the plugin loads at startup and starts the server."
fi

B="$BIN/rhinomcp-bridge"
PORT=7340
if [ -f "$DATA/config.json" ]; then
  PORT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1])).get("port",7340))' "$DATA/config.json" 2>/dev/null || echo 7340)"
fi

cat <<EOF

Installed:
  plugin  $PLUGDIR/RhinoMCP.rhp
  tools   $DATA/tools/$(basename "$TOOLS")
  bridge  $B

$RHINO_NOTE

=== Connect a client (copy what you need; nothing below has been run) ===

Claude Code (stdio, recommended):
  claude mcp add rhino -- "$B"

Claude Code (HTTP alternative, no bridge; token is read from your config at run time):
  claude mcp add --transport http rhino http://127.0.0.1:$PORT/mcp \\
    --header "Authorization: Bearer \$(python3 -c 'import json,os;print(json.load(open(os.path.expanduser("~/Library/Application Support/RhinoMCP/config.json")))["token"])')"

Claude Desktop:
  Build the extension with packaging/build_mcpb.sh and double-click dist/rhino-mcp.mcpb, or add to
  ~/Library/Application Support/Claude/claude_desktop_config.json under "mcpServers":
    "rhino": { "command": "$B" }

Cursor (~/.cursor/mcp.json):
  { "mcpServers": { "rhino": { "command": "$B" } } }

If macOS blocks the bridge ("cannot be opened"): xattr -d com.apple.quarantine "$B"
EOF
