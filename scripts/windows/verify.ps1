<#
.SYNOPSIS
  Verifies a RhinoMCP install on Windows against the LIVE Rhino 7 and writes verify-report.txt.

.DESCRIPTION
  Start Rhino 7 (with the RhinoMCP plugin loaded, window not minimized) before running.
  Always runs:  discovery, /health on 127.0.0.1 and localhost, MCP smoke test (initialize, tools/list count,
                context_get, geometry_create box + measure volume 500 + history_undo, view_capture PNG,
                drawing_make2d on a box), then the same initialize/tools/list/context_get through
                rhinomcp-bridge.exe over stdio.
  If Python 3 is found: also runs  python -m unittest  on tests\test_*.py against the live Rhino (several minutes;
                skip with -SkipPython). The suites create and delete objects on layers named _mcp_test*.
  Everything created by the smoke test is undone/deleted. Use an empty or scratch document anyway.

.PARAMETER Report   Report path (default: verify-report.txt in the current folder).
.PARAMETER DataDir  Override %APPDATA%\RhinoMCP.
.PARAMETER Bridge   Path of rhinomcp-bridge.exe (default: <DataDir>\bin\rhinomcp-bridge.exe).
.PARAMETER TestsDir Folder with test_*.py (default: ..\..\tests next to the script, or .\tests in the zip).
#>
[CmdletBinding()]
param(
    [string]$Report = (Join-Path (Get-Location) 'verify-report.txt'),
    [string]$DataDir,
    [string]$Bridge,
    [string]$TestsDir,
    [switch]$SkipPython
)
$ErrorActionPreference = 'Continue'
if (-not $DataDir) { $DataDir = Join-Path $env:APPDATA 'RhinoMCP' }
if (-not $Bridge) { $Bridge = Join-Path $DataDir 'bin\rhinomcp-bridge.exe' }
if (-not $TestsDir) {
    foreach ($c in @((Join-Path $PSScriptRoot 'tests'), (Join-Path $PSScriptRoot '..\..\tests'))) {
        if (Test-Path (Join-Path $c 'mcp_client.py')) { $TestsDir = (Resolve-Path $c).Path; break }
    }
}

$script:lines = New-Object System.Collections.ArrayList
$script:fail = 0
$script:pass = 0
function Out-Line([string]$s) { [void]$script:lines.Add($s); Write-Host $s }
function Check([string]$name, [bool]$cond, [string]$detail) {
    if ($cond) { $script:pass++; Out-Line "PASS  $name" }
    else { $script:fail++; Out-Line "FAIL  $name  $detail" }
}
function Section([string]$t) { Out-Line ''; Out-Line "== $t" }

# ---------------------------------------------------------------- HTTP helpers (no proxy, status codes returned)
function Invoke-Http([string]$method, [string]$url, [string]$body, [hashtable]$headers, [int]$timeoutSec = 120) {
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = $method
    $req.Proxy = $null
    $req.Timeout = $timeoutSec * 1000
    $req.ReadWriteTimeout = $timeoutSec * 1000
    if ($headers) { foreach ($k in $headers.Keys) { if ($k -eq 'Content-Type') { $req.ContentType = $headers[$k] } elseif ($k -eq 'Accept') { $req.Accept = $headers[$k] } else { $req.Headers[$k] = $headers[$k] } } }
    if ($body) {
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($body)
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
    }
    try { $resp = $req.GetResponse() }
    catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if (-not $resp) { return @{ Status = 0; Body = $_.Exception.Message } }
    }
    $sr = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
    $text = $sr.ReadToEnd(); $sr.Close()
    $status = [int]$resp.StatusCode; $resp.Close()
    return @{ Status = $status; Body = $text }
}

$script:token = $null; $script:base = $null; $script:rid = 0
function Rpc([string]$method, [string]$paramsJson) {
    $script:rid++
    $p = if ($paramsJson) { ",`"params`":$paramsJson" } else { '' }
    $body = "{`"jsonrpc`":`"2.0`",`"id`":$($script:rid),`"method`":`"$method`"$p}"
    $r = Invoke-Http 'POST' "$($script:base)/mcp" $body @{ 'Content-Type' = 'application/json'; 'Accept' = 'application/json, text/event-stream'; 'Authorization' = "Bearer $($script:token)" }
    if ($r.Status -ne 200) { return @{ http = $r.Status; error = $r.Body } }
    try { return ($r.Body | ConvertFrom-Json) } catch { return @{ http = 200; error = "unparseable: $($r.Body.Substring(0, [Math]::Min(200, $r.Body.Length)))" } }
}
# Calls a tool; returns @{ rpc=; payload=<parsed first text block>; images=@(...); ok=bool }
function Tool([string]$name, [string]$argsJson) {
    if (-not $argsJson) { $argsJson = '{}' }
    $r = Rpc 'tools/call' "{`"name`":`"$name`",`"arguments`":$argsJson}"
    $out = @{ rpc = $r; ok = $false; payload = $null; images = @() }
    if (-not $r.result) { return $out }
    foreach ($c in $r.result.content) {
        if ($c.type -eq 'text' -and -not $out.payload) { try { $out.payload = $c.text | ConvertFrom-Json } catch { $out.payload = $null } }
        if ($c.type -eq 'image') { $out.images += $c }
    }
    $out.ok = ($out.payload -ne $null -and $out.payload.ok -eq $true)
    return $out
}

# ---------------------------------------------------------------- environment
Out-Line "RhinoMCP verify report  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Section 'Environment'
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { Out-Line "OS:          $($os.Caption) $($os.Version) ($($os.OSArchitecture))" }
Out-Line "PowerShell:  $($PSVersionTable.PSVersion)"
$isAdmin = 'unknown'
try { $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { }
Out-Line "User admin:  $isAdmin"
Out-Line "DataDir:     $DataDir"
Out-Line "Bridge:      $Bridge  (exists: $(Test-Path $Bridge))"
$netrel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
Out-Line ".NET Framework release key: $netrel (528040 or higher = 4.8)"
$rhinoExe = @("${env:ProgramFiles}\Rhino 7\System\Rhino.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($rhinoExe) { Out-Line "Rhino.exe:   $rhinoExe  $((Get-Item $rhinoExe).VersionInfo.ProductVersion)" }
$tools = Get-ChildItem (Join-Path $DataDir 'tools') -Filter 'RhinoMCP.Tools.*.dll' -ErrorAction SilentlyContinue
Out-Line "Tools dlls:  $(($tools | ForEach-Object { $_.Name }) -join ', ')"
$rhp = Join-Path $DataDir 'plugin\RhinoMCP.rhp'
Out-Line "Plugin copy: $rhp (exists: $(Test-Path $rhp))"

# ---------------------------------------------------------------- discovery
Section 'Discovery'
$inst = $null
$instDir = Join-Path $DataDir 'instances'
if (Test-Path $instDir) {
    $files = Get-ChildItem $instDir -Filter '*.json' | Sort-Object LastWriteTime -Descending
    foreach ($f in $files) {
        try { $j = Get-Content -Raw $f.FullName | ConvertFrom-Json } catch { continue }
        if ($j.pid -and (Get-Process -Id $j.pid -ErrorAction SilentlyContinue)) { $inst = $j; break }
    }
}
Check 'live instance file found' ($inst -ne $null) "no live <pid>.json under $instDir. Is Rhino running with the plugin loaded?"
$cfgPath = Join-Path $DataDir 'config.json'
if (Test-Path $cfgPath) { try { $script:token = (Get-Content -Raw $cfgPath | ConvertFrom-Json).token } catch { } }
Check 'config.json has a token' ([bool]$script:token) "missing $cfgPath"
if (-not $inst -or -not $script:token) {
    Out-Line ''; Out-Line 'Cannot continue without a running Rhino + token.'
    $script:lines | Set-Content -Encoding UTF8 $Report
    Write-Host "Report: $Report"; exit 1
}
Out-Line "instance: pid=$($inst.pid) port=$($inst.port) version=$($inst.version) startedAt=$($inst.startedAt)"
$script:base = "http://127.0.0.1:$($inst.port)"

# ---------------------------------------------------------------- health, Host handling, auth
Section 'HTTP surface'
foreach ($h in @('127.0.0.1', 'localhost')) {
    $r = Invoke-Http 'GET' "http://${h}:$($inst.port)/health" $null $null 15
    Check "GET /health via $h" ($r.Status -eq 200 -and $r.Body -match '"ok"\s*:\s*true') "status=$($r.Status) body=$($r.Body)"
    if ($r.Status -eq 200) { Out-Line "      $($r.Body)" }
}
$r = Invoke-Http 'POST' "$($script:base)/mcp" '{"jsonrpc":"2.0","id":1,"method":"ping"}' @{ 'Content-Type' = 'application/json' } 15
Check 'POST /mcp without token -> 401' ($r.Status -eq 401) "status=$($r.Status)"
$r = Invoke-Http 'POST' "$($script:base)/mcp" '{"jsonrpc":"2.0","id":1,"method":"ping"}' @{ 'Content-Type' = 'application/json'; 'Authorization' = "Bearer $($script:token)"; 'Origin' = 'http://evil.example' } 15
Check 'POST /mcp with Origin header -> 403' ($r.Status -eq 403) "status=$($r.Status)"

# ---------------------------------------------------------------- MCP smoke
Section 'MCP smoke test'
$created = @()
try {
    $r = Rpc 'initialize' '{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"verify.ps1","version":"1"}}'
    Check 'initialize' ($r.result -and $r.result.serverInfo) ($r | ConvertTo-Json -Compress -Depth 5)
    if ($r.result) { Out-Line "      server: $($r.result.serverInfo.name) $($r.result.serverInfo.version)" }
    $r = Rpc 'tools/list' $null
    $n = if ($r.result) { @($r.result.tools).Count } else { 0 }
    Check 'tools/list returns tools' ($n -gt 20) "count=$n"
    Out-Line "      tool count: $n"

    $t = Tool 'context_get' '{}'
    Check 'context_get ok' $t.ok ($t.rpc | ConvertTo-Json -Compress -Depth 4)
    if ($t.ok) { Out-Line "      rhino=$($t.payload.data.rhino) units=$($t.payload.data.units)" }

    $before = (Tool 'objects_query' '{"limit":1}')
    $total0 = if ($before.ok) { [int]$before.payload.data.total } else { -1 }

    $t = Tool 'geometry_create' '{"items":[{"kind":"box","corner":[0,0,0],"size":[10,10,5],"name":"verify_box"}],"layer":"_mcp_verify"}'
    $handle = $null
    if ($t.ok) { $it = $t.payload.data.items[0]; $handle = $it.handle }
    Check 'geometry_create box' ($t.ok -and $handle) ($t.rpc | ConvertTo-Json -Compress -Depth 6)
    if ($handle) {
        $created += $handle
        $vol = [double]$t.payload.data.items[0].volume
        Check 'box volume = 500' ([Math]::Abs($vol - 500.0) -lt 0.001) "volume=$vol"
        $m = Tool 'measure' "{`"target`":`"$handle`"}"
        $mv = if ($m.ok) { [double]$m.payload.data.totals.volume } else { -1 }
        Check 'measure volume = 500' ([Math]::Abs($mv - 500.0) -lt 0.001) "volume=$mv"
        $mid = Tool 'objects_query' '{"limit":1}'
        Check 'object count +1 after create' ($mid.ok -and [int]$mid.payload.data.total -eq ($total0 + 1)) "before=$total0 after=$($mid.payload.data.total)"
        $u = Tool 'history_undo' '{}'
        Check 'history_undo' $u.ok ($u.rpc | ConvertTo-Json -Compress -Depth 4)
        $after = Tool 'objects_query' '{"limit":1}'
        Check 'object count back after undo' ($after.ok -and [int]$after.payload.data.total -eq $total0) "expected=$total0 got=$($after.payload.data.total)"
        if ($after.ok -and [int]$after.payload.data.total -eq $total0) { $created = @() }
    }

    $t = Tool 'view_capture' '{"width":320,"height":200}'
    $png = $false
    if ($t.images.Count -gt 0) { $png = ($t.images[0].mimeType -eq 'image/png' -and $t.images[0].data.StartsWith('iVBOR')) }
    Check 'view_capture returns a PNG' ($t.ok -and $png) "images=$($t.images.Count) $(($t.rpc | ConvertTo-Json -Compress -Depth 3).Substring(0,[Math]::Min(300,($t.rpc | ConvertTo-Json -Compress -Depth 3).Length)))"
    if ($t.images.Count -gt 0) { Out-Line "      image: $($t.images[0].mimeType), base64 length $($t.images[0].data.Length)" }

    $t = Tool 'geometry_create' '{"items":[{"kind":"box","min":[0,0,0],"max":[1000,1000,500]}],"layer":"_mcp_verify"}'
    if ($t.ok) {
        $h2 = $t.payload.data.items[0].handle; $created += $h2
        $d = Tool 'drawing_make2d' "{`"target`":[`"$h2`"],`"view`":`"Top`",`"layer_root`":`"_mcp_verify_out::t`"}"
        $oo = if ($d.ok) { [int]$d.payload.data.output_objects } else { 0 }
        Check 'drawing_make2d on a box' ($d.ok -and $oo -gt 0) ($d.rpc | ConvertTo-Json -Compress -Depth 5)
        Out-Line "      output_objects: $oo"
    } else { Check 'drawing_make2d (box setup)' $false ($t.rpc | ConvertTo-Json -Compress -Depth 5) }
}
finally {
    # best-effort cleanup of everything the smoke test made
    $null = Tool 'objects_delete' '{"target":{"query":{"layer":"_mcp_verify_out","include_sublayers":true}},"confirm_count":100000}'
    $null = Tool 'objects_delete' '{"target":{"query":{"layer":"_mcp_verify","include_sublayers":true}},"confirm_count":100000}'
    $null = Tool 'layers_manage' '{"ops":[{"op":"delete","path":"_mcp_verify_out","recursive":true,"confirm_objects":0},{"op":"delete","path":"_mcp_verify","recursive":true,"confirm_objects":0}]}'
}

# ---------------------------------------------------------------- bridge over stdio
Section 'rhinomcp-bridge.exe (stdio -> live Rhino)'
if (Test-Path $Bridge) {
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Bridge; $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $psi.StandardInputEncoding = New-Object System.Text.UTF8Encoding($false)
        $psi.EnvironmentVariables['RHINOMCP_DATA_DIR'] = $DataDir
        $p = [System.Diagnostics.Process]::Start($psi)
        $send = {
            param($line)
            $p.StandardInput.WriteLine($line); $p.StandardInput.Flush()
        }
        & $send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"verify.ps1","version":"1"}}}'
        $l1 = $p.StandardOutput.ReadLine()
        & $send '{"jsonrpc":"2.0","method":"notifications/initialized"}'
        & $send '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
        $l2 = $p.StandardOutput.ReadLine()
        & $send '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"context_get","arguments":{}}}'
        $l3 = $p.StandardOutput.ReadLine()
        $p.StandardInput.Close()
        if (-not $p.WaitForExit(10000)) { $p.Kill() }
        $stderr = $p.StandardError.ReadToEnd()
        $j1 = $null; $j2 = $null; $j3 = $null
        try { $j1 = $l1 | ConvertFrom-Json } catch { }
        try { $j2 = $l2 | ConvertFrom-Json } catch { }
        try { $j3 = $l3 | ConvertFrom-Json } catch { }
        Check 'bridge: initialize reaches Rhino' ($j1 -and $j1.result -and $j1.result.serverInfo.name -notmatch 'bridge') "line=$l1"
        $names = if ($j2 -and $j2.result) { @($j2.result.tools | ForEach-Object { $_.name }) } else { @() }
        Check 'bridge: tools/list is the live list (not the placeholder)' ($names.Count -gt 20 -and ($names -notcontains 'rhino_status')) "tools=$($names.Count)"
        Check 'bridge: context_get ok' ($j3 -and $j3.result -and -not $j3.result.isError) "line=$(if ($l3) { $l3.Substring(0, [Math]::Min(200, $l3.Length)) })"
        Check 'bridge: exit code 0 on stdin close' ($p.ExitCode -eq 0) "exit=$($p.ExitCode)"
        if ($stderr) { Out-Line "      bridge stderr: $($stderr.Trim())" }
    } catch { Check 'bridge run' $false $_.Exception.Message }
} else { Check 'bridge present' $false "not found: $Bridge (run install.ps1 first)" }

# ---------------------------------------------------------------- python suites
Section 'Python test suites (live Rhino)'
$py = $null
foreach ($cand in @('python', 'py')) { $cmd = Get-Command $cand -ErrorAction SilentlyContinue; if ($cmd) { $py = $cmd.Source; break } }
if ($SkipPython) { Out-Line 'skipped (-SkipPython)' }
elseif (-not $py) { Out-Line 'Python not found: skipped (the PowerShell smoke test above is the fallback).' }
elseif (-not $TestsDir) { Out-Line 'tests folder not found: skipped.' }
else {
    $ver = (& $py --version 2>&1) -join ' '
    Out-Line "python: $py ($ver); tests: $TestsDir"
    $env:RHINOMCP_DATA_DIR = $DataDir
    $env:BRIDGE_CMD = "`"$Bridge`""
    $env:PYTHONIOENCODING = 'utf-8'
    Push-Location $TestsDir
    try {
        $out = & $py -m unittest discover -s . -p 'test_*.py' -v 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    $tail = $out | Select-Object -Last 60
    foreach ($l in ($out | Where-Object { $_ -match '(FAIL|ERROR|skipped)' } | Select-Object -First 80)) { Out-Line "  $l" }
    Out-Line '  ... last lines:'
    foreach ($l in $tail) { Out-Line "  $l" }
    Check 'python -m unittest (all test_*.py)' ($code -eq 0) "exit code $code"
}

# ---------------------------------------------------------------- summary
Section 'Summary'
Out-Line "passed: $script:pass   failed: $script:fail"
Out-Line $(if ($script:fail -eq 0) { 'RESULT: OK' } else { 'RESULT: FAILURES (send this file back)' })
$script:lines | Set-Content -Encoding UTF8 $Report
Write-Host ''
Write-Host "Report written to: $Report" -ForegroundColor Cyan
if ($script:fail -ne 0) { exit 1 }
