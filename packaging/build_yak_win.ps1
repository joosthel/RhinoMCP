<#
.SYNOPSIS
  Builds dist\rhino-mcp-<version>-rh7_0-win.yak (not installed, not published).
  Package contents: RhinoMCP.rhp, RhinoMCP.Tools.<stamp>.dll, rhinomcp-bridge.exe, manifest.yml.

.PARAMETER DistDir  Folder with the built RhinoMCP.rhp, RhinoMCP.Tools.*.dll and rhinomcp-bridge.exe (default: <repo>\dist).
.PARAMETER Yak      Path to Yak.exe. Default: Rhino 7's (C:\Program Files\Rhino 7\System\Yak.exe) or yak on PATH.
                    With -DownloadYak the McNeel CLI is fetched into dist\yak-cli (used by CI, where Rhino is not installed).
.PARAMETER Version  Override the version (default: AssemblyVersion in src\Core\Plugin.cs).
#>
[CmdletBinding()]
param(
    [string]$DistDir,
    [string]$Yak,
    [string]$Version,
    [switch]$DownloadYak
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $DistDir) { $DistDir = Join-Path $root 'dist' }

if (-not $Version) {
    $m = Select-String -Path (Join-Path $root 'src\Core\Plugin.cs') -Pattern 'AssemblyVersion\("(\d+\.\d+\.\d+)\.' | Select-Object -First 1
    if (-not $m) { throw 'cannot read AssemblyVersion from src\Core\Plugin.cs' }
    $Version = $m.Matches[0].Groups[1].Value
}

$rhp = Join-Path $DistDir 'RhinoMCP.rhp'
$tools = Get-ChildItem -Path $DistDir -Filter 'RhinoMCP.Tools.*.dll' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
$bridge = Join-Path $DistDir 'rhinomcp-bridge.exe'
foreach ($f in @($rhp, $bridge)) { if (-not (Test-Path $f)) { throw "missing: $f" } }
if (-not $tools) { throw "missing: $DistDir\RhinoMCP.Tools.*.dll" }

if (-not $Yak) {
    $cands = @("${env:ProgramFiles}\Rhino 7\System\Yak.exe", (Join-Path $DistDir 'yak-cli\yak.exe'))
    $Yak = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Yak) { $c = Get-Command yak -ErrorAction SilentlyContinue; if ($c) { $Yak = $c.Source } }
}
if (-not $Yak -and $DownloadYak) {
    $dir = Join-Path $DistDir 'yak-cli'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri 'https://files.mcneel.com/yak/tools/latest/yak.exe' -OutFile (Join-Path $dir 'yak.exe')
    $Yak = Join-Path $dir 'yak.exe'
}
if (-not $Yak) { throw 'Yak.exe not found. Install Rhino 7, pass -Yak <path>, or use -DownloadYak.' }

$stage = Join-Path $DistDir 'yak-stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $rhp $stage
Copy-Item $tools.FullName $stage
Copy-Item $bridge $stage
(Get-Content (Join-Path $PSScriptRoot 'yak\manifest.yml')) -replace '^version: .*', "version: $Version" | Set-Content -Encoding UTF8 (Join-Path $stage 'manifest.yml')

Push-Location $stage
try {
    & $Yak build --platform win
    if ($LASTEXITCODE -ne 0) { throw "yak build failed ($LASTEXITCODE)" }
    $pkg = Get-ChildItem -Filter '*.yak' | Select-Object -First 1
    if (-not $pkg) { throw 'yak produced no .yak file' }
    Get-ChildItem -Path $DistDir -Filter 'rhino-mcp-*-win.yak' -ErrorAction SilentlyContinue | Remove-Item -Force
    # Yak may infer another tag (e.g. rh7_0-any); force the Rhino 7 Windows tag.
    $target = Join-Path $DistDir "rhino-mcp-$Version-rh7_0-win.yak"
    Move-Item -Force $pkg.FullName $target
} finally { Pop-Location }
Remove-Item -Recurse -Force $stage
Write-Host "OK: $target"
