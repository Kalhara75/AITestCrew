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
| **Web UI — Playwright** (Blazor/MVC) | **Cannot execute on Server Core containers** — see note below | Local executes (visible or headless) | Chromium + full Windows (Media Foundation) |
| **Desktop UI — FlaUI** (WinForms) | **Cannot execute on server** | **Must execute locally** | Interactive Windows desktop + target app installed |
| **Post-delivery UI verifications** | Depends on target (see above) | Depends on target | Same as the verification's target type |

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

### Recommended team workflow

| Activity | Where | Who |
|---|---|---|
| **Record** web UI test cases | QA's local machine (Runner CLI) | Any QA engineer |
| **Record** desktop test cases | QA's local machine (Runner CLI) | Any QA engineer |
| **Run** API / aseXML tests | Server (dashboard or Runner CLI) | Anyone |
| **Run** web UI tests | Server (dashboard — headless) | Anyone |
| **Run** desktop tests | Local machine or dedicated VM (Runner CLI) | QA engineer or scheduled task |
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
#    config like ApiStacks. Copy your Runner's appsettings.json as a starting point:
mkdir docker-config
cp src/AiTestCrew.Runner/appsettings.json docker-config/appsettings.json
# Review docker-config/appsettings.json — set ApiStacks, auth credentials,
# agent URLs (LegacyWebUiUrl, BraveCloudUiUrl, etc.)

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
| `./docker-config/` | `C:/config/` | `appsettings.json` — full config with ApiStacks, secrets | Yes |
| `./docker-auth-state/` | `C:/auth-state/` | Playwright SSO storage state JSON files | Yes |

The SQLite database survives container restarts and rebuilds. Config and auth state can be edited on the host and the container picks up changes without rebuild (config is read at startup, auth state is read per-test-run).

**Refreshing auth state:** Playwright's SSO sessions expire after `StorageStateMaxAgeHours` (default 8h). To refresh:

```powershell
# 1. Re-run --auth-setup locally to capture a fresh session
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC

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

       "LegacyWebUiUrl": "https://your-mvc-app.com",
       "BraveCloudUiUrl": "https://your-blazor-app.com",
       "BraveCloudUiUsername": "user@company.com",
       "BraveCloudUiPassword": "<password>",
       "BraveCloudUiTotpSecret": "<base32-totp-secret>"
     }
   }
   ```

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

### 2. Save auth state (one-time per target)

```powershell
# For Blazor apps (Azure AD SSO + 2FA)
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_Blazor
# Complete the SSO + 2FA flow in the browser that opens

# For Legacy MVC apps (forms auth)
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_MVC
# Log in with your credentials
```

Auth state is saved locally and reused for 8 hours (configurable).

### 3. Record a test case

```powershell
# Web UI recording (browser opens, interact with the app, click Save & Stop)
.\AiTestCrew.Runner.exe --record `
  --module security --testset user-management `
  --case-name "Search users by name" `
  --target UI_Web_Blazor

# Desktop recording (app launches, interact, press S to save)
.\AiTestCrew.Runner.exe --record `
  --module desktop --testset calc `
  --case-name "Basic Addition" `
  --target UI_Desktop_WinForms
```

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
| `LegacyWebUiUrl` | string | `""` | Base URL of the Legacy MVC application |
| `LegacyWebUiUsername` | string | `""` | Forms auth username |
| `LegacyWebUiPassword` | string | `""` | Forms auth password |
| `LegacyWebUiStorageStatePath` | string | `null` | Path to cached auth state (relative to exe) |
| `BraveCloudUiUrl` | string | `""` | Base URL of the Blazor application |
| `BraveCloudUiUsername` | string | `""` | Azure AD email |
| `BraveCloudUiPassword` | string | `""` | Azure AD password |
| `BraveCloudUiTotpSecret` | string | `null` | Base32 TOTP secret for automated 2FA |
| `BraveCloudUiStorageStatePath` | string | `null` | Path to cached SSO auth state |
| `WinFormsAppPath` | string | `""` | Full path to the desktop app exe |

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
| Auth state expired | Re-run `--auth-setup --target <UI_Web_Blazor|UI_Web_MVC>`. Default expiry is 8 hours. |
| Concurrent run conflict | Same test set is already running. Wait for it to finish or use a different test set. Different test sets can run in parallel. |
| Port 5050 already in use | Change `ListenUrl` in appsettings.json or set `AITESTCREW_TestEnvironment__ListenUrl=http://+:8080` |
| `ApiStacks must be configured` (in Docker) | The container's `appsettings.json` is minimal. Create `docker-config/appsettings.json` (copy from `src/AiTestCrew.Runner/`) and rebuild/restart. The compose file mounts this into `C:/config/`. |
| Docker build: `Cannot find module '@rolldown/binding-win32-x64-msvc'` | Vite's native bindings don't load in Server Core. Build the UI on the host first: `cd ui && npx vite build`. The Dockerfile copies `ui/dist/` in. |
| Web UI test in Docker: `Target page, context or browser has been closed` | Server Core lacks Media Foundation (Chromium dependency). Cannot be fixed reliably — run web UI tests from the local Runner CLI instead. See "Test Execution Model" above. |
| `Missing X-Api-Key header` when creating first user | Bootstrap mode allows POST `/api/users` without auth only when zero users exist. If you already have users, get an existing admin's API key and send it as `X-Api-Key`. |
| Copied DB shows as empty after container restart | A stale WAL file was applied on top. Use the helper container sequence in "Copying an existing SQLite DB into the container" above — it removes the `.db-wal` / `.db-shm` files before copying. |
