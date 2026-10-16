<#
.SYNOPSIS
  Reports whether System.Net.HttpListener can bind http://127.0.0.1:PORT/ and http://localhost:PORT/ as the
  CURRENT user, and whether a request actually reaches it with the expected Host header.
  The plugin's server relies on this (127.0.0.1 first, localhost as fallback). Exit code is always 0; the
  result is the printed report (and, with -Json, one JSON line per prefix for machine reading).
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$Json
)
$ErrorActionPreference = 'Continue'
if ($Port -eq 0) {
    $tmp = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $tmp.Start(); $Port = $tmp.LocalEndpoint.Port; $tmp.Stop()
}
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "user=$($id.Name) admin=$admin port=$Port os=$([Environment]::OSVersion.VersionString)"

foreach ($hostName in @('127.0.0.1', 'localhost')) {
    $prefix = "http://${hostName}:$Port/"
    $res = [ordered]@{ prefix = $prefix; user = $id.Name; admin = $admin; started = $false; reachable = $false; error = $null; win32 = $null }
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add($prefix)
    try {
        $l.Start()
        $res.started = $true
        $task = $l.GetContextAsync()
        try {
            $req = [System.Net.HttpWebRequest]::Create($prefix + 'probe')
            $req.Proxy = $null; $req.Timeout = 5000
            $respTask = $req.GetResponseAsync()
            if ($task.Wait(5000)) {
                $ctx = $task.Result
                $seenHost = $ctx.Request.Headers['Host']
                $ctx.Response.StatusCode = 200
                $b = [Text.Encoding]::UTF8.GetBytes('ok')
                $ctx.Response.OutputStream.Write($b, 0, $b.Length); $ctx.Response.Close()
                $res.reachable = ($seenHost -eq "${hostName}:$Port")
                if (-not $res.reachable) { $res.error = "unexpected Host header '$seenHost'" }
                [void]$respTask.Wait(5000)
            } else { $res.error = 'request did not arrive within 5s' }
        } catch { $res.error = $_.Exception.Message }
    } catch [System.Net.HttpListenerException] {
        $res.error = $_.Exception.Message
        $res.win32 = $_.Exception.ErrorCode   # 5 = access denied (needs urlacl), 183 = already in use
    } catch { $res.error = $_.Exception.Message }
    finally { try { $l.Close() } catch { } }
    $state = if ($res.started -and $res.reachable) { 'WORKS' } elseif ($res.started) { 'BINDS-BUT-UNREACHABLE' } else { 'FAILS' }
    Write-Host ("{0,-24} {1}{2}" -f $prefix, $state, $(if ($res.error) { "  ($($res.error); win32=$($res.win32))" } else { '' }))
    if ($Json) { Write-Output ($res | ConvertTo-Json -Compress) }
}
exit 0
