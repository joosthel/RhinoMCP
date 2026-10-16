#!/bin/bash
# Builds the Claude Desktop extension (server.type = binary).
#   packaging/build_mcpb.sh                -> dist/rhino-mcp.mcpb; bundles every bridge present in dist/:
#                                             rhinomcp-bridge (macOS, built on demand) and rhinomcp-bridge.exe (Windows, if built)
#   packaging/build_mcpb.sh --platform win -> dist/rhino-mcp-win.mcpb with only rhinomcp-bridge.exe
#   packaging/build_mcpb.sh --platform mac -> dist/rhino-mcp.mcpb with only the macOS bridge
# The manifest uses mcp_config.platform_overrides.win32 to select the .exe on Windows.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
PLATFORM=all
if [ "${1:-}" = "--platform" ]; then PLATFORM="${2:?mac|win|all}"; fi

HAVE_MAC=0; HAVE_WIN=0
if [ "$PLATFORM" != "win" ]; then
  [ -x dist/rhinomcp-bridge ] || bridge/build.sh
  HAVE_MAC=1
fi
if [ "$PLATFORM" != "mac" ]; then
  if [ -f dist/rhinomcp-bridge.exe ]; then HAVE_WIN=1
  elif [ "$PLATFORM" = "win" ]; then echo "dist/rhinomcp-bridge.exe missing (build with bridge-win/build.sh or msbuild)" >&2; exit 1
  else echo "note: dist/rhinomcp-bridge.exe not found; building a macOS-only extension" >&2; fi
fi

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/rhinomcp-mcpb.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/server"
python3 - "$ROOT/packaging/mcpb/manifest.json" "$STAGE/manifest.json" "$HAVE_MAC" "$HAVE_WIN" <<'PY'
import json, sys
src, dst, mac, win = sys.argv[1], sys.argv[2], sys.argv[3] == "1", sys.argv[4] == "1"
m = json.load(open(src))
cfg = m["server"]["mcp_config"]
if mac and not win:
    cfg.pop("platform_overrides", None)
    m["compatibility"]["platforms"] = ["darwin"]
elif win and not mac:
    cfg.pop("platform_overrides", None)
    m["server"]["entry_point"] = "server/rhinomcp-bridge.exe"
    cfg["command"] = "${__dirname}/server/rhinomcp-bridge.exe"
    m["compatibility"]["platforms"] = ["win32"]
json.dump(m, open(dst, "w"), indent=2)
PY
if [ "$HAVE_MAC" = 1 ]; then cp dist/rhinomcp-bridge "$STAGE/server/rhinomcp-bridge"; chmod +x "$STAGE/server/rhinomcp-bridge"; fi
if [ "$HAVE_WIN" = 1 ]; then cp dist/rhinomcp-bridge.exe "$STAGE/server/rhinomcp-bridge.exe"; fi
NAME=rhino-mcp.mcpb
if [ "$HAVE_WIN" = 1 ] && [ "$HAVE_MAC" = 0 ]; then NAME=rhino-mcp-win.mcpb; fi
OUT="$ROOT/dist/$NAME"
rm -f "$OUT"
if npx --yes @anthropic-ai/mcpb validate "$STAGE/manifest.json" >&2 && \
   npx --yes @anthropic-ai/mcpb pack "$STAGE" "$OUT" >&2; then
  echo "(packed with @anthropic-ai/mcpb)"
else
  echo "mcpb CLI unavailable or failed; falling back to plain zip" >&2
  rm -f "$OUT"
  (cd "$STAGE" && zip -qr -X "$OUT" manifest.json server)
fi
echo "OK: dist/$NAME"
