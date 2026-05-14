# E2E test of the Gateway against the temp KB, exercising read + write tools
# through the MCP JSON-RPC stdio loop. Uses config.test.json (temp KB).

$ErrorActionPreference = 'Stop'
$exe = 'C:\Proyectos\GxGenie\GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe'
if (-not (Test-Path $exe)) { throw "Gateway not built: $exe" }

$env:GXGENIE_CONFIG = 'C:\Proyectos\GxGenie\config.test.json'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)

$stderrSb = [System.Text.StringBuilder]::new()
Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action {
    if ($EventArgs.Data) { $script:stderrSb.AppendLine($EventArgs.Data) | Out-Null }
} | Out-Null
$p.BeginErrorReadLine()

function Send-Request($obj) {
    $json = ($obj | ConvertTo-Json -Compress -Depth 20)
    $p.StandardInput.WriteLine($json)
    $p.StandardInput.Flush()
}
function Read-Line() { return $p.StandardOutput.ReadLine() }

# 1) initialize
Send-Request @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2024-11-05'; capabilities=@{}; clientInfo=@{ name='test'; version='0' } } }
$null = Read-Line
Write-Host "[OK] initialize"

# 2) tools/list — confirm new tools are advertised
Send-Request @{ jsonrpc='2.0'; id=2; method='tools/list' }
$r = (Read-Line | ConvertFrom-Json)
$names = $r.result.tools | ForEach-Object { $_.name }
Write-Host "[OK] tools/list — $($names.Count) tools: $($names -join ', ')"

# 3) gx_kb_info on temp KB
Send-Request @{ jsonrpc='2.0'; id=3; method='tools/call'; params=@{ name='gx_kb_info'; arguments=@{} } }
$r = (Read-Line | ConvertFrom-Json)
$payload = $r.result.content[0].text | ConvertFrom-Json
Write-Host "[OK] gx_kb_info — KB=$($payload.kbName), entities=$($payload.totalEntities), procedures=$($payload.objectCounts.Procedure)"

# 4) gx_create_procedure via the Gateway
$procName = "MCPGwTest_" + (Get-Random -Maximum 99999)
Send-Request @{
    jsonrpc='2.0'; id=4; method='tools/call'
    params=@{ name='gx_create_procedure'; arguments=@{ name=$procName; description='Created via Gateway E2E test'; source="msg('via gateway')" } }
}
$r = (Read-Line | ConvertFrom-Json)
if ($r.result.isError) { throw "create_procedure failed: $($r.result.content[0].text)" }
$payload = $r.result.content[0].text | ConvertFrom-Json
Write-Host "[OK] gx_create_procedure — $procName, backup=$($payload.backup_path)"

# 5) gx_list_objects — verify it's there
Send-Request @{ jsonrpc='2.0'; id=5; method='tools/call'; params=@{ name='gx_list_objects'; arguments=@{ type='Procedure'; filter=$procName; limit=5 } } }
$r = (Read-Line | ConvertFrom-Json)
$payload = $r.result.content[0].text | ConvertFrom-Json
if ($payload.count -lt 1) { throw "list_objects didn't find $procName" }
Write-Host "[OK] gx_list_objects found $procName (id=$($payload.items[0].id))"

# 6) gx_read_object — verify the source is what we sent
Send-Request @{ jsonrpc='2.0'; id=6; method='tools/call'; params=@{ name='gx_read_object'; arguments=@{ name=$procName; type='Procedure' } } }
$r = (Read-Line | ConvertFrom-Json)
$payload = $r.result.content[0].text | ConvertFrom-Json
$src = $payload.parts.source
if ($src -notlike "*via gateway*") { throw "source didn't roundtrip: '$src'" }
Write-Host "[OK] gx_read_object — source roundtripped: $src"

# 7) gx_export_xpz
$xpz = "C:\Proyectos\GxGenie\GxGenie.Worker\probes\gw-$procName.xpz"
Send-Request @{ jsonrpc='2.0'; id=7; method='tools/call'; params=@{ name='gx_export_xpz'; arguments=@{ objects=@("Procedure:$procName"); output_path=$xpz } } }
$r = (Read-Line | ConvertFrom-Json)
$payload = $r.result.content[0].text | ConvertFrom-Json
if (-not $payload.success) { throw "export failed" }
Write-Host "[OK] gx_export_xpz — $($payload.bytes) bytes at $($payload.output_path)"

# Close stdin → Gateway exits cleanly
$p.StandardInput.Close()
$p.WaitForExit(5000) | Out-Null
Write-Host ""
Write-Host "Exit: $($p.ExitCode)"
if ($stderrSb.Length -gt 0) {
    Write-Host ""
    Write-Host "STDERR (gateway log):"
    Write-Host $stderrSb.ToString()
}
