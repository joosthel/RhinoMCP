#!/bin/bash
# Locked integration run: build, dev_reload, then python3 unittest on the given files.
#   scripts/dev-test.sh tests/test_core.py [more files...]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
[ $# -ge 1 ] || { echo "usage: dev-test.sh <test files...>" >&2; exit 2; }

LOCK=/tmp/rhinomcp-test.lock
WAITED=0
until mkdir "$LOCK" 2>/dev/null; do
  if [ "$WAITED" -ge 600 ]; then echo "timed out waiting for $LOCK (10 min). Remove it if stale." >&2; exit 1; fi
  [ $((WAITED % 30)) -eq 0 ] && echo "waiting for test lock ($LOCK)..." >&2
  sleep 5; WAITED=$((WAITED + 5))
done
trap 'rmdir "$LOCK" 2>/dev/null || true' EXIT INT TERM

scripts/build.sh
echo "==> dev_reload"
scripts/mcp.sh call dev_reload '{}'

echo "==> tests"
PYTHONPATH="$ROOT/tests${PYTHONPATH:+:$PYTHONPATH}" python3 -m unittest -v "$@"
