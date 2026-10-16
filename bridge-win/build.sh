#!/bin/bash
# Builds dist/rhinomcp-bridge.exe (net48 console app) with Rhino 7 for Mac's bundled Mono csc.
# The exe is a plain .NET Framework 4.8 assembly: it runs natively on Windows 10/11 and under Mono on macOS (tests).
# On Windows use bridge-win/RhinoMcpBridge.csproj (msbuild) instead; see .github/workflows/ci.yml.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
V="${RHINO_MONO:-/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Frameworks/Mono64Rhino.framework/Versions/6.12.0}"
[ -x "$V/Resources/bin/mono" ] || { echo "Rhino 7 bundled Mono not found at: $V" >&2; exit 1; }
mkdir -p dist
MONO_PATH="$V/Resources/lib/mono/4.5" MONO_CFG_DIR="$V/Resources/etc" "$V/Resources/bin/mono" \
  "$V/Resources/lib/mono/4.5/csc.exe" -nologo -target:exe -langversion:7.3 -optimize+ \
  -out:dist/rhinomcp-bridge.exe \
  -r:System.dll -r:System.Core.dll -r:System.Web.Extensions.dll \
  bridge-win/Program.cs bridge-win/AssemblyInfo.cs
echo "OK: dist/rhinomcp-bridge.exe"
