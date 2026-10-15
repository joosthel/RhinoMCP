#!/bin/bash
# Builds a universal (arm64 + x86_64) rhinomcp-bridge into dist/rhinomcp-bridge.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/bridge"
swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/rhinomcp-bridge"
mkdir -p "$ROOT/dist"
cp "$BIN" "$ROOT/dist/rhinomcp-bridge"
chmod +x "$ROOT/dist/rhinomcp-bridge"
# Unsigned distribution: ad-hoc sign so arm64 will execute locally; this is NOT Developer ID signing.
codesign --force --sign - "$ROOT/dist/rhinomcp-bridge" >/dev/null 2>&1 || true
echo "OK: dist/rhinomcp-bridge ($(lipo -archs "$ROOT/dist/rhinomcp-bridge"))"
