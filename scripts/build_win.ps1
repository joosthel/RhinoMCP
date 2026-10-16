<#
.SYNOPSIS
  Windows dev build of RhinoMCP: dist\win\RhinoMCP.rhp (core) + dist\win\RhinoMCP.Tools.<stamp>.dll (tools). Rhino 7 only (net48, C# 7.3).
.DESCRIPTION
  Uses `dotnet build` (SDK) when available, else Visual Studio's msbuild. RhinoCommon comes from the NuGet package unless
  -LocalRhino is given (then it is referenced from the Rhino 7 install: C:\Program Files\Rhino 7\System).
  -Deploy copies the stamped tools dll (and pdb) to %APPDATA%\RhinoMCP\tools (keeps the newest 3) for hot reload (dev_reload).
  The Mac build is scripts/build.sh and is unrelated to this script. UNVERIFIED on a real Windows machine at the time of writing; CI builds the same csproj files.
.EXAMPLE
  scripts\build_win.ps1 -Deploy
  scripts\build_win.ps1 -LocalRhino -Configuration Debug
#>
param(
  [string]$Configuration = "Release",
  [switch]$LocalRhino,
  [string]$RhinoSystemDir = "C:\Program Files\Rhino 7\System",
  [switch]$Deploy,
  [switch]$Release   # unstamped RhinoMCP.Tools.dll (what packaging ships next to the .rhp)
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$stamp = Get-Date -Format "yyyyMMddHHmmss"
$props = @("-p:Configuration=$Configuration")
if (-not $Release) { $props += "-p:ToolsStamp=$stamp" }
if ($LocalRhino) {
  if (-not (Test-Path (Join-Path $RhinoSystemDir "RhinoCommon.dll"))) { throw "RhinoCommon.dll not found in $RhinoSystemDir (pass -RhinoSystemDir)" }
  $props += "-p:RhinoSystemDir=$RhinoSystemDir"
}

$proj = "src\Tools\RhinoMCP.Tools.csproj"   # builds src\Core\RhinoMCP.csproj first
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
  Write-Host "==> dotnet build $proj"
  & dotnet build $proj @props -nologo -v:minimal
} else {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
  $msbuild = $null
  if (Test-Path $vswhere) { $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1 }
  if (-not $msbuild) { throw "Neither dotnet nor Visual Studio msbuild found. Install the .NET SDK or Visual Studio Build Tools." }
  Write-Host "==> msbuild $proj"
  & $msbuild /restore $proj @props /nologo /v:minimal
}
if ($LASTEXITCODE -ne 0) { throw "BUILD FAILED" }

$out = Join-Path $root "dist\win"
$rhp = Join-Path $out "RhinoMCP.rhp"
$tools = if ($Release) { Join-Path $out "RhinoMCP.Tools.dll" } else { Join-Path $out "RhinoMCP.Tools.$stamp.dll" }
if (-not (Test-Path $rhp))   { throw "missing $rhp" }
if (-not (Test-Path $tools)) { throw "missing $tools" }
Write-Host "OK: $rhp, $tools"

if ($Deploy) {
  $dest = Join-Path $env:APPDATA "RhinoMCP\tools"
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  Copy-Item $tools $dest -Force
  $pdb = [System.IO.Path]::ChangeExtension($tools, ".pdb")
  if (Test-Path $pdb) { Copy-Item $pdb $dest -Force }
  Get-ChildItem $dest -Filter "RhinoMCP.Tools.*.dll" | Sort-Object Name -Descending | Select-Object -Skip 3 | ForEach-Object {
    Remove-Item $_.FullName -Force; Remove-Item ([System.IO.Path]::ChangeExtension($_.FullName, ".pdb")) -Force -ErrorAction SilentlyContinue
  }
  Write-Host "deployed tools dll to $dest"
}
