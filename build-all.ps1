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

Write-Host "`nAll builds succeeded." -ForegroundColor Green
