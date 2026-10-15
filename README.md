# RhinoMCP

A personal research project: a broad, non-opinionated [Model Context Protocol](https://modelcontextprotocol.io) server for **Rhino 7 and Rhino 8** (macOS and Windows).

Goal: let AI agents drive Rhino to help designers create shapes, develop architectural designs, and produce/export drawings from their models.

## Status

v0.2: **52 tools**, **214 live tests**, focused on **Rhino 7.39 for Mac**. Research findings and lessons: [`docs/FINDINGS.md`](docs/FINDINGS.md). Install: [`docs/INSTALL.md`](docs/INSTALL.md). See also [`docs/PLAN.md`](docs/PLAN.md), [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md), [`docs/DECISIONS.md`](docs/DECISIONS.md), synthesis in [`docs/research/00-synthesis.md`](docs/research/00-synthesis.md).

## Install

From a source checkout (builds, installs the plugin, tools and a stdio bridge, then prints the client config lines):

```sh
scripts/install.sh
```

Restart Rhino 7 once, then connect a client, e.g. `claude mcp add rhino -- ~/"Library/Application Support/RhinoMCP/bin/rhinomcp-bridge"`. Claude Desktop: run `packaging/build_mcpb.sh` and double-click `dist/rhino-mcp.mcpb`. A Yak package is built with `packaging/build_yak.sh`. No Node or Python is needed at runtime. The binaries are unsigned; see [`docs/INSTALL.md`](docs/INSTALL.md) for the Gatekeeper workaround and troubleshooting.

| Doc | Topic |
|---|---|
| [00](docs/research/00-synthesis.md) | Synthesis & recommended architecture |
| [01](docs/research/01-existing-mcp-servers.md) | Existing MCP servers (Rhino, Blender, Revit, Unity…) |
| [02](docs/research/02-rhino-control.md) | Controlling Rhino 7/8 programmatically |
| [03](docs/research/03-mcp-design.md) | MCP design for large tool surfaces |
| [04](docs/research/04-designer-workflows.md) | Designer/architect workflows & capability map |
| [05](docs/research/05-clean-make2d.md) | Clean Make2D pipeline |
| [06](docs/research/06-agent-skills.md) | Agent skills: standards & packaging |
| [07](docs/research/07-rhinoai-reference.md) | McNeel RhinoAI teardown |
| [08](docs/research/08-one-click-install.md) | One-click install & auto-start |
| [FINDINGS](docs/FINDINGS.md) | Research findings (Milestone 5) |
| [INSTALL](docs/INSTALL.md) | Install & connect, troubleshooting |
| [eval: freeform](docs/eval/freeform/REPORT.md) | Eval report: freeform modelling |
| [eval: drawing](docs/eval/drawing/REPORT.md) | Eval report: drawing production |
| [eval: layout](docs/eval/layout/REPORT.md) | Eval report: layout |
| [eval: scale](docs/eval/scale/REPORT.md) | Eval report: scale |
| [eval: rebuild](docs/eval/rebuild/REPORT.md) | Eval report: rebuild |
| [eval: carabiner](docs/eval/carabiner/REPORT.md) | Eval report: carabiner |
