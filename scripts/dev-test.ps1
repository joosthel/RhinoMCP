<#
.SYNOPSIS
  Locked integration run on Windows: (optional) build, dev_reload, then python -m unittest on the given files.
.EXAMPLE
  scripts\dev-test.ps1 tests\test_core.py tests\test_geometry.py
  scripts\dev-test.ps1 -Build -LocalRhino tests\test_core.py
.NOTES
  Mirror of scripts/dev-test.sh. The lock is a directory (atomic mkdir) in %TEMP%. Requires Rhino 7 running with the RhinoMCP plugin
  installed, and python on PATH. Unverified on a real Windows machine at the time of writing.
#>
param(
  [switch]$Build,        # run scripts\build_win.ps1 -Deploy first (default: test whatever tools dll is already deployed)
  [switch]$LocalRhino,   # passed to build_win.ps1
  [Parameter(ValueFromRemainingArguments = $true)][string[]]$Tests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $Tests -or $Tests.Count -lt 1) { Write-Error "usage: dev-test.ps1 [-Build] [-LocalRhino] <test files...>"; exit 2 }

$lock = Join-Path $env:TEMP "rhinomcp-test.lock"
$waited = 0
while ($true) {
  try { New-Item -ItemType Directory -Path $lock -ErrorAction Stop | Out-Null; break } catch { }
  if ($waited -ge 600) { Write-Error "timed out waiting for $lock (10 min). Remove it if stale."; exit 1 }
  if ($waited % 30 -eq 0) { Write-Host "waiting for test lock ($lock)..." }
  Start-Sleep -Seconds 5; $waited += 5
}
try {
  $py = if (Get-Command python -ErrorAction SilentlyContinue) { "python" } else { "py" }
  $dll = $null
  if ($Build) {
    if ($LocalRhino) { & "$PSScriptRoot\build_win.ps1" -Deploy -LocalRhino } else { & "$PSScriptRoot\build_win.ps1" -Deploy }
    $dll = (Get-ChildItem (Join-Path $root "dist\win") -Filter "RhinoMCP.Tools.*.dll" | Sort-Object Name -Descending | Select-Object -First 1).FullName
  }
  if ($dll) {
    Write-Host "==> dev_reload $dll"
    $json = (@{ dll = $dll } | ConvertTo-Json -Compress)
  } else {
    Write-Host "==> dev_reload (newest in %APPDATA%\RhinoMCP\tools)"
    $json = "{}"
  }
  & $py "$PSScriptRoot\mcp.py" call dev_reload $json
  if ($LASTEXITCODE -ne 0) { throw "dev_reload failed" }

  Write-Host "==> tests"
  $env:PYTHONPATH = (Join-Path $root "tests") + $(if ($env:PYTHONPATH) { ";" + $env:PYTHONPATH } else { "" })
  & $py -m unittest -v @Tests
  exit $LASTEXITCODE
}
finally {
  Remove-Item $lock -Force -Recurse -ErrorAction SilentlyContinue
}
