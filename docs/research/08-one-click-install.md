# 08 — One-click install & auto-start

> Source: Sonnet 5.5 research subagent, 2026-10-15, with read-only checks on this machine. **[U]** = unverified, needs a test.

## Bottom line
1. Ship **one Yak package** containing:
   - the `.rhp` (load at startup), which hosts an authenticated 127.0.0.1 HTTP endpoint;
   - a tiny signed native **`rhinomcp-bridge`** that converts stdio to HTTP.
2. Ship a **`.mcpb` (type `binary`)** with the same bridge for Claude Desktop. No Node, Python or JSON editing is needed.
3. The plugin's `MCPConnect` command adds us to Claude Code, Cursor, VS Code and Codex only after user consent, and shows the diff first.
4. If the plugin is missing, the bridge serves an install tool. This is the RhinoAI pattern.

## Local findings
- Rhino 7 reinstalled: **7.39**, still x86_64 only (Rosetta), hardened runtime.
- `~/Library/Application Support/McNeel/Rhinoceros/7.0/` has no `MacPlugIns` folder.
- Rhino 7 Mac ships `yak`, which reports the package dir `~/Library/Application Support/McNeel/Rhinoceros/packages/7.0`. **Yak works on R7 Mac.**

## Plugin install & auto-load
- Mac user folder `~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/<Name>.rhp/` is scanned at launch. `.macrhi` is legacy. Source: [Mac installers](https://developer.rhino3d.com/guides/rhinocommon/plugin-installers-mac/).
- **Yak tags** are `rh7_0-mac`, `rh7_0-win`, `rh8_0-mac`, `rh8_0-win` (not `rhino7_0`). See [distributions](https://discourse.mcneel.com/t/new-feature-distributions/98252) and the [CLI](https://developer.rhino3d.com/guides/yak/yak-cli-reference/).
  - `yak build` may infer the wrong tag. Workaround: rename the file.
  - Rhino 8 prefers an `rh8` distribution when one exists.
- R7 can't drag-and-drop `.yak` files. Use `_-PackageManager` ([forum](https://discourse.mcneel.com/t/rhino-for-mac-drag-drop-install-for-yak-files/164505)). R7 Mac Package Manager UI listing **[U]**.
- `PlugInLoadTime.AtStartup` is honoured only after the first load ([thread](https://discourse.mcneel.com/t/not-loading-rhino-plugin-at-startup/144438)). Expect one restart after install. Mac behaviour **[U]**.
- Keep `OnLoad` light. Start the server on first `RhinoApp.Idle`.

## Avoiding external runtimes
- **Direct HTTP from the plugin:**
  - Claude Code, Cursor, VS Code and Codex can connect to it **[U on each]**.
  - **Claude Desktop cannot.** Its config file runs only stdio servers, and Custom Connectors connect from Anthropic's cloud, so they can't reach localhost.
  - Conclusion: HTTP alone isn't enough.
- **Native bridge:**
  - .NET NativeAOT (same language as the plugin) or Go.
  - Ship osx-arm64 + osx-x64 (or a lipo'd universal binary) and win-x64.
  - **Developer ID sign and notarize** on Mac; unsigned arm64 binaries won't run at all. Authenticode on Windows.
- **`.mcpb`** ([manifest](https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md)):
  - Supported types are `binary`, `node`, `python`, `uv`.
  - Use `platform_overrides` for darwin and win32.
  - Claude Desktop bundles Node, so `node` is the fallback if `binary` misbehaves.
  - Submitting to the extension directory gives a true one-click install ([support](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop)).
- **Writing client configs:** only on an explicit click. Show the target file and diff, back up, merge, and abort on parse errors.

## Security for an auto-started server
- Bind `127.0.0.1`, not `localhost` or `0.0.0.0`.
- Reject any Host header other than `127.0.0.1:<port>` / `localhost:<port>`. Reject requests carrying an Origin header, and reject CORS preflights.
- 256-bit random token; `Authorization: Bearer` with constant-time comparison.
- **Discovery file:** `…/RhinoMCP/instances/<pid>.json` holds `{pid, port, version, doc, token, startTime}`. Mode 0600; deleted on exit.
  - The bridge ignores entries whose pid is dead.
  - It picks the newest instance, and offers a `list_rhino_instances` tool.
- Random free port. Fixed ports are why existing projects break with two Rhinos open.
- Visible status: a startup line on the command line, plus `MCPStatus` / `MCPStop` / `MCPStart`.
- First run asks for consent. Script execution sits behind its own setting.

## Recommended user flow
1. Install "Rhino MCP" from the Package Manager. Fallback: a signed `.pkg` / `.msi`.
2. Restart Rhino once. The plugin auto-loads, asks to enable the server, then starts automatically from then on.
3. **Claude Desktop:** double-click `rhino-mcp.mcpb` → Install → reopen Claude.
4. **Other clients:** run `MCPConnect` in Rhino → choose the client → Connect.
5. If Rhino is closed, the bridge answers "Open Rhino". If the plugin is missing, the bridge offers to install it.

## What we must build and sign
- `RhinoMCP.rhp`, two builds:
  - net48 for Rhino 7 (AnyCPU; Mono x86_64 on Mac);
  - net8.0 for Rhino 8.
- `rhinomcp-bridge` for osx-arm64, osx-x64 and win-x64. Signed and notarized.
- Yak packages: `rh7_0-mac`, `rh7_0-win`, `rh8_0-mac`, `rh8_0-win`.
- `rhino-mcp.mcpb` (binary type).
- Costs: an Apple Developer ID and a Windows code-signing certificate.

## Risks to test first
1. Does AtStartup take effect after a Yak-only install on R7 Mac?
2. Are `rh7_0-mac` tags honoured by R7 Mac's Package Manager?
3. Does `HttpListener` (or a raw TCP server) work under Mono on R7 Mac with loopback binding?
4. Does Claude Desktop run a signed `binary` `.mcpb` without prompts?
5. Does Gatekeeper block a bridge extracted by Claude Desktop or Yak?
