#!/bin/bash
# Talk to the running RhinoMCP server (thin wrapper around scripts/mcp.py, which is platform aware).
#   mcp.sh tools/list
#   mcp.sh call <tool> '<json args>'
#   mcp.sh raw '<jsonrpc request>'
# Port comes from the newest live instances/*.json in the RhinoMCP data dir, token from config.json there.
set -euo pipefail
exec python3 "$(cd "$(dirname "$0")" && pwd)/mcp.py" "$@"
