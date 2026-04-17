#!/usr/bin/env pwsh
# Build all AITestCrew projects (.NET + React UI)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "`n=== Building .NET solution ===" -ForegroundColor Cyan
dotnet build "$root/AITestCrew.slnx"
if ($LASTEXITCODE -ne 0) { Write-Host "dotnet build FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== Building React UI ===" -ForegroundColor Cyan
Push-Location "$root/ui"
try {
    if (!(Test-Path "node_modules")) {
        Write-Host "Installing npm dependencies..." -ForegroundColor Yellow
        npm install
        if ($LASTEXITCODE -ne 0) { Write-Host "npm install FAILED" -ForegroundColor Red; exit 1 }
    }
    npx vite build
    if ($LASTEXITCODE -ne 0) { Write-Host "UI build FAILED" -ForegroundColor Red; exit 1 }
} finally {
    Pop-Location
}

# Copy the built React app into the WebApi's wwwroot so the API serves the SPA.
$uiDist   = "$root/ui/dist"
$wwwroot  = "$root/src/AiTestCrew.WebApi/wwwroot"

if (Test-Path $uiDist) {
    Write-Host "`n=== Copying UI build to WebApi wwwroot ===" -ForegroundColor Cyan
    if (Test-Path $wwwroot) { Remove-Item -Recurse -Force $wwwroot }
    Copy-Item -Recurse $uiDist $wwwroot
    Write-Host "Copied $(Get-ChildItem -Recurse $wwwroot -File | Measure-Object | Select-Object -Expand Count) files to wwwroot"
} else {
    Write-Host "WARNING: ui/dist not found — skipping wwwroot copy" -ForegroundColor Yellow
}

Write-Host "`nAll builds succeeded." -ForegroundColor Green
