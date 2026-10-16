<#
.SYNOPSIS
  Builds dist\RhinoMCP-<version>-windows.zip: RhinoMCP.rhp, RhinoMCP.Tools.<stamp>.dll, rhinomcp-bridge.exe,
  install.ps1, verify.ps1, INSTALL-windows.md and the tests\ needed by verify.ps1.

.PARAMETER DistDir  Folder with the built artifacts (default: <repo>\dist).
.PARAMETER Version  Override the version (default: AssemblyVersion in src\Core\Plugin.cs).
#>
[CmdletBinding()]
param(
    [string]$DistDir,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $DistDir) { $DistDir = Join-Path $root 'dist' }
if (-not $Version) {
    $m = Select-String -Path (Join-Path $root 'src\Core\Plugin.cs') -Pattern 'AssemblyVersion\("(\d+\.\d+\.\d+)\.' | Select-Object -First 1
    if (-not $m) { throw 'cannot read AssemblyVersion from src\Core\Plugin.cs' }
    $Version = $m.Matches[0].Groups[1].Value
}
$tools = Get-ChildItem -Path $DistDir -Filter 'RhinoMCP.Tools.*.dll' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
foreach ($f in @('RhinoMCP.rhp', 'rhinomcp-bridge.exe')) { if (-not (Test-Path (Join-Path $DistDir $f))) { throw "missing: $DistDir\$f" } }
if (-not $tools) { throw "missing: $DistDir\RhinoMCP.Tools.*.dll" }

$name = "RhinoMCP-$Version-windows"
$stage = Join-Path $DistDir "zip-stage\$name"
if (Test-Path (Join-Path $DistDir 'zip-stage')) { Remove-Item -Recurse -Force (Join-Path $DistDir 'zip-stage') }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $DistDir 'RhinoMCP.rhp') $stage
Copy-Item $tools.FullName $stage
Copy-Item (Join-Path $DistDir 'rhinomcp-bridge.exe') $stage
Copy-Item (Join-Path $root 'scripts\windows\install.ps1') $stage
Copy-Item (Join-Path $root 'scripts\windows\verify.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'windows\INSTALL-windows.md') $stage
$td = Join-Path $stage 'tests'
New-Item -ItemType Directory -Force -Path $td | Out-Null
Get-ChildItem (Join-Path $root 'tests') -Filter '*.py' | Copy-Item -Destination $td

$zip = Join-Path $DistDir "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -Recurse -Force (Join-Path $DistDir 'zip-stage')
Write-Host "OK: $zip"
