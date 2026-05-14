# Test harness for the Gateway. Spawns it, sends 3 JSON-RPC requests over stdin,
# reads responses from stdout, prints a summary. Run from anywhere — paths are absolute.

$ErrorActionPreference = 'Stop'
$exe = 'C:\Proyectos\GxGenie\GxGenie.Gateway\bin\Release\net8.0\GxGenie.Gateway.exe'
if (-not (Test-Path $exe)) { throw "Gateway not built: $exe" }

$env:GXGENIE_CONFIG = 'C:\Proyectos\GxGenie\config.json'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$p = [System.Diagnostics.Process]::Start($psi)

# Async stderr drain so it doesn't deadlock
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

function Read-Line() {
    return $p.StandardOutput.ReadLine()
}

# 1) initialize
Send-Request @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'test'; version = '0' } } }
$r1 = Read-Line
Write-Host "INITIALIZE → $r1"

# 2) tools/list
Send-Request @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
$r2 = Read-Line
Write-Host ""
Write-Host "TOOLS/LIST → " -NoNewline
$r2obj = $r2 | ConvertFrom-Json
Write-Host "$($r2obj.result.tools.Count) tools: $(($r2obj.result.tools | ForEach-Object { $_.name }) -join ', ')"

# 3) tools/call gx_kb_info
Send-Request @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'gx_kb_info'; arguments = @{} } }
$r3 = Read-Line
Write-Host ""
Write-Host "TOOLS/CALL gx_kb_info →"
$r3obj = $r3 | ConvertFrom-Json
$inner = $r3obj.result.content[0].text | ConvertFrom-Json
Write-Host "  kbName       : $($inner.kbName)"
Write-Host "  kbVersion    : $($inner.kbVersion)"
Write-Host "  totalEntities: $($inner.totalEntities)"
Write-Host "  models       : $($inner.models.Count)"
Write-Host "  objectCounts : $(($inner.objectCounts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ')"

# 4) tools/call gx_list_objects (limited)
Send-Request @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'gx_list_objects'; arguments = @{ type = 'Transaction'; filter = 'BOrden'; limit = 3 } } }
$r4 = Read-Line
Write-Host ""
Write-Host "TOOLS/CALL gx_list_objects(Transaction, BOrden, 3) →"
$r4obj = $r4 | ConvertFrom-Json
$inner4 = $r4obj.result.content[0].text | ConvertFrom-Json
Write-Host "  count: $($inner4.count)"
$inner4.items | ForEach-Object { Write-Host "  - $($_.name) [$($_.type)]" }

# Close stdin → Gateway exits cleanly
$p.StandardInput.Close()
$p.WaitForExit(5000) | Out-Null

Write-Host ""
Write-Host "Exit code: $($p.ExitCode)"
if ($stderrSb.Length -gt 0) {
    Write-Host ""
    Write-Host "STDERR:"
    Write-Host $stderrSb.ToString()
}
