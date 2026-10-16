<#
.SYNOPSIS
  Collect RhinoMCP diagnostics into a zip you can attach to a GitHub issue.
  The token in config.json is removed. Nothing is sent anywhere.
#>
param([string]$Out = (Join-Path (Get-Location) ("rhinomcp-logs-{0}.zip" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$ErrorActionPreference = 'Continue'
$Data = if ($env:RHINOMCP_DATA_DIR) { $env:RHINOMCP_DATA_DIR } else { Join-Path $env:APPDATA 'RhinoMCP' }
$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("rhinomcp-logs-" + [guid]::NewGuid())
$D = Join-Path $Tmp 'rhinomcp-logs'; New-Item -ItemType Directory -Force -Path $D | Out-Null

$rhino = Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\7.0\Install' -ErrorAction SilentlyContinue
@(
  "date: $((Get-Date).ToUniversalTime().ToString('o'))"
  "os: $([Environment]::OSVersion.VersionString) 64bit=$([Environment]::Is64BitOperatingSystem)"
  "rhino: $($rhino.Version) $($rhino.InstallPath)"
  "tools: $((Get-ChildItem (Join-Path $Data 'tools') -ErrorAction SilentlyContinue | ForEach-Object Name) -join ' ')"
  "bridge: $((Get-Item (Join-Path $Data 'bin\rhinomcp-bridge.exe') -ErrorAction SilentlyContinue).FullName)"
) | Set-Content (Join-Path $D 'environment.txt')

$cfg = Join-Path $Data 'config.json'
if (Test-Path $cfg) { $c = Get-Content $cfg -Raw | ConvertFrom-Json; if ($c.token) { $c.token = '<removed>' }; $c | ConvertTo-Json | Set-Content (Join-Path $D 'config.json') }
foreach ($f in 'crash.log') { $p = Join-Path $Data $f; if (Test-Path $p) { Copy-Item $p $D } }
Get-ChildItem (Join-Path $Data 'instances') -Filter *.json -ErrorAction SilentlyContinue | Copy-Item -Destination $D
$vr = Join-Path (Get-Location) 'verify-report.txt'; if (Test-Path $vr) { Copy-Item $vr $D }

try { Invoke-RestMethod -TimeoutSec 5 http://127.0.0.1:7340/health | ConvertTo-Json | Set-Content (Join-Path $D 'health.json') } catch { "health: $($_.Exception.Message)" | Set-Content (Join-Path $D 'health.json') }

Compress-Archive -Path $D -DestinationPath $Out -Force
Remove-Item -Recurse -Force $Tmp
Write-Host "Wrote $Out"
Write-Host "Please check it before sharing, then attach it to an issue: https://github.com/joosthel/RhinoMCP/issues/new/choose"
