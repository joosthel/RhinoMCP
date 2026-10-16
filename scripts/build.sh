#!/bin/bash
# Builds dist/RhinoMCP.rhp (core) and dist/RhinoMCP.Tools.<stamp>.dll (tools) with Rhino's bundled Mono + Roslyn csc.
# Set DEBUG_SYMBOLS=1 to also emit portable .pdb files (loaded alongside the tools dll by the plugin).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

V="/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Frameworks/Mono64Rhino.framework/Versions/6.12.0"
RES="/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
[ -x "$V/Resources/bin/mono" ] || { echo "Rhino 7 bundled Mono not found at: $V" >&2; exit 1; }
export MONO_PATH="$V/Resources/lib/mono/4.5" MONO_CFG_DIR="$V/Resources/etc"
MONO="$V/Resources/bin/mono"
CSC="$V/Resources/lib/mono/4.5/csc.exe"

DBG=()
[ "${DEBUG_SYMBOLS:-0}" = "1" ] && DBG=(-debug:portable)

mkdir -p dist
STAMP="$(date +%Y%m%d%H%M%S)"
LOG="$(mktemp -t rhinomcp-build.XXXXXX)"
WARNINGS=0

# compile <out> [extra csc args...]; source files are read from stdin, one per line.
compile() {
  local out="$1"; shift
  local files=()
  local f
  while IFS= read -r f; do files+=("$f"); done
  [ "${#files[@]}" -gt 0 ] || { echo "no source files for $out" >&2; exit 1; }
  echo "==> $out (${#files[@]} files)"
  if ! "$MONO" "$CSC" -nologo -langversion:7.3 -target:library -optimize+ ${DBG[@]+"${DBG[@]}"} \
      -out:"$out" \
      -r:"$RES/RhinoCommon.dll" -r:"$RES/Newtonsoft.Json.dll" \
      -r:System.dll -r:System.Core.dll -r:System.Net.dll -r:System.Net.Http.dll \
      "$@" "${files[@]}" >"$LOG" 2>&1; then
    cat "$LOG" >&2
    echo "BUILD FAILED ($out)" >&2
    exit 1
  fi
  local w
  w=$(grep -c "warning CS" "$LOG" || true)
  WARNINGS=$((WARNINGS + w))
  if [ "$w" -gt 0 ]; then grep "warning CS" "$LOG" | sort -u | head -20; fi
  return 0
}

rm -f dist/RhinoMCP.rhp dist/RhinoMCP.pdb dist/RhinoMCP.Tools.*.dll dist/RhinoMCP.Tools.*.pdb

find src/Core -name '*.cs' -not -path '*/obj/*' | sort | compile dist/RhinoMCP.rhp
TOOLS="dist/RhinoMCP.Tools.$STAMP.dll"
find src/Tools -name '*.cs' -not -path '*/obj/*' | sort | compile "$TOOLS" -r:dist/RhinoMCP.rhp

# Deploy the tools dll to the hot-reload folder; keep only the newest 3.
DEST="$HOME/Library/Application Support/RhinoMCP/tools"
mkdir -p "$DEST"
cp "$TOOLS" "$DEST/"
if [ -f "${TOOLS%.dll}.pdb" ]; then cp "${TOOLS%.dll}.pdb" "$DEST/"; fi
ls -1 "$DEST"/RhinoMCP.Tools.*.dll | sort -r | tail -n +4 | while IFS= read -r old; do
  rm -f "$old" "${old%.dll}.pdb"
done

rm -f "$LOG"
echo "OK: dist/RhinoMCP.rhp, $TOOLS ($WARNINGS warnings)"
