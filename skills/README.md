# RhinoMCP agent skills

Agent skills in the Anthropic Agent Skills format: each `skills/<name>/SKILL.md` has YAML frontmatter (`name`, `description`) and a body of at most about 200 lines; detail lives in `references/*.md`, one level deep.

| Skill | Use it for |
|---|---|
| [rhino-mcp-efficient-use](rhino-mcp-efficient-use/SKILL.md) | Hub: session start, overview-query-act-verify loop, token rules, undo, failure playbook, tool map |
| [rhino-units-tolerances](rhino-units-tolerances/SKILL.md) | Units, absolute tolerance, imports, why operations fail |
| [rhino-geometry-hygiene](rhino-geometry-hygiene/SKILL.md) | Diagnosing and repairing bad geometry, degree, mesh export presets |
| [rhino-layer-standards](rhino-layer-standards/SKILL.md) | AIA/NCS, ISO 13567, UK ISO 19650, custom JSON schemes (English names only) |
| [rhino-drawing-production](rhino-drawing-production/SKILL.md) | Clean Make2D, line weights, hatches, layouts, title blocks, export |
| [rhino-architectural-massing](rhino-architectural-massing/SKILL.md) | Levels, grids, walls, slabs, stairs, area schedules, region-aware defaults |
| [rhino-product-design](rhino-product-design/SKILL.md) | Draft, continuity, wall thickness, fillets, STEP export checks |

Code values and standards in these skills are quoted from general knowledge and marked "verify" where precision matters. Check them against local code and client standards.

## How the skills are served

1. **Claude Code plugin.** The repository root is a plugin (`.claude-plugin/plugin.json`, name `rhino-mcp`) and a one-plugin marketplace (`.claude-plugin/marketplace.json`). Claude Code loads `skills/` from the plugin root. Install:

   ```
   /plugin marketplace add joosthel/RhinoMCP
   /plugin install rhino-mcp@rhino-mcp
   ```

   For local development: `claude --plugin-dir /path/to/RhinoMCP`.
2. **MCP resources and prompts.** The server exposes the same markdown through `resources/list` / `resources/read` (the `SKILL.md` and `references/` files) and `prompts/list` / `prompts/get` (workflows), and generates its short `instructions` text from `rhino-mcp-efficient-use`. This serves Cursor, Codex and other clients that do not load Claude skills.
3. **Manual.** Copy a skill directory into `~/.claude/skills/` or `.claude/skills/`. For claude.ai or Desktop, zip the skill directory and upload it.

## Connecting the server

The plugin deliberately ships no `.mcp.json`: the server URL carries a per-user token. In Rhino run the command `MCPConnect`. It prints the ready-made line to add the server, which looks like:

```
claude mcp add --transport http rhino http://127.0.0.1:7340/mcp --header "Authorization: Bearer <token>"
```

Use the exact line Rhino prints (the port may differ).

## Checking the skills

```
python3 scripts/check_skills.py
```

Validates frontmatter (name format, reserved words, description length and word count), body line counts, directory depth, and relative links.
