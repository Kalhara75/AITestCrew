#!/usr/bin/env pwsh
# Copies Playwright auth state files from the local Runner bin directory
# into the Docker volume mount (./docker-auth-state/).
#
# Workflow for refreshing auth:
#   1. Run --auth-setup locally to capture a fresh session:
#        dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor
#        dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC
#   2. Run this script to sync the saved state into the container's volume:
#        .\refresh-auth-state.ps1
#   3. The container picks it up on the next test run (no restart needed —
#      Playwright reads the file on each context creation).
#
# Auth state is reused for `StorageStateMaxAgeHours` (default 8 hours in config).

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$sources = @(
    "$root/src/AiTestCrew.Runner/bin/Debug/net8.0-windows/bravecloud-auth-state.json",
    "$root/src/AiTestCrew.Runner/bin/Debug/net8.0-windows/legacy-auth-state.json"
)

$destDir = Join-Path $root "docker-auth-state"
New-Item -ItemType Directory -Path $destDir -Force | Out-Null

$copied = 0
foreach ($src in $sources) {
    if (Test-Path $src) {
        $destFile = Join-Path $destDir (Split-Path $src -Leaf)
        Copy-Item $src $destFile -Force
        $size = [math]::Round((Get-Item $destFile).Length / 1KB, 1)
        Write-Host "Copied: $(Split-Path $src -Leaf) ($size KB)" -ForegroundColor Green
        $copied++
    } else {
        Write-Host "Missing: $(Split-Path $src -Leaf) — run --auth-setup first" -ForegroundColor Yellow
    }
}

if ($copied -eq 0) {
    Write-Host "`nNo auth state files found. Run one of these to create them:" -ForegroundColor Red
    Write-Host "  dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor"
    Write-Host "  dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC"
    exit 1
}

Write-Host "`nSynced $copied file(s) to $destDir" -ForegroundColor Cyan
Write-Host "Container will pick these up on the next test run."
