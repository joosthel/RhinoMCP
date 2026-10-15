#!/bin/bash
# Builds dist/rhino-mcp.mcpb (Claude Desktop extension, server.type = binary, macOS).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
[ -x dist/rhinomcp-bridge ] || bridge/build.sh
STAGE="$(mktemp -d -t rhinomcp-mcpb)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/server"
cp packaging/mcpb/manifest.json "$STAGE/manifest.json"
cp dist/rhinomcp-bridge "$STAGE/server/rhinomcp-bridge"
chmod +x "$STAGE/server/rhinomcp-bridge"
OUT="$ROOT/dist/rhino-mcp.mcpb"
rm -f "$OUT"
if npx --yes @anthropic-ai/mcpb validate "$STAGE/manifest.json" >&2 && \
   npx --yes @anthropic-ai/mcpb pack "$STAGE" "$OUT" >&2; then
  echo "(packed with @anthropic-ai/mcpb)"
else
  echo "mcpb CLI unavailable or failed; falling back to plain zip" >&2
  rm -f "$OUT"
  (cd "$STAGE" && zip -qr -X "$OUT" manifest.json server)
fi
echo "OK: dist/rhino-mcp.mcpb"
