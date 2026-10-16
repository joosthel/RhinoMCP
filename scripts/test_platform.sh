#!/bin/bash
# Pure-logic tests for src/Core/Platform.cs (no running Rhino needed): compiles tests/platform_logic/PlatformTests.cs together with
# Platform.cs using Rhino's bundled Mono, then runs it. Covers the Windows branches via an injected platform.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
V="/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Frameworks/Mono64Rhino.framework/Versions/6.12.0"
RES="/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
export MONO_PATH="$V/Resources/lib/mono/4.5:$RES" MONO_CFG_DIR="$V/Resources/etc"
OUT="$(mktemp -d -t rhinomcp-platform.XXXXXX)"
trap 'rm -rf "$OUT"' EXIT
"$V/Resources/bin/mono" "$V/Resources/lib/mono/4.5/csc.exe" -nologo -langversion:7.3 -target:exe -out:"$OUT/PlatformTests.exe" \
  -r:"$RES/RhinoCommon.dll" -r:System.dll -r:System.Core.dll src/Core/Platform.cs tests/platform_logic/PlatformTests.cs
cp "$RES/RhinoCommon.dll" "$OUT/"
PYTHONPATH="$ROOT/tests" "$V/Resources/bin/mono" "$OUT/PlatformTests.exe"
PYTHONPATH="$ROOT/tests" python3 -m unittest -q tests.test_platform_logic
