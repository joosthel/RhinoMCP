#!/bin/bash
# Collect RhinoMCP diagnostics into a zip you can attach to a GitHub issue.
# The token in config.json is removed. Nothing is sent anywhere.
set -u
DATA="${RHINOMCP_DATA_DIR:-$HOME/Library/Application Support/RhinoMCP}"
OUT="${1:-$PWD/rhinomcp-logs-$(date +%Y%m%d-%H%M%S).zip}"
TMP="$(mktemp -d)"; D="$TMP/rhinomcp-logs"; mkdir -p "$D"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

{
  echo "date: $(date -u +%FT%TZ)"
  echo "os: $(sw_vers -productName 2>/dev/null) $(sw_vers -productVersion 2>/dev/null) ($(uname -m))"
  echo "rosetta_capable: $(sysctl -n sysctl.proc_translated 2>/dev/null || echo n/a)"
  for app in "/Applications/Rhino 7.app"; do
    [ -d "$app" ] && echo "rhino: $(defaults read "$app/Contents/Info" CFBundleShortVersionString 2>/dev/null) $(lipo -archs "$app/Contents/MacOS/Rhinoceros" 2>/dev/null)"
  done
  echo "plugin: $(ls -la "$HOME/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/RhinoMCP.rhp/" 2>/dev/null | tail -n +2)"
  echo "tools: $(ls "$DATA/tools" 2>/dev/null | tr '\n' ' ')"
  echo "bridge: $(ls -la "$DATA/bin/rhinomcp-bridge" 2>/dev/null)"
} > "$D/environment.txt"

# config without the token
[ -f "$DATA/config.json" ] && python3 -c "import json,sys; d=json.load(open(sys.argv[1])); d['token']='<removed>' if 'token' in d else None; print(json.dumps(d,indent=2))" "$DATA/config.json" > "$D/config.json" 2>/dev/null
[ -f "$DATA/crash.log" ] && tail -c 200000 "$DATA/crash.log" > "$D/crash.log"
[ -d "$DATA/instances" ] && cp "$DATA/instances/"*.json "$D/" 2>/dev/null

# live server info, if Rhino is running
if [ -x "$ROOT/scripts/mcp.sh" ]; then
  curl -s -m 5 http://127.0.0.1:7340/health > "$D/health.json" 2>/dev/null
  "$ROOT/scripts/mcp.sh" call server_info '{}' > "$D/server_info.json" 2>/dev/null
  "$ROOT/scripts/mcp.sh" call system_status '{}' > "$D/system_status.json" 2>/dev/null
  "$ROOT/scripts/mcp.sh" call context_get '{}' > "$D/context.json" 2>/dev/null
fi
# macOS crash reports for Rhino (last 3)
ls -t "$HOME/Library/Logs/DiagnosticReports/"Rhinoceros* 2>/dev/null | head -3 | while read -r f; do cp "$f" "$D/"; done

(cd "$TMP" && zip -qr "$OUT" rhinomcp-logs) && rm -rf "$TMP"
echo "Wrote $OUT"
echo "Please check it before sharing, then attach it to an issue: https://github.com/joosthel/RhinoMCP/issues/new/choose"
