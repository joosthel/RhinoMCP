#!/bin/bash
# Builds dist/rhino-mcp-<version>-rh7_0-mac.yak (not installed, not published).
# Layout: RhinoMCP.rhp, RhinoMCP.Tools.<stamp>.dll, rhinomcp-bridge, manifest.yml.
# NOTE: Core currently loads the tools dll only from ~/Library/Application Support/RhinoMCP/tools;
# see docs/INSTALL.md ("Core change request"). Until Core also searches next to the .rhp, the
# yak ships the dll but a first-run copy step (scripts/install.sh) is still needed.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
YAK="${YAK:-/Applications/Rhino 7.app/Contents/Resources/bin/yak}"
[ -x "$YAK" ] || { echo "yak not found at: $YAK" >&2; exit 1; }

if [ ! -f dist/RhinoMCP.rhp ] || ! ls dist/RhinoMCP.Tools.*.dll >/dev/null 2>&1; then scripts/build.sh; fi
[ -x dist/rhinomcp-bridge ] || bridge/build.sh

VERSION="$(sed -n 's/.*AssemblyVersion("\([0-9]*\.[0-9]*\.[0-9]*\)\..*/\1/p' src/Core/Plugin.cs | head -1)"
[ -n "$VERSION" ] || { echo "cannot read AssemblyVersion" >&2; exit 1; }
TOOLS="$(ls -1 dist/RhinoMCP.Tools.*.dll | sort | tail -n 1)"

STAGE="$ROOT/dist/yak-stage"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp dist/RhinoMCP.rhp "$STAGE/"
cp "$TOOLS" "$STAGE/"
cp dist/rhinomcp-bridge "$STAGE/"
sed "s/^version: .*/version: $VERSION/" packaging/yak/manifest.yml > "$STAGE/manifest.yml"

cd "$STAGE"
"$YAK" build --platform mac
PKG="$(ls -1 ./*.yak | head -n 1)"
TARGET="$ROOT/dist/rhino-mcp-$VERSION-rh7_0-mac.yak"
rm -f "$ROOT"/dist/rhino-mcp-*.yak
# yak may infer a different tag (e.g. rh7_0-any/rh8_0-mac); force the Rhino 7 Mac tag.
mv "$PKG" "$TARGET"
cd "$ROOT"
rm -rf "$STAGE"
echo "OK: ${TARGET#$ROOT/}"
