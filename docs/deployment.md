# AITestCrew — Deployment & Distribution Guide

This guide covers how to deploy the AITestCrew server for shared team access and how to distribute the Runner CLI to individual QA engineers for recording test cases.

---

## Architecture Overview

AITestCrew uses a **server + local agent** model:

- **Server** (WebApi) — hosts the shared dashboard, stores all test data in SQLite, runs API and aseXML tests centrally. Deployed once on a team server.
- **Local Runner** (CLI) — runs on each QA engineer's machine for recording test cases (needs browser/desktop access). Syncs recorded tests to the server via HTTP.

```
┌─────────────────────────────────────────────────────────────┐
│  Team Server (Windows Server / Docker)                      │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐ │
│  │  AiTestCrew.WebApi                                    │ │
│  │  ├── REST API          (port 5050)                    │ │
│  │  ├── React Dashboard   (served from /wwwroot)         │ │
│  │  ├── SQLite Database   (aitestcrew.db)                │ │
│  │  └── Test Execution    (API, aseXML — runs centrally) │ │
│  └───────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
        ▲ HTTP              ▲ HTTP              ▲ HTTP
        │                   │                   │
┌───────┴──────┐   ┌───────┴──────┐   ┌───────┴──────┐
│ QA Engineer 1│   │ QA Engineer 2│   │ QA Engineer 3│
│ Browser (UI) │   │ Browser (UI) │   │ Browser (UI) │
│ Runner (CLI) │   │ Runner (CLI) │   │ Runner (CLI) │
│ - --record   │   │ - --record   │   │ - --record   │
│ - --auth-setup│  │ - --auth-setup│  │ - --auth-setup│
└──────────────┘   └──────────────┘   └──────────────┘
```

---

## Test Execution Model — What Runs Where

Not all test types can execute on the server. Understanding this is critical for planning your deployment.

### Execution matrix

| Test Type | Triggered from Dashboard (Docker / Server Core) | Triggered from Runner CLI | Requirements |
|---|---|---|---|
| **API** (REST/GraphQL) | Server executes | Local executes | HTTP access to the target API only |
| **aseXML** (generate/deliver) | Server executes | Local executes | Network access to Bravo SFTP/FTP endpoints |
| **Web UI — Playwright** (Blazor/MVC) | Server enqueues → **local agent** executes | Local executes (visible or headless) | Chromium + full Windows (Media Foundation) |
| **Desktop UI — FlaUI** (WinForms) | Server enqueues → **local agent** executes | Local executes | Interactive Windows desktop + target app installed |
| **Post-delivery UI verifications** | Same as target — API verifications execute on server; Web/Desktop are queued | Depends on target | Same as the verification's target type |

> **Phase 4 agent mode:** the dashboard triggers web + desktop runs exactly like API runs; under the covers the server enqueues the job and a local Runner (started with `--agent`) claims and executes it. See **[Local Agent Setup](#local-agent-setup-phase-4)** below.

> **Web UI tests + Windows Server Core containers:** Chromium requires the Windows Media Foundation subsystem (`mf.dll`) which Windows Server Core omits. Enabling it via `DISM` fails inside containers (no package source), and copying the DLLs manually fails signature verification. For a server deployment using Windows Server Core (the default for `.NET 8` Windows containers), web UI tests must be run from a local Runner CLI on a machine with full Windows. If you need web UI tests to run centrally, host the WebApi on a full Windows Server VM (not Server Core) using `publish.ps1` + install as a Windows Service, not Docker.

### Why desktop tests can't run on the server

FlaUI uses Windows UI Automation, which requires:
1. An **interactive desktop session** — services run in Session 0 (non-interactive) and have no access to window handles
2. The **target WinForms application** installed on the machine
3. A **visible screen** — UI Automation interacts with real rendered windows

A Docker container, Windows Service, or headless server has no desktop session. Triggering a desktop test from the web dashboard will fail.

### How desktop tests execute in the multi-user setup

The test case definition is stored centrally on the server (SQLite). Execution happens on a machine that has the desktop app.

```
┌─────────────────────┐         ┌──────────────────────────┐
│  Team Server        │         │  Desktop Test Machine    │
│  (WebApi + SQLite)  │◀──HTTP──│  (QA's PC or dedicated VM)│
│                     │         │                           │
│  Stores test defs   │         │  Runner CLI               │
│  Stores results     │         │  ├── Reads test def from  │
│  Runs API tests     │         │  │   server via HTTP      │
│  Runs web UI tests  │         │  ├── Launches target app  │
│  Shows dashboard    │         │  ├── FlaUI executes steps │
│                     │         │  └── Saves results back   │
└─────────────────────┘         └──────────────────────────┘
```

**Option A — QA engineer runs from their machine (simplest):**

```powershell
# Runner pulls test definition from server, executes locally, pushes results back
.\AiTestCrew.Runner.exe --reuse desktop-tests --module desktop --objective "Basic Addition"
```

**Option B — Dedicated test machine with auto-login:**

Set up a Windows VM or physical machine that:
- Has the target WinForms application installed
- Is configured to auto-login (always has an interactive desktop)
- Has the Runner CLI installed with `ServerUrl` pointing to the server

Schedule runs via Windows Task Scheduler:

```powershell
# scheduled-desktop-tests.ps1 — runs nightly via Task Scheduler
C:\Tools\AITestCrew\AiTestCrew.Runner.exe --reuse desktop-tests --module desktop
```

> **Important:** The Task Scheduler task must use **"Run only when user is logged on"** — not "Run whether user is logged on or not" (which runs in a non-interactive session where UI Automation cannot access windows).

**Option C — Persistent RDP session:**

Keep an RDP session open to a server/VM. Disconnect without locking so the desktop stays interactive:

```powershell
# On the remote machine — disconnect RDP without locking the session
tscon %sessionname% /dest:console
```

### Local Agent Setup (Phase 4)

Turns each QA engineer's Runner CLI into a long-running worker that claims jobs the server can't execute in-process (web + desktop UI). Setup is a one-time flag.

**On the QA machine:**

1. Clone and build the Runner (same binary as the CLI — no separate install).
2. Set `ServerUrl` + `ApiKey` in `src/AiTestCrew.Runner/appsettings.json` so the Runner talks to the shared server.
3. Start the agent:

```powershell
dotnet run --project src/AiTestCrew.Runner -- --agent --name "Alice-PC"
```

Expected output:

```
Registered as 72ad9b3c1a5e (Alice-PC)
Capabilities: UI_Web_Blazor, UI_Web_MVC, UI_Desktop_WinForms
Server: https://aitestcrew.internal
Polling for jobs — press Ctrl+C to stop.
```

**Defaults:**
- `--name` defaults to `$env:COMPUTERNAME` (or the `AgentName` config value).
- `--capabilities` defaults to all three UI targets (`UI_Web_Blazor,UI_Web_MVC,UI_Desktop_WinForms`).
- Poll interval 10s, heartbeat interval 30s, server marks agent Offline after 2 min of silence. All configurable via `AgentPollIntervalSeconds`, `AgentHeartbeatIntervalSeconds`, `AgentHeartbeatTimeoutSeconds`.

**Dashboard view:**
- The Modules page lists connected agents with status (Online / Busy / Offline), capabilities, and any job they're currently running.
- When a dashboard user triggers a web/desktop run, the banner shows *"Queued — waiting for agent with UI_Web_Blazor"* until an agent claims it, then flips to *"Running on Alice-PC"*.

**Auto-start on login (optional):**

Create `C:\Tools\aitestcrew-agent\start-agent.cmd`:

```cmd
@echo off
cd /d "C:\MyCode\github\AITestCrew"
dotnet run --project src\AiTestCrew.Runner -- --agent --name "%COMPUTERNAME%"
```

Add a shortcut to the Windows Startup folder (`shell:startup`) pointing at the cmd file. For desktop tests, the machine must remain logged in with an interactive session (see [Option B](#why-desktop-tests-cant-run-on-the-server) above for the auto-login pattern).

**Stopping the agent:**
Press Ctrl+C — the Runner finishes the current job, deregisters itself from the server, and exits. An ungracefully killed agent is detected by the heartbeat timeout.

**Auth state:**
Playwright storage-state files live on the agent's machine (e.g. `bravecloud-auth-state.json` next to the Runner binary). Run `dotnet run --project src/AiTestCrew.Runner -- --auth-setup` on the QA machine once so the first Blazor job doesn't fall through to an interactive login. For Legacy MVC forms auth use `--auth-setup --target UI_Web_MVC`. The server's `docker-auth-state` mount is only used for runs that execute server-side (API / aseXML) — not for agent-claimed jobs.

**Screenshots on failure:**
When a Web UI or Desktop UI step fails, the agent captures a screenshot locally, then POSTs a copy to `POST /api/screenshots` on the server. The dashboard's execution-detail page renders it via `/screenshots/{filename}` from the server's `PlaywrightScreenshotDir`. Nothing to configure — it happens automatically whenever `ServerUrl` is set in the Runner config. If screenshots aren't rendering, check the agent terminal for `Screenshot upload failed: ...` warnings (often a stale API key or the agent hitting a Docker server restart mid-run).

**Legacy MVC concurrency:**
The Legacy Web UI agent (`UI_Web_MVC`) serializes objective execution inside its process via a static semaphore — only one MVC test case runs at a time even when `MaxParallelAgents > 1`. This sidesteps the single-session / shared-ASP.NET-session issues that otherwise cause 15-second Playwright timeouts when many objectives in a test set run concurrently. Blazor (`UI_Web_Blazor`), API, aseXML, and Desktop agents continue to parallelize up to `MaxParallelAgents`.

---

### Recommended team workflow

| Activity | Where | Who |
|---|---|---|
| **Record** web UI test cases | QA's local machine (Runner CLI) | Any QA engineer |
| **Record** desktop test cases | QA's local machine (Runner CLI) | Any QA engineer |
| **Run** API / aseXML tests | Server (dashboard or Runner CLI) | Anyone |
| **Run** web UI tests | Dashboard → local agent claims the job | Anyone with an agent online |
| **Run** desktop tests | Dashboard → local agent on a machine with the app | Anyone with an agent online |
| **View results** | Server dashboard (browser) | Anyone |
| **Manage** modules/test sets/users | Server dashboard (browser) | Anyone |

---

## Server Deployment

Three options in order of recommendation:

### Option 1: Self-Contained Publish (Simplest)

Produces a standalone folder with `AiTestCrew.WebApi.exe` — no .NET SDK needed on the target machine.

**On the build machine (has .NET 8 SDK + Node.js):**

```powershell
# Clone and publish
git clone <repo-url> AITestCrew
cd AITestCrew
.\publish.ps1 -OutputDir C:\deploy\aitestcrew
```

**On the server:**

1. Copy the `C:\deploy\aitestcrew\` folder to the server.

2. Edit `appsettings.json`:
   ```json
   {
     "TestEnvironment": {
       "LlmProvider": "Anthropic",
       "LlmApiKey": "<your-claude-api-key>",
       "LlmModel": "claude-sonnet-4-6",

       "ListenUrl": "http://+:5050",
       "CorsOrigins": [],

       "StorageProvider": "Sqlite",
       "SqliteConnectionString": "Data Source=C:/data/aitestcrew.db"
     }
   }
   ```

3. Run:
   ```powershell
   cd C:\deploy\aitestcrew
   .\AiTestCrew.WebApi.exe
   ```

4. Open `http://<server-ip>:5050` in a browser.

**Install as a Windows Service (recommended for production):**

```powershell
sc.exe create AITestCrew binPath= "C:\deploy\aitestcrew\AiTestCrew.WebApi.exe"
sc.exe config AITestCrew start= auto
sc.exe start AITestCrew
```

To update: stop the service, replace the folder, start again.

```powershell
sc.exe stop AITestCrew
# Copy new files...
sc.exe start AITestCrew
```

---

### Option 2: Docker Compose (Windows Containers)

**Prerequisites:**
- Docker Desktop for Windows, switched to **Windows containers** mode (right-click Docker tray icon → "Switch to Windows containers")
- Windows 10/11 Pro, or Windows Server 2022+

**Steps:**

```powershell
# 1. Pre-build the React UI on the host.
#    (The Dockerfile skips the UI build because Windows Server Core containers
#    can't load Vite's native rolldown bindings.)
cd ui
npm ci
npx vite build
cd ..

# 2. Copy the environment template and configure secrets
cp .env.example .env
# Edit .env — set at minimum:
#   AITESTCREW_TestEnvironment__LlmApiKey=sk-ant-...

# 3. Create the runtime config directory (mounted into the container).
#    This holds the full appsettings.json — env vars alone can't express nested
#    config like ApiStacks or Environments. Copy your Runner's appsettings.json:
mkdir docker-config
cp src/AiTestCrew.Runner/appsettings.json docker-config/appsettings.json
# Review docker-config/appsettings.json — set ApiStacks, auth credentials,
# per-customer Environments (with LegacyWebUiUrl, BraveCloudUiUrl, BravoDbConnectionString,
# ApiStackBaseUrls per env), and DefaultEnvironment.

# 4. Create the auth state directory (for Playwright SSO sessions)
mkdir docker-auth-state
# Populate it from your local Runner (after running --auth-setup):
.\refresh-auth-state.ps1

# 5. Build and start (first build ~5-10 minutes for Windows base images)
docker compose up -d --build

# 6. Verify
docker compose logs -f
# Should see: "Now listening on: http://[::]:5050"

# 7. Open http://localhost:5050
```

**Data persistence:** Three directories are mounted into the container:

| Host path | Container path | Purpose | Gitignored |
|---|---|---|---|
| `aitestcrew-data` (Docker volume) | `C:/data/` | SQLite database | — (Docker-managed) |
| `./docker-config/` | `C:/config/` | `appsettings.json` — full config with ApiStacks, Environments, secrets | Yes |
| `./docker-auth-state/` | `C:/auth-state/` | Playwright SSO storage state JSON files | Yes |

The SQLite database survives container restarts and rebuilds. Config and auth state can be edited on the host and the container picks up changes without rebuild (config is read at startup, auth state is read per-test-run).

**Refreshing auth state:** Playwright's SSO sessions expire after `StorageStateMaxAgeHours` (default 8h). To refresh, repeat per environment — each one writes to its own storage-state filename:

```powershell
# 1. Re-run --auth-setup locally to capture a fresh session per customer env
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor --environment sumo-retail
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC    --environment sumo-retail
# ...repeat for each configured customer env (ams-metering, tasn-networks, etc.)

# 2. Sync the captured state into the Docker volume mount
.\refresh-auth-state.ps1
# No container restart needed
```

**Copying an existing SQLite DB into the container:**

```powershell
docker compose stop

# Clean volume and copy DB in one shot (Windows containers can't mount single files)
docker run --rm -v aitestcrew_aitestcrew-data:C:/data `
                -v ${PWD}/data:C:/src `
                mcr.microsoft.com/windows/servercore:ltsc2022 `
                powershell -Command "Remove-Item C:/data/aitestcrew.db* -EA SilentlyContinue; Copy-Item C:/src/aitestcrew.db C:/data/aitestcrew.db"

docker compose start
```

**Updating:**
```powershell
git pull
docker compose up -d --build
# Volume is preserved — no data loss
```

**Stopping:**
```powershell
docker compose down          # Stops container, keeps volume
docker compose down -v       # Stops AND deletes data volume (destructive!)
```

**Environment variable reference:**

| Variable | Default | Purpose |
|---|---|---|
| `AITESTCREW_TestEnvironment__LlmProvider` | `Anthropic` | LLM provider |
| `AITESTCREW_TestEnvironment__LlmApiKey` | (required) | Claude API key |
| `AITESTCREW_TestEnvironment__LlmModel` | `claude-sonnet-4-6` | Model to use |
| `AITESTCREW_TestEnvironment__ListenUrl` | `http://+:5050` | Bind URL |
| `AITESTCREW_TestEnvironment__StorageProvider` | `Sqlite` | Storage backend |
| `AITESTCREW_TestEnvironment__SqliteConnectionString` | `Data Source=C:/data/aitestcrew.db` | DB path |
| `AITESTCREW_TestEnvironment__CorsOrigins__0` | (none) | First allowed CORS origin |
| `AITESTCREW_TestEnvironment__AseXml__BravoDb__ConnectionString` | (none) | Bravo DB for aseXML delivery |

---

### Option 3: IIS Hosting

For organisations that prefer IIS over standalone processes.

1. Publish the app: `.\publish.ps1 -OutputDir C:\inetpub\aitestcrew`
2. Install the [.NET 8 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0) on the server.
3. Create an IIS site pointing to `C:\inetpub\aitestcrew`.
4. Set the Application Pool to "No Managed Code" (the app is self-hosted via Kestrel behind IIS).
5. Configure `appsettings.json` as above.

---

## First-Time Server Setup

After the server is running (any deployment method):

### 1. Create the first user

The first user can be created without authentication (bootstrap mode):

```bash
curl -X POST http://<server>:5050/api/users \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"Admin\"}"
```

Response (save the `apiKey` — it's only shown once):
```json
{
  "id": "a1b2c3d4e5f6",
  "name": "Admin",
  "apiKey": "atc_8f3a9b7c2d1e4f6a8b3c9d7e2f1a4b6c8d3e9f7a2b1c4d",
  "createdAt": "2026-04-17T12:00:00Z",
  "isActive": true
}
```

### 2. Create additional users

Subsequent users require the admin's API key:

```bash
curl -X POST http://<server>:5050/api/users \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: atc_8f3a9b7c..." \
  -d "{\"name\": \"Bob\"}"
```

### 3. Migrate existing test data (optional)

If you have existing JSON-based test data from running AITestCrew locally:

```powershell
# On the machine with the existing data, configure SQLite connection
# in src/AiTestCrew.Runner/appsettings.json:
#   "SqliteConnectionString": "Data Source=C:/data/aitestcrew.db"

dotnet run --project src/AiTestCrew.Runner -- --migrate-to-sqlite
```

Then copy the `.db` file to the server's data directory.

---

## Distributing the Runner CLI to QA Engineers

Each QA engineer needs the Runner CLI on their local Windows machine to record test cases. Three distribution methods:

### Method 1: Self-Contained Single Folder (Recommended)

Build a standalone Runner package that requires no .NET SDK:

```powershell
# On the build machine
dotnet publish src/AiTestCrew.Runner/AiTestCrew.Runner.csproj `
  -c Release `
  --self-contained `
  -r win-x64 `
  -o ./runner-dist
```

This produces a `runner-dist/` folder (~80-100 MB) containing `AiTestCrew.Runner.exe` and all dependencies. Distribute as a zip.

**QA engineer setup:**

1. Unzip to a local folder (e.g., `C:\Tools\AITestCrew\`)

2. Create `appsettings.json` in the same folder:
   ```json
   {
     "TestEnvironment": {
       "ServerUrl": "http://<team-server>:5050",
       "ApiKey": "atc_<your-personal-key>",

       "PlaywrightBrowser": "chromium",
       "PlaywrightHeadless": false,

       "DefaultEnvironment": "sumo-retail",
       "Environments": {
         "sumo-retail": {
           "DisplayName": "Sumo Retail",
           "LegacyWebUiUrl":              "https://legacy-sumo.company.com",
           "BraveCloudUiUrl":             "https://sumo.company.com",
           "BraveCloudUiUsername":        "user@company.com",
           "BraveCloudUiPassword":        "<password>",
           "BraveCloudUiTotpSecret":      "<base32-totp-secret>",
           "BraveCloudUiStorageStatePath": "bravecloud-auth-state.sumo.json",
           "LegacyWebUiStorageStatePath":  "legacy-auth-state.sumo.json"
         },
         "ams-metering": {
           "DisplayName": "AMS Metering",
           "LegacyWebUiUrl":              "https://legacy-ams.company.com",
           "BraveCloudUiUrl":             "https://ams.company.com",
           "BraveCloudUiStorageStatePath": "bravecloud-auth-state.ams.json",
           "LegacyWebUiStorageStatePath":  "legacy-auth-state.ams.json"
         }
       }
     }
   }
   ```

   Single-environment deployments can keep URLs at the top level and omit `Environments` entirely — the resolver falls back to top-level fields. Add the `Environments` section only when you need to target multiple customer deployments from the same Runner.

3. Install Playwright browsers (one-time):
   ```powershell
   cd C:\Tools\AITestCrew
   .\playwright.ps1 install chromium
   # Or if using the .NET tool:
   npx playwright install chromium
   ```

4. Test the connection:
   ```powershell
   .\AiTestCrew.Runner.exe --list-modules
   ```

### Method 2: Shared Network Drive

Place the published Runner on a network share. Each engineer runs it directly:

```powershell
\\server\tools\AITestCrew\AiTestCrew.Runner.exe --list-modules
```

Each engineer needs their own `appsettings.json` — either in the same directory or configured via environment variables:

```powershell
$env:AITESTCREW_TestEnvironment__ServerUrl = "http://team-server:5050"
$env:AITESTCREW_TestEnvironment__ApiKey = "atc_..."
\\server\tools\AITestCrew\AiTestCrew.Runner.exe --record --module sec --testset users --case-name "Search" --target UI_Web_Blazor
```

### Method 3: Source Clone (for developers)

Engineers who have the .NET SDK can clone the repo and run directly:

```powershell
git clone <repo-url> AITestCrew
cd AITestCrew

# Configure
cp src/AiTestCrew.Runner/appsettings.example.json src/AiTestCrew.Runner/appsettings.json
# Edit appsettings.json — set ServerUrl + ApiKey

# Run
dotnet run --project src/AiTestCrew.Runner -- --list-modules
```

---

## QA Engineer Quick Start

After receiving the Runner CLI and an API key from the admin:

### 1. Verify connectivity

```powershell
.\AiTestCrew.Runner.exe --list-modules
# Should show modules from the shared server
```

### 2. Save auth state (one-time per target **and per environment**)

```powershell
# List the customer environments configured on your Runner
.\AiTestCrew.Runner.exe --list-environments

# For Blazor apps (Azure AD SSO + 2FA) — against a specific customer env
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_Blazor --environment sumo-retail
# Complete the SSO + 2FA flow in the browser that opens
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_Blazor --environment ams-metering
# Again, with the matching customer credentials

# For Legacy MVC apps (forms auth)
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_MVC --environment sumo-retail
# Log in with your credentials
```

Each environment writes to its own storage-state file (e.g. `bravecloud-auth-state.sumo.json` vs `bravecloud-auth-state.ams.json`) so they don't clobber each other. Omit `--environment` to fall back to `DefaultEnvironment`.

Auth state is saved locally and reused for 8 hours (configurable).

### 3. Record a test case

```powershell
# Web UI recording (browser opens, interact with the app, click Save & Stop)
.\AiTestCrew.Runner.exe --record `
  --module security --testset user-management `
  --case-name "Search users by name" `
  --target UI_Web_Blazor `
  --environment sumo-retail

# Desktop recording (app launches, interact, press S to save)
.\AiTestCrew.Runner.exe --record `
  --module desktop --testset calc `
  --case-name "Basic Addition" `
  --target UI_Desktop_WinForms `
  --environment sumo-retail
```

`--environment` is optional — defaults to `DefaultEnvironment`. Specify it when the same module/test set will be recorded against multiple customer deployments.

The recorded test case syncs to the shared server automatically. It appears in the web dashboard immediately.

### 4. Run saved tests

```powershell
# Run API / web UI tests — these also work from the dashboard
.\AiTestCrew.Runner.exe --reuse user-management --module security

# Run desktop tests — MUST run locally (not from dashboard)
.\AiTestCrew.Runner.exe --reuse calc --module desktop --objective "Basic Addition"
```

> **Desktop tests:** Because FlaUI needs an interactive desktop and the target app, desktop test execution must always happen from the Runner CLI on a machine that has the app installed. API and web UI tests can be triggered from the dashboard instead.

### 5. View results in the dashboard

Open `http://<team-server>:5050` in any browser, log in with your API key. Results from both server-triggered and local Runner executions appear in the same dashboard.

---

## Configuration Reference

### Server (`appsettings.json → TestEnvironment`)

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `LlmProvider` | string | `"OpenAI"` | `"Anthropic"` or `"OpenAI"` |
| `LlmApiKey` | string | `""` | API key for the LLM provider |
| `LlmModel` | string | `"gpt-4o"` | Model identifier |
| `ListenUrl` | string | `""` | Bind URL(s). Empty = `http://localhost:5050`. Semicolons for multiple. |
| `CorsOrigins` | string[] | `[]` | Allowed CORS origins. Empty = Vite dev defaults. `["*"]` = any. |
| `StorageProvider` | string | `"File"` | `"File"` (JSON on disk) or `"Sqlite"` |
| `SqliteConnectionString` | string | `""` | SQLite path. Required when `StorageProvider = "Sqlite"`. |
| `MaxParallelAgents` | int | `4` | Maximum concurrent test agent executions |
| `MaxExecutionRunsPerTestSet` | int | `10` | Retention: old runs auto-pruned beyond this count |
| `PlaywrightBrowser` | string | `"chromium"` | Browser for web UI testing |
| `PlaywrightHeadless` | bool | `true` | Run browser headless (server) or visible (recording) |
| `PlaywrightScreenshotDir` | string | `null` | Directory for failure screenshots |

### Runner (`appsettings.json → TestEnvironment`)

All server settings above, plus:

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `ServerUrl` | string | `""` | WebApi URL for remote mode. Empty = use local storage. |
| `ApiKey` | string | `""` | API key for authenticating with the remote server |
| `DefaultEnvironment` | string | `null` | Customer env used when `--environment` is omitted. Falls back to the first key in `Environments`, then to a synthesised `"default"` env for legacy single-env configs. |
| `Environments` | dict | `{}` | Map of customer key → `EnvironmentConfig` block. See [Multi-Environment Setup](#multi-environment-setup) below. |
| `LegacyWebUiUrl` | string | `""` | Base URL of the Legacy MVC application (top-level fallback; prefer `Environments.<key>.LegacyWebUiUrl` for multi-customer deployments) |
| `LegacyWebUiUsername` | string | `""` | Forms auth username (top-level fallback) |
| `LegacyWebUiPassword` | string | `""` | Forms auth password (top-level fallback) |
| `LegacyWebUiStorageStatePath` | string | `null` | Path to cached auth state, relative to exe (top-level fallback) |
| `BraveCloudUiUrl` | string | `""` | Base URL of the Blazor application (top-level fallback) |
| `BraveCloudUiUsername` | string | `""` | Azure AD email (top-level fallback) |
| `BraveCloudUiPassword` | string | `""` | Azure AD password (top-level fallback) |
| `BraveCloudUiTotpSecret` | string | `null` | Base32 TOTP secret for automated 2FA (top-level fallback) |
| `BraveCloudUiStorageStatePath` | string | `null` | Path to cached SSO auth state (top-level fallback) |
| `WinFormsAppPath` | string | `""` | Full path to the desktop app exe (top-level fallback) |

### Multi-Environment Setup

Each customer deployment has its own URLs, credentials, auth-state files, and (for aseXML) Bravo DB connection string. Configure them under `TestEnvironment.Environments.<key>`:

| Setting (inside an env block) | Purpose |
|---|---|
| `DisplayName` | Human-readable label shown in `--list-environments` and the UI dropdown |
| `LegacyWebUiUrl` / `LegacyWebUiUsername` / `LegacyWebUiPassword` / `LegacyWebUiStorageStatePath` | Per-env legacy MVC overrides |
| `BraveCloudUiUrl` / `BraveCloudUiUsername` / `BraveCloudUiPassword` / `BraveCloudUiTotpSecret` / `BraveCloudUiStorageStatePath` | Per-env Blazor + Azure SSO overrides |
| `WinFormsAppPath` / `WinFormsAppArgs` | Per-env desktop app path (different customer builds install to different paths / accept different launch args) |
| `BravoDbConnectionString` | Per-env Bravo application DB for aseXML endpoint resolution |
| `ApiStackBaseUrls` | Map of ApiStacks key → BaseUrl override. The shared `ApiStacks.<stack>.Modules.*` definitions are reused; only the `BaseUrl` differs per customer. |

Any field omitted from an env block falls back to the top-level field of the same name. This lets you migrate gradually: add one env at a time while leaving top-level fields in place as sane defaults.

Select the active env at run time:

- CLI: `--environment <key>` on any run / auth / recording command
- UI: environment dropdown in the **Run Objective** dialog
- Persisted: the chosen env is saved on each test set; omitting `--environment` uses the persisted value

See the **Multi-Environment Support** section of `docs/functional.md` for the per-objective `AllowedEnvironments` + `EnvironmentParameters` authoring model.

### Environment Variable Overrides

Any setting can be overridden via environment variables with the `AITESTCREW_` prefix. Nested properties use double underscores:

```
AITESTCREW_TestEnvironment__LlmApiKey=sk-ant-...
AITESTCREW_TestEnvironment__StorageProvider=Sqlite
AITESTCREW_TestEnvironment__AseXml__BravoDb__ConnectionString=Server=...
```

---

## Updating the Server

### Self-contained deployment

```powershell
sc.exe stop AITestCrew          # Stop the service
# Copy new published files to C:\deploy\aitestcrew (overwrite)
sc.exe start AITestCrew         # Restart
```

The SQLite database is separate from the application files — no data loss on update.

### Docker deployment

```powershell
git pull                         # Get latest code
docker compose up -d --build     # Rebuild and restart
```

The Docker volume preserves the database across rebuilds.

### Distributing Runner updates

Rebuild the Runner package and redistribute the zip/folder. The Runner is stateless (all data lives on the server) — replacing the files is sufficient.

---

## Troubleshooting

| Problem | Solution |
|---|---|
| `unable to open database file` | The parent directory for the SQLite file doesn't exist. The app creates it automatically — check the path is valid. |
| `Missing X-Api-Key header` | Auth is active (SQLite mode). Pass your API key via `X-Api-Key` header or log in via the web UI. |
| Docker build fails | Ensure Docker Desktop is in Windows container mode. Linux containers cannot build `net8.0-windows` projects. |
| Runner can't connect to server | Check `ServerUrl` in appsettings.json. Verify the server is reachable: `curl http://<server>:5050/api/health` |
| Playwright browser not found | Run `npx playwright install chromium` in the Runner directory. |
| Auth state expired | Re-run `--auth-setup --target <UI_Web_Blazor\|UI_Web_MVC> --environment <envKey>`. Default expiry is 8 hours. Repeat per customer env — each writes to its own storage-state filename. |
| Web UI test fails immediately with unauth redirect after adding `Environments` | The env block specifies a different `BraveCloudUiStorageStatePath` / `LegacyWebUiStorageStatePath` than the one your existing auth state was saved to. Either point the env's path at the existing filename, omit the path from the env block (falls back to top-level), or re-run `--auth-setup --environment <key>` to save state to the env-specific filename. |
| `--auth-setup` wrote to a different filename than agents expected | Verify the "Storage state →" line the command prints at launch. It shows the exact path being written. If it differs from what the agents read, reconcile by editing the env's `*StorageStatePath` or omitting it so it falls back to the top-level field. |
| Concurrent run conflict | Same test set is already running. Wait for it to finish or use a different test set. Different test sets can run in parallel. |
| Port 5050 already in use | Change `ListenUrl` in appsettings.json or set `AITESTCREW_TestEnvironment__ListenUrl=http://+:8080` |
| `ApiStacks must be configured` (in Docker) | The container's `appsettings.json` is minimal. Create `docker-config/appsettings.json` (copy from `src/AiTestCrew.Runner/`) and rebuild/restart. The compose file mounts this into `C:/config/`. |
| Docker build: `Cannot find module '@rolldown/binding-win32-x64-msvc'` | Vite's native bindings don't load in Server Core. Build the UI on the host first: `cd ui && npx vite build`. The Dockerfile copies `ui/dist/` in. |
| Web UI test in Docker: `Target page, context or browser has been closed` | Server Core lacks Media Foundation (Chromium dependency). Cannot be fixed reliably — run web UI tests from the local Runner CLI instead. See "Test Execution Model" above. |
| `Missing X-Api-Key header` when creating first user | Bootstrap mode allows POST `/api/users` without auth only when zero users exist. If you already have users, get an existing admin's API key and send it as `X-Api-Key`. |
| Copied DB shows as empty after container restart | A stale WAL file was applied on top. Use the helper container sequence in "Copying an existing SQLite DB into the container" above — it removes the `.db-wal` / `.db-shm` files before copying. |
| Agent shows Offline on the dashboard while the Runner is still running | Heartbeat stopped reaching the server. Check the agent terminal for `Heartbeat failed: ...` warnings (stale API key, dropped network, Docker restart). The Runner keeps retrying; it goes back Online on the next successful heartbeat. |
| Agent-claimed Web/Desktop run shows "View Screenshot" but the image fails to load | The agent couldn't upload to `/api/screenshots` after capture. Check the agent terminal for `Screenshot upload failed: ...`. Usually an expired API key, the Docker container restarted between capture and upload, or `PlaywrightScreenshotDir` isn't writable inside the container. |
| Legacy MVC test set: many objectives fail at `Timeout 15000ms exceeded` when "Re-run Tests" on a whole set | The legacy MVC backend can't handle concurrent authenticated sessions. The agent already serializes `UI_Web_MVC` objectives inside its process, but if you run multiple test sets in parallel (module-level run) each still gets one browser per test set. Either run test sets one at a time or set `MaxParallelAgents: 1` on the **agent's** `appsettings.json`. |
| Dashboard heading shows the parent test-set objective instead of the filtered single test case | Fixed in the orchestrator — single-objective runs now display the objective's own name. Old persisted runs keep the previous heading; only new runs will look right. |
