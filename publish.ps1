#!/usr/bin/env pwsh
# Publish AITestCrew as a self-contained deployment folder, OR build and start
# the Docker Compose stack on Docker Desktop (Windows containers).
#
# Usage:
#   .\publish.ps1                                  # Publish WebApi self-contained to ./publish/
#   .\publish.ps1 -OutputDir C:\deploy             # Publish WebApi to custom path
#   .\publish.ps1 -Docker                          # Build UI, then `docker compose up -d --build`
#   .\publish.ps1 -Docker -SyncDockerConfig        # Same as -Docker, plus overwrite docker-config/appsettings.json
#                                                  # from src/AiTestCrew.WebApi/appsettings.json before deploying.
#                                                  # Use during dev when you want every config edit in the source
#                                                  # tree to flow into the running container. Without it, the
#                                                  # script never overwrites docker-config/appsettings.json (only
#                                                  # warns when source is newer) so production secrets stashed
#                                                  # there are safe.
#   .\publish.ps1 -Runner                          # Publish QA agent pack to ./publish/runner/ + zip it
#   .\publish.ps1 -Runner -OutputDir C:\deploy     # Same, to a custom path (zip lands at <OutputDir>/AITestCrew-Agent.zip)
#
# WebApi publish output contains everything needed to run the server:
#   cd <output-dir>
#   .\AiTestCrew.WebApi.exe              # Runs on http://localhost:5050
#
# Runner pack output is a redistributable agent install:
#   <OutputDir>/runner/                  # Self-contained Runner + appsettings.json + start-agent.cmd + README.txt
#   <OutputDir>/AITestCrew-Agent.zip     # Same folder, zipped for handing to a QA engineer
#
# Docker mode requires Docker Desktop in Windows containers mode and a populated
# .env file (copy from .env.example). See docs/deployment.md → Option 2.
#
# Configure via appsettings.json in the output folder, or via AITESTCREW_* env vars.

param(
    [string]$OutputDir = "$PSScriptRoot/publish",
    [switch]$Docker,
    [switch]$SyncDockerConfig,
    [switch]$Runner,
    [string]$ServerUrl = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($SyncDockerConfig -and -not $Docker) {
    throw "-SyncDockerConfig only applies to Docker mode. Add -Docker, or drop -SyncDockerConfig (folder publishes have their own appsettings.json copy step)."
}

if ($Runner -and $Docker) {
    throw "-Runner and -Docker are mutually exclusive. -Runner builds the QA agent pack; -Docker deploys the server stack."
}

if ($ServerUrl -and -not $Runner) {
    throw "-ServerUrl only applies to -Runner mode."
}

# ── Runner pack mode (QA agent install bundle) ──
# Build only the Runner (no UI, no WebApi). The Runner is self-contained, ships
# with a templated appsettings.json (placeholders for ServerUrl + ApiKey), a
# start-agent.cmd auto-start script, and a 1-page README. The whole folder is
# then zipped into <OutputDir>/AITestCrew-Agent.zip so it can be handed to a QA
# engineer as a single attachment.
if ($Runner) {
    $runnerOut = Join-Path $OutputDir "runner"
    Write-Host "`n=== Publishing Runner (QA agent pack) ===" -ForegroundColor Cyan

    if (Test-Path $runnerOut) { Remove-Item -Recurse -Force $runnerOut }

    dotnet publish "$root/src/AiTestCrew.Runner/AiTestCrew.Runner.csproj" `
        -c Release `
        -o $runnerOut `
        --self-contained `
        -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Runner) failed" }

    # ── Sanitised appsettings.json built from the admin's own server config ──
    # Source priority: WebApi appsettings (most up-to-date with real env URLs) →
    # Runner appsettings → example. The chosen file is loaded, all secrets are
    # stripped (passwords, API keys, DB / Service Bus connection strings, TOTP
    # secrets, LLM key, local DB path, server-only blocks), and the result is
    # written to the runner pack. QAs inherit every URL / env / endpoint the
    # admin configured and only need to set their personal ApiKey before running.
    $runnerConfig = Join-Path $runnerOut "appsettings.json"
    $webApiConfig    = Join-Path $root "src/AiTestCrew.WebApi/appsettings.json"
    $runnerSrcConfig = Join-Path $root "src/AiTestCrew.Runner/appsettings.json"
    $exampleConfig   = Join-Path $root "src/AiTestCrew.Runner/appsettings.example.json"

    $source = if (Test-Path $webApiConfig)    { $webApiConfig    }
              elseif (Test-Path $runnerSrcConfig) { $runnerSrcConfig }
              else                            { $exampleConfig   }
    Write-Host "Templating appsettings.json from: $source" -ForegroundColor Cyan

    $cfg = Get-Content $source -Raw | ConvertFrom-Json -AsHashtable
    if (-not $cfg.ContainsKey('TestEnvironment')) {
        throw "Source config $source has no TestEnvironment section."
    }
    $te = $cfg.TestEnvironment

    # Top-level fields holding secrets → blank.
    $blankTop = @(
        'LlmApiKey', 'ApiKey', 'AuthToken', 'AuthPassword',
        'LegacyWebUiPassword', 'BraveCloudUiPassword', 'BraveCloudUiTotpSecret',
        'SqliteConnectionString'
    )
    foreach ($k in $blankTop) {
        if ($te.ContainsKey($k)) { $te[$k] = "" }
    }

    # Server-side storage provider → switch to File so the Runner doesn't try to
    # open the admin's local SQLite path.
    if ($te.ContainsKey('StorageProvider')) { $te['StorageProvider'] = 'File' }

    # Top-level dicts that hold connection strings the agent doesn't need.
    if ($te.ContainsKey('DbConnections'))         { $te['DbConnections']         = @{} }
    if ($te.ContainsKey('ServiceBusConnections')) { $te['ServiceBusConnections'] = @{} }

    # aseXML Bravo DB connection string (server-only — agent uploads files, server queries).
    if ($te.ContainsKey('AseXml') -and $te.AseXml -is [hashtable] -and $te.AseXml.ContainsKey('BravoDb')) {
        $te.AseXml.BravoDb.ConnectionString = ""
    }

    # Drop server-only blocks entirely so QAs don't see them.
    foreach ($k in @('Backup', 'Chat', 'ListenUrl', 'CorsOrigins')) {
        if ($te.ContainsKey($k)) { $te.Remove($k) }
    }

    # Pre-fill ServerUrl when given; always blank ApiKey (per-user).
    if ($ServerUrl) { $te['ServerUrl'] = $ServerUrl }
    $te['ApiKey'] = ""

    # Per-env sanitisation — keep URLs / display names / storage state paths,
    # blank passwords + DB / Service Bus connection strings.
    if ($te.ContainsKey('Environments') -and $te.Environments -is [hashtable]) {
        $envKeys = @($te.Environments.Keys)
        foreach ($envKey in $envKeys) {
            $e = $te.Environments[$envKey]
            if ($e -isnot [hashtable]) { continue }
            foreach ($k in @('LegacyWebUiPassword', 'BraveCloudUiPassword', 'BraveCloudUiTotpSecret', 'BravoDbConnectionString')) {
                if ($e.ContainsKey($k)) { $e[$k] = "" }
            }
            if ($e.ContainsKey('DbConnections'))         { $e['DbConnections']         = @{} }
            if ($e.ContainsKey('ServiceBusConnections')) { $e['ServiceBusConnections'] = @{} }
        }
    }

    $cfg | ConvertTo-Json -Depth 100 | Set-Content -Path $runnerConfig -Encoding UTF8
    if ($ServerUrl) {
        Write-Host "Pre-filled ServerUrl = $ServerUrl in appsettings.json" -ForegroundColor Green
    }

    # ── Auto-start script for the Startup folder ──
    $startCmd = Join-Path $runnerOut "start-agent.cmd"
    @'
@echo off
REM Starts the AITestCrew agent so the dashboard can dispatch UI / desktop jobs
REM onto this machine. Drop a shortcut to this file in shell:startup to auto-run
REM at login. The agent runs in the foreground — close the window to stop.
cd /d "%~dp0"
AiTestCrew.Runner.exe --agent --name "%COMPUTERNAME%"
'@ | Set-Content -Path $startCmd -Encoding ASCII

    # ── README ──
    $readme = Join-Path $runnerOut "README.txt"
    @'
AITestCrew QA Agent Pack
========================

This folder turns your machine into a worker that the AITestCrew dashboard can
dispatch Web UI, Desktop UI, and recording jobs onto.

ONE-TIME SETUP
--------------
1. Open appsettings.json and set ONE field:
     - "ApiKey": "atc_..."                          (your personal API key)
   "ServerUrl" is already pre-filled, and your team's environments (URLs, AAD
   accounts, storage state paths, etc.) are baked in. All sensitive values
   (passwords, DB connection strings, Service Bus keys, LLM key) have been
   stripped — the agent doesn't need them. It talks to the server via HTTP
   and authenticates browser sessions interactively.

   OPTIONAL — only if you'll run desktop (WinForms) tests:
     Find your env block under "Environments" and set "WinFormsAppPath" to
     where the desktop app is installed on YOUR machine, e.g.:
       "WinFormsAppPath": "C:\\Bravo\\BravoWin.exe"

2. Install the Playwright browser (only needed if you'll record/run web tests):
     powershell -ExecutionPolicy Bypass -File .\playwright.ps1 install chromium

   The "-ExecutionPolicy Bypass" is needed because Windows blocks scripts that
   came out of a downloaded zip. To fix it once instead of typing it every time,
   run (as your user, not admin):
     Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
     Get-ChildItem -Recurse | Unblock-File
   After that .\playwright.ps1 install chromium works directly. If your machine
   is locked down by Group Policy and the above errors out, fall back to:
     npx playwright install chromium      (needs Node.js)

3. (Optional) Authenticate once against your customer apps so test runs don't
   stop on a login screen:
     AiTestCrew.Runner.exe --auth-setup --target UI_Web_Blazor --environment <envKey>
     AiTestCrew.Runner.exe --auth-setup --target UI_Web_MVC   --environment <envKey>

RUNNING THE AGENT
-----------------
Double-click start-agent.cmd. You should see:

    Registered as <id> (<your-machine-name>)
    Capabilities: UI_Web_Blazor, UI_Web_MVC, UI_Desktop_WinForms
    Polling for jobs — press Ctrl+C to stop.

The dashboard's Agents panel will now show your machine as Online.

AUTO-START AT LOGIN (OPTIONAL)
------------------------------
Press Win+R, type  shell:startup  and press Enter. Drop a shortcut to
start-agent.cmd into that folder. The agent will start the next time you log in.

For desktop tests the machine must stay logged in with an interactive session.
See docs/deployment.md → "Local Agent Setup" on the server for the full reference.

TROUBLESHOOTING
---------------
- "Missing X-Api-Key header"  → ApiKey in appsettings.json is wrong or blank.
- "Failed to register agent"  → ServerUrl unreachable (firewall? VPN? typo?).
- Playwright browser missing  → re-run step 2 above.
- Screenshots not appearing in the dashboard on failure → check this terminal
  for "Screenshot upload failed" — usually a stale API key.

More help: ask the team for the AITestCrew QA Quickstart (docs/qa-quickstart.md).
'@ | Set-Content -Path $readme -Encoding UTF8

    # ── Zip the pack for distribution ──
    $zipPath = Join-Path $OutputDir "AITestCrew-Agent.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Write-Host "Zipping pack..." -ForegroundColor Cyan
    Compress-Archive -Path "$runnerOut/*" -DestinationPath $zipPath -CompressionLevel Optimal

    $fileCount = (Get-ChildItem -Recurse $runnerOut -File | Measure-Object).Count
    $zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "`n=== Runner pack published ($fileCount files, zip $zipSizeMb MB) ===" -ForegroundColor Green
    Write-Host "Folder: $runnerOut"
    Write-Host "Zip:    $zipPath"
    Write-Host ""
    Write-Host "Hand the zip to a QA engineer along with their atc_ API key." -ForegroundColor Cyan
    Write-Host "They unzip, edit appsettings.json (ServerUrl + ApiKey), and run start-agent.cmd."
    return
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
