#!/usr/bin/env pwsh
# Publish AITestCrew as a self-contained deployment folder, OR build and start
# the Docker Compose stack on Docker Desktop (Windows containers).
#
# Usage:
#   .\publish.ps1                                  # Publish self-contained to ./publish/
#   .\publish.ps1 -OutputDir C:\deploy             # Publish to custom path
#   .\publish.ps1 -Docker                          # Build UI, then `docker compose up -d --build`
#   .\publish.ps1 -Docker -SyncDockerConfig        # Same as -Docker, plus overwrite docker-config/appsettings.json
#                                                  # from src/AiTestCrew.WebApi/appsettings.json before deploying.
#                                                  # Use during dev when you want every config edit in the source
#                                                  # tree to flow into the running container. Without it, the
#                                                  # script never overwrites docker-config/appsettings.json (only
#                                                  # warns when source is newer) so production secrets stashed
#                                                  # there are safe.
#
# Folder publish output contains everything needed to run the server:
#   cd <output-dir>
#   .\AiTestCrew.WebApi.exe              # Runs on http://localhost:5050
#
# Docker mode requires Docker Desktop in Windows containers mode and a populated
# .env file (copy from .env.example). See docs/deployment.md → Option 2.
#
# Configure via appsettings.json in the output folder, or via AITESTCREW_* env vars.

param(
    [string]$OutputDir = "$PSScriptRoot/publish",
    [switch]$Docker,
    [switch]$SyncDockerConfig
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($SyncDockerConfig -and -not $Docker) {
    throw "-SyncDockerConfig only applies to Docker mode. Add -Docker, or drop -SyncDockerConfig (folder publishes have their own appsettings.json copy step)."
}

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

if ($Docker) {
    # ── Docker Compose build + up ──
    # The Dockerfile runs its own `dotnet publish` inside the SDK image, so the
    # host-side .NET publish is skipped here. The UI must be pre-built on the
    # host (done above) because Vite's native rolldown bindings can't load in
    # Windows Server Core containers — see Dockerfile header.

    if (!(Test-Path "$root/.env")) {
        throw ".env not found. Copy .env.example to .env and set AITESTCREW_TestEnvironment__LlmApiKey before running with -Docker."
    }

    # ── Bootstrap docker-config/appsettings.json (volume-mounted at runtime) ──
    # docker-compose.yml mounts ./docker-config to C:/config inside the container.
    # WebApi loads C:/config/appsettings.json AFTER the baked-in appsettings.json,
    # so the volume mount overrides whatever the image was built with — that's
    # the file the running container actually reads creds from at runtime.
    #
    # We never overwrite an existing docker-config/appsettings.json (it holds the
    # user's real password / API keys) but we do bootstrap it from the example
    # on first deploy, and we warn when the source-controlled appsettings.json
    # has been edited more recently than the live one — that's the case where
    # someone forgets the override exists.
    $dockerConfigDir = Join-Path $root "docker-config"
    $dockerConfig = Join-Path $dockerConfigDir "appsettings.json"
    $sourceAppsettings = Join-Path $root "src/AiTestCrew.WebApi/appsettings.json"
    $exampleAppsettings = Join-Path $root "src/AiTestCrew.WebApi/appsettings.example.json"

    if (!(Test-Path $dockerConfigDir)) { New-Item -ItemType Directory -Path $dockerConfigDir | Out-Null }

    if (!(Test-Path $dockerConfig)) {
        $bootstrapSource = if (Test-Path $sourceAppsettings) { $sourceAppsettings } else { $exampleAppsettings }
        Copy-Item $bootstrapSource $dockerConfig
        Write-Host "Bootstrapped $dockerConfig from $bootstrapSource — edit it before re-running, the image won't pick up appsettings changes any other way." -ForegroundColor Yellow
    } elseif ($SyncDockerConfig) {
        # Caller opted in to force-sync — overwrite docker-config from the source tree.
        # Loud confirmation so anyone passing this in CI / on a shared box sees what
        # they just clobbered.
        if (!(Test-Path $sourceAppsettings)) {
            throw "-SyncDockerConfig requires src/AiTestCrew.WebApi/appsettings.json to exist."
        }
        Copy-Item $sourceAppsettings $dockerConfig -Force
        Write-Host "-SyncDockerConfig: copied $sourceAppsettings -> $dockerConfig (overwrote previous content)." -ForegroundColor Cyan
    } elseif ((Test-Path $sourceAppsettings) -and ((Get-Item $sourceAppsettings).LastWriteTimeUtc -gt (Get-Item $dockerConfig).LastWriteTimeUtc)) {
        Write-Host "WARNING: src/AiTestCrew.WebApi/appsettings.json is newer than docker-config/appsettings.json." -ForegroundColor Yellow
        Write-Host "         The container reads docker-config/appsettings.json at runtime — your source-tree edits are NOT picked up." -ForegroundColor Yellow
        Write-Host "         Re-run with -SyncDockerConfig to overwrite, or merge changes into docker-config/appsettings.json manually." -ForegroundColor Yellow
    }

    Write-Host "`n=== Building and starting Docker Compose stack ===" -ForegroundColor Cyan
    Push-Location $root
    try {
        docker compose up -d --build
        if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }
    } finally {
        Pop-Location
    }

    Write-Host "`n=== Docker stack running ===" -ForegroundColor Green
    Write-Host "Dashboard: http://localhost:5050"
    Write-Host ""
    Write-Host "Useful commands:" -ForegroundColor Cyan
    Write-Host "  docker compose logs -f      # Tail logs"
    Write-Host "  docker compose ps           # Show container status"
    Write-Host "  docker compose down         # Stop (preserves data volume)"
    return
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
