<#
.SYNOPSIS
  Installs RhinoMCP for Rhino 7 on Windows (current user, no admin needed). Idempotent.

.DESCRIPTION
  Copies:
    RhinoMCP.rhp                -> %APPDATA%\RhinoMCP\plugin\RhinoMCP.rhp   (a stable folder you point Rhino at)
    RhinoMCP.Tools.<stamp>.dll  -> %APPDATA%\RhinoMCP\tools\               (3 newest kept)
    rhinomcp-bridge.exe         -> %APPDATA%\RhinoMCP\bin\
  Removes the "downloaded from the internet" mark (Unblock-File) from everything it copies: without this Rhino
  refuses to load a .rhp that came out of a downloaded zip.
  Prints (never runs) the `claude mcp add` line and the Claude Desktop / Cursor JSON. It never edits any config.

  Loading the plugin into Rhino 7 is a one-time manual step (see the output): drag RhinoMCP.rhp onto the Rhino
  window, or run _PlugInManager > Install... and pick the file. After that Rhino loads it at startup.
  Alternative: install the Yak package instead (-Yak, or `yak install` yourself).

.PARAMETER Source
  Folder containing RhinoMCP.rhp, RhinoMCP.Tools.*.dll and rhinomcp-bridge.exe. Default: the folder of this
  script (release zip), else <repo>\dist (source checkout).
.PARAMETER DataDir
  Override %APPDATA%\RhinoMCP (testing).
.PARAMETER Yak
  Also run `yak install` on a rhino-mcp-*-rh7_0-win.yak found in Source (needs Rhino 7's Yak.exe).
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$DataDir,
    [switch]$Yak
)
$ErrorActionPreference = 'Stop'

if (-not $DataDir) { $DataDir = Join-Path $env:APPDATA 'RhinoMCP' }

function Find-Source {
    $cands = @()
    if ($Source) { $cands += $Source }
    $cands += $PSScriptRoot
    $cands += (Join-Path $PSScriptRoot '..\..\dist')
    foreach ($c in $cands) {
        if ($c -and (Test-Path (Join-Path $c 'RhinoMCP.rhp'))) { return (Resolve-Path $c).Path }
    }
    throw "RhinoMCP.rhp not found. Run this script from the unzipped release folder, from a built checkout (dist\), or pass -Source <folder>."
}

$src = Find-Source
$tools = Get-ChildItem -Path $src -Filter 'RhinoMCP.Tools.*.dll' | Sort-Object Name | Select-Object -Last 1
if (-not $tools) { throw "RhinoMCP.Tools.*.dll not found in $src" }
$bridgeSrc = Join-Path $src 'rhinomcp-bridge.exe'
if (-not (Test-Path $bridgeSrc)) { throw "rhinomcp-bridge.exe not found in $src" }

$plugDir = Join-Path $DataDir 'plugin'
$toolsDir = Join-Path $DataDir 'tools'
$binDir = Join-Path $DataDir 'bin'
foreach ($d in @($plugDir, $toolsDir, $binDir)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# Is Rhino running? A loaded .rhp cannot be overwritten.
$rhino = Get-Process -Name 'Rhino' -ErrorAction SilentlyContinue
if ($rhino) {
    Write-Warning "Rhino is running. If copying RhinoMCP.rhp fails, close Rhino and run this script again."
}

Copy-Item -Force (Join-Path $src 'RhinoMCP.rhp') (Join-Path $plugDir 'RhinoMCP.rhp')
Copy-Item -Force $tools.FullName $toolsDir
$pdb = [IO.Path]::ChangeExtension($tools.FullName, '.pdb')
if (Test-Path $pdb) { Copy-Item -Force $pdb $toolsDir }
# keep the 3 newest tools dlls
$old = Get-ChildItem -Path $toolsDir -Filter 'RhinoMCP.Tools.*.dll' | Sort-Object Name -Descending | Select-Object -Skip 3
foreach ($o in $old) {
    Remove-Item -Force $o.FullName
    $p = [IO.Path]::ChangeExtension($o.FullName, '.pdb'); if (Test-Path $p) { Remove-Item -Force $p }
}
Copy-Item -Force $bridgeSrc (Join-Path $binDir 'rhinomcp-bridge.exe')

Get-ChildItem -Path $plugDir, $toolsDir, $binDir -File -Recurse | Unblock-File

$rhp = Join-Path $plugDir 'RhinoMCP.rhp'
$bridge = Join-Path $binDir 'rhinomcp-bridge.exe'

if ($Yak) {
    $yakPkg = Get-ChildItem -Path $src -Filter 'rhino-mcp-*-rh7_0-win.yak' | Sort-Object Name | Select-Object -Last 1
    $yakExe = @("${env:ProgramFiles}\Rhino 7\System\Yak.exe", "${env:ProgramFiles}\Rhino 7\System\yak.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($yakPkg -and $yakExe) {
        Write-Host "Installing Yak package $($yakPkg.Name) ..."
        & $yakExe install $yakPkg.FullName
    } else {
        Write-Warning "Yak install skipped (package or Yak.exe not found)."
    }
}

$cfg = Join-Path $DataDir 'config.json'
$port = 7340
if (Test-Path $cfg) {
    try { $j = Get-Content -Raw $cfg | ConvertFrom-Json; if ($j.port) { $port = $j.port } } catch { }
}
$desktopJson = (@{ mcpServers = @{ rhino = @{ command = $bridge } } } | ConvertTo-Json -Depth 5)

Write-Host ''
Write-Host 'Installed:' -ForegroundColor Green
Write-Host "  plugin  $rhp"
Write-Host "  tools   $(Join-Path $toolsDir $tools.Name)"
Write-Host "  bridge  $bridge"
Write-Host ''
Write-Host '=== Load the plugin into Rhino 7 (one time) ===' -ForegroundColor Cyan
Write-Host @"
Option A (drag and drop): start Rhino 7, then drag
    $rhp
  from Explorer onto the Rhino window. Rhino registers it and loads it.
Option B (Plug-in Manager): in Rhino run the command  _PlugInManager , click Install... and browse to the same file.
Then restart Rhino once. From then on the plugin loads at startup and starts the MCP server.
Optional check (command line shows a RhinoMCP startup line; the file below appears while Rhino runs):
    $(Join-Path $DataDir 'instances')\<pid>.json
"@
Write-Host ''
Write-Host '=== Connect a client (copy what you need; nothing below has been run) ===' -ForegroundColor Cyan
Write-Host @"

Claude Code (stdio, recommended):
  claude mcp add rhino -- "$bridge"

Claude Code (HTTP alternative, no bridge; the port is the one in config.json, here $port):
  `$tok = (Get-Content -Raw "$cfg" | ConvertFrom-Json).token
  claude mcp add --transport http rhino http://127.0.0.1:$port/mcp --header "Authorization: Bearer `$tok"

Claude Desktop - add to %APPDATA%\Claude\claude_desktop_config.json (merge into "mcpServers"), or install the .mcpb:
$desktopJson

Cursor - add to %USERPROFILE%\.cursor\mcp.json:
$desktopJson

Then run scripts\windows\verify.ps1 (or verify.ps1 from the zip) with Rhino running and send back verify-report.txt.
"@
