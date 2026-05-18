<#
.SYNOPSIS
    Pulls the latest GxGenie commits from origin/main and rebuilds Worker + Gateway.

.DESCRIPTION
    Convenience script for users who already cloned the repo and want to
    update in one command. The script:

    1. Verifies no GxGenie.Worker.exe / GxGenie.Gateway.exe are running
       (those binaries are file-locked by Claude Code while it owns the MCP
       server, so the build would fail).
    2. Runs git pull origin main.
    3. Builds Worker and Gateway in Release configuration.

    The MCP registration (claude mcp add) and config.json / .mcp.json files
    are not touched. Check CHANGELOG.md for what changes per release.

.PARAMETER Force
    Skip the process check and try the build anyway. The build will probably
    fail if Claude Code is open, but useful if you know the locks are stale.

.EXAMPLE
    .\update.ps1

.EXAMPLE
    .\update.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

Write-Host "GxGenie update — repo at $root" -ForegroundColor Cyan
Write-Host ""

# 1. Check for running GxGenie processes (tasklist sees things Get-Process can miss
#    when the process is owned by another user session, e.g. Claude Code).
$running = & cmd /c 'tasklist /FI "IMAGENAME eq GxGenie.Worker.exe" /FI "IMAGENAME eq GxGenie.Gateway.exe" /NH 2>nul' |
    Where-Object { $_ -match 'GxGenie\.(Worker|Gateway)\.exe' }

if ($running -and -not $Force) {
    Write-Host "Aborted: GxGenie process(es) still running:" -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "Close every Claude Code window that has the MCP server loaded," -ForegroundColor Yellow
    Write-Host "then re-run .\update.ps1. To override (locks may be stale), use:" -ForegroundColor Yellow
    Write-Host "  .\update.ps1 -Force" -ForegroundColor Yellow
    exit 1
}
if ($running -and $Force) {
    Write-Host "WARNING: GxGenie processes running, forcing through -Force." -ForegroundColor Yellow
}

# 2. Pull
Write-Host "[1/3] git pull origin main" -ForegroundColor Green
& git -C $root pull origin main
if ($LASTEXITCODE -ne 0) { Write-Host "git pull failed (exit $LASTEXITCODE)" -ForegroundColor Red; exit 1 }
Write-Host ""

# 3. Build Worker
Write-Host "[2/3] dotnet build Worker -c Release" -ForegroundColor Green
& dotnet build (Join-Path $root 'GxGenie.Worker\GxGenie.Worker.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Worker build failed (exit $LASTEXITCODE)." -ForegroundColor Red
    Write-Host "If you see MSB3027 'file in use', close Claude Code and re-run." -ForegroundColor Red
    exit 1
}
Write-Host ""

# 4. Build Gateway
Write-Host "[3/3] dotnet build Gateway -c Release" -ForegroundColor Green
& dotnet build (Join-Path $root 'GxGenie.Gateway\GxGenie.Gateway.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Gateway build failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}
Write-Host ""

Write-Host "Update complete." -ForegroundColor Cyan
Write-Host "Open Claude Code — the next mcp__gxgenie__* call relaunches the Gateway." -ForegroundColor Cyan
Write-Host "See CHANGELOG.md for what's new in this version." -ForegroundColor Cyan
