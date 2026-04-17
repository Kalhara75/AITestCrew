#!/usr/bin/env pwsh
# Publish AITestCrew as a self-contained deployment folder.
#
# Usage:
#   .\publish.ps1                        # Publish to ./publish/
#   .\publish.ps1 -OutputDir C:\deploy   # Publish to custom path
#
# The output folder contains everything needed to run the server:
#   cd <output-dir>
#   .\AiTestCrew.WebApi.exe              # Runs on http://localhost:5050
#
# Configure via appsettings.json in the output folder, or via AITESTCREW_* env vars.

param(
    [string]$OutputDir = "$PSScriptRoot/publish"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# ── Build React UI ──
Write-Host "`n=== Building React UI ===" -ForegroundColor Cyan
Push-Location "$root/ui"
try {
    if (!(Test-Path "node_modules")) {
        Write-Host "Installing npm dependencies..." -ForegroundColor Yellow
        npm install
        if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
    }
    npx vite build
    if ($LASTEXITCODE -ne 0) { throw "UI build failed" }
} finally {
    Pop-Location
}

# ── Publish .NET (self-contained, single directory) ──
Write-Host "`n=== Publishing .NET application ===" -ForegroundColor Cyan
dotnet publish "$root/src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj" `
    -c Release `
    -o $OutputDir `
    --self-contained `
    -r win-x64

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── Copy React build into wwwroot ──
$wwwroot = Join-Path $OutputDir "wwwroot"
if (Test-Path $wwwroot) { Remove-Item -Recurse -Force $wwwroot }
Copy-Item -Recurse "$root/ui/dist" $wwwroot

# ── Copy example config if no config exists ──
$targetConfig = Join-Path $OutputDir "appsettings.json"
if (!(Test-Path $targetConfig)) {
    Copy-Item "$root/src/AiTestCrew.WebApi/appsettings.example.json" $targetConfig
    Write-Host "Copied appsettings.example.json — edit $targetConfig before running." -ForegroundColor Yellow
}

$fileCount = (Get-ChildItem -Recurse $OutputDir -File | Measure-Object).Count
Write-Host "`n=== Published ($fileCount files) ===" -ForegroundColor Green
Write-Host "Output: $OutputDir"
Write-Host ""
Write-Host "To run:" -ForegroundColor Cyan
Write-Host "  cd $OutputDir"
Write-Host "  .\AiTestCrew.WebApi.exe"
Write-Host ""
Write-Host "Or install as a Windows Service:" -ForegroundColor Cyan
Write-Host "  sc.exe create AITestCrew binPath= `"$OutputDir\AiTestCrew.WebApi.exe`""
Write-Host "  sc.exe start AITestCrew"
