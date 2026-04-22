# AITestCrew — Functional Documentation

## Overview

AITestCrew is an AI-powered test automation framework that uses LLMs (Claude via Anthropic SDK) to generate, execute, and validate tests across multiple surfaces — REST/GraphQL APIs, web UIs (Blazor and ASP.NET MVC via Playwright), Windows desktop applications (WinForms via FlaUI), and AEMO B2B aseXML transaction workflows (template-driven generation, SFTP/FTP delivery to Bravo endpoints, and post-delivery UI verification). Tests can be AI-generated from plain English objectives or human-recorded via interactive browser/desktop sessions, then saved and replayed deterministically without further LLM calls.

---

## What It Does

1. **Understands your objective** — You provide a plain English description of what to test. This can target any supported surface: an API endpoint, a web UI workflow, a desktop application flow, or an AEMO B2B aseXML transaction.
2. **Routes to the right agent** — The orchestrator decomposes the objective and dispatches each task to a specialised agent based on the target type (API, Blazor, MVC, WinForms, aseXML generation, or aseXML delivery).
3. **Generates or replays test steps** — AI-generated mode: the LLM produces test steps grounded in real application state (live API responses, actual DOM selectors from browser exploration, UI Automation tree snapshots). Recording mode: you interact with the application while the recorder captures your actions as replayable steps with auto-detected selectors.
4. **Executes the tests** — Each agent runs its steps against the real target: HTTP requests for APIs, Playwright browser automation for web UIs, FlaUI automation for desktop apps, or template rendering + SFTP/FTP upload for aseXML deliveries.
5. **Validates results** — APIs use a hybrid rule-based + LLM validation pipeline. Web and desktop UI agents execute Playwright/FlaUI assertions (text, visibility, URL). aseXML delivery agents optionally run post-delivery UI verifications — recorded UI steps that check the downstream processing in Bravo with `{{Token}}` substitution from each delivery's context (NMI, MessageID, TransactionID, etc.).
6. **Reports results** — A summary table and an LLM-written narrative are printed to the console and shown in the React web dashboard. Per-step pass/fail detail, screenshots on failure, and execution history with run-over-run comparison are available.
7. **Saves and reuses test sets** — Generated and recorded test cases are persisted to disk (JSON files) or a SQLite database, organised into modules and test sets, so they can be re-executed repeatedly without calling the LLM again. Verify-only mode allows re-running just the UI verification steps of a delivery test case without re-delivering.

---

## Available Agents

AITestCrew routes each test task to a specialised agent based on the task's `TestTargetType`. The orchestrator calls `CanHandleAsync` on each registered agent and dispatches to the first match.

| Agent | Target Type(s) | What it does |
|---|---|---|
| **API Agent** (`ApiTestAgent`) | `API_REST`, `API_GraphQL` | LLM-generated REST/GraphQL test cases — endpoint discovery, HTTP execution, hybrid rule-based + LLM response validation |
| **Brave Cloud UI Agent** (`BraveCloudUiTestAgent`) | `UI_Web_Blazor` | Playwright-based Blazor web UI testing with Azure AD SSO + TOTP/MFA authentication |
| **Legacy Web UI Agent** (`LegacyWebUiTestAgent`) | `UI_Web_MVC` | Playwright-based legacy ASP.NET MVC web UI testing with forms authentication and StorageState caching |
| **WinForms Desktop UI Agent** (`WinFormsUiTestAgent`) | `UI_Desktop_WinForms` | FlaUI-based Windows Forms desktop application testing — LLM-driven exploration via UI Automation tree, recording via Windows hooks, deterministic replay |
| **aseXML Generation Agent** (`AseXmlGenerationAgent`) | `AseXml_Generate` | Template-driven AEMO B2B aseXML payload generation. LLM picks a template and extracts user field values from the objective; the renderer performs deterministic `{{token}}` substitution with auto-generated MessageID/TransactionID/timestamps. |
| **aseXML Delivery Agent** (`AseXmlDeliveryAgent`) | `AseXml_Deliver` | Renders an aseXML payload and uploads it to a Bravo inbound drop location resolved from `mil.V2_MIL_EndPoint` by `EndPointCode`. SFTP/FTP auto-detected from `OutBoxUrl` scheme. Wraps the XML in a `{MessageID}.zip` when the endpoint's `IsOutboundFilesZiped = 1`. |

All agents extend `BaseTestAgent`, which provides shared LLM communication (`AskLlmAsync`, `AskLlmForJsonAsync`). Web UI agents share `BaseWebUiTestAgent` (Playwright), desktop UI agents share `BaseDesktopUiTestAgent` (FlaUI).

```
BaseTestAgent                          ← LLM helpers
  ├── ApiTestAgent                     ← REST / GraphQL
  ├── BaseWebUiTestAgent               ← Playwright shared infra (abstract)
  │     ├── BraveCloudUiTestAgent      ← Blazor (UI_Web_Blazor)
  │     └── LegacyWebUiTestAgent       ← Legacy MVC (UI_Web_MVC)
  ├── BaseDesktopUiTestAgent           ← FlaUI shared infra (abstract)
  │     └── WinFormsUiTestAgent        ← Windows Forms (UI_Desktop_WinForms)
  ├── AseXmlGenerationAgent            ← AEMO aseXML payload rendering (AseXml_Generate)
  └── AseXmlDeliveryAgent              ← Render + SFTP/FTP upload to Bravo (AseXml_Deliver)
```

The following target types are defined but do not yet have agent implementations: `Background_Hangfire`, `MessageBus`, `Database`.

### aseXML templates

Templates live under `templates/asexml/{TransactionType}/{templateId}.xml`, each paired with a `{templateId}.manifest.json` that declares every token used in the template and classifies it as:

- **`auto`** — generated at render time (e.g. `MessageID`, `TransactionID`, timestamps). Never overridable.
- **`user`** — supplied by the caller (LLM-extracted from the objective, or, later, via the edit dialog). `required: true` fields cause a failing step when missing. Optional `description` guides the LLM on structure; optional `format` (e.g. `"nem12"`) triggers post-render validation.
- **`const`** — hardwired in the template (e.g. `From`, `To`, `SupplyOn`, `SupplyOff`). Surfaced for display but not editable.

Adding a new transaction type is a content change, not a code change: drop a new `{template}.xml` + `{template}.manifest.json` pair under `templates/asexml/{TransactionType}/` and the registry picks it up at next startup. Adding a new auto-field generator is a one-method change in `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs`.

**Shipped transaction types:**

| Transaction | Templates | Notes |
|---|---|---|
| `MeterFaultAndIssueNotification` (MFN) | `MFN-OneInAllIn` | DNSP-initiated outage notification to a FRMP. |
| `MeterDataNotification` (MDN) | `MDN-NEM12-30min`, `MDN-NEM12-5min` | MDP-initiated NEM12 interval data delivery. The `CsvIntervalData` user field carries the NEM12 CSV body (100/200/300/400/500/900 records) and is grammar-validated post-render via `Nem12CsvValidator` — malformed CSV fails the render step with a line-level diagnostic. The 30-min variant matches the plain MDP header shape (no `description` attr, no `SecurityContext`); the 5-min variant matches MDPs that include descriptions + `SecurityContext`. |

Rendered XML is written to `output/asexml/{timestamp}_{taskId}/{NN}-{caseName}.xml`. The delivery agent (`AseXml_Deliver`) renders the XML, uploads it via SFTP/FTP to a Bravo endpoint, and optionally runs post-delivery UI verifications with `{{Token}}` substitution from each run's context.

---

## Modules and Test Sets

Tests are organised in a four-level hierarchy: **Module > Test Set > Test Objective (Test Case) > Steps**.

- **Module** — a top-level grouping representing an application area (e.g. "Standing Data Replication (SDR)").
- **Test Set** — a user-defined container within a module. Multiple test objectives accumulate in a test set over time.
- **Test Objective (Test Case)** — corresponds to a single user objective (e.g. "Test GET /api/ControlledLoadDecodes"). Contains one or more steps.
- **Step** — an individual API call (`ApiTestDefinition`) or UI test case (`WebUiTestDefinition`). Each step has its own pass/fail result. The objective's overall status is the aggregate of its steps.

### Creating Modules and Test Sets (CLI)

```bash
# Create a module
dotnet run --project src/AiTestCrew.Runner -- --create-module "Standing Data Replication (SDR)"

# List all modules
dotnet run --project src/AiTestCrew.Runner -- --list-modules

# Create an empty test set within a module
dotnet run --project src/AiTestCrew.Runner -- --create-testset sdr "Controlled Load Decodes"
```

### Creating via the Web UI

1. Open the dashboard — the root page shows all modules.
2. Click **+ Create Module** to create a module.
3. Click into a module, then **+ Test Set** to create a test set.
4. Click **Run Objective** to generate tests and add them to a test set.

> **Note:** When running in SQLite mode, the web UI requires authentication. Enter your API key on the login page to access the dashboard.

### Editing Test Cases

Test cases can be reviewed and modified after generation via two methods:

**Direct edit** — Click any test objective in the test set detail page, then click a specific step to open an editor. All fields are editable: name, method, endpoint, headers, query params, body, expected status, and body assertions. The edit dialog accepts a `stepIndex` to target a specific step within the objective.

**Delete individual steps** — Each step row in the test case table has an inline delete icon (trash). Click it, confirm with Yes/No, and only that step is removed from the objective. The "Delete Step" button in the edit dialog works the same way. If you delete the last remaining step in an objective, the entire objective is removed. This uses the existing PUT endpoint — the step is removed from the array client-side and the modified objective is saved.

**API endpoint:** `PUT /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}` (full objective body in request).

**AI edit (natural language patch)** — Click the **AI Edit Test Cases** button above the test case table. Describe the change in plain English (e.g., *"remove the /api/v1 prefix from all endpoints"* or *"change expectedStatus to 200 for the happy path test"*). The system uses the LLM to generate a preview of changes, which you can review field-by-field before applying. Scope can be set to all objectives or a specific objective.

---

## Multi-User Deployment

AITestCrew supports single-user file-based usage out of the box and scales to multi-user team deployments with SQLite storage, API key authentication, and distributed recording.

### Storage backends

| Backend | Config | Description |
|---|---|---|
| **File** (default) | `StorageProvider: "File"` | JSON files in `modules/`, `testsets/`, `executions/` relative to `AppContext.BaseDirectory` |
| **SQLite** | `StorageProvider: "Sqlite"` | Single database file. Set `SqliteConnectionString` (e.g. `"Data Source=C:/data/aitestcrew.db"`) |

Migrate existing JSON data to SQLite:
```bash
dotnet run --project src/AiTestCrew.Runner -- --migrate-to-sqlite
```
This reads all JSON files from the current data directory and inserts them into the configured SQLite database. Requires `SqliteConnectionString` to be set.

### Configuration

Key settings in `appsettings.json → TestEnvironment`:

| Setting | Description | Default |
|---|---|---|
| `StorageProvider` | `"File"` or `"Sqlite"` | `"File"` |
| `SqliteConnectionString` | SQLite database path | `null` |
| `ListenUrl` | WebApi bind URL. Supports multiple URLs separated by semicolons | `""` (= `http://localhost:5050`) |
| `CorsOrigins` | String array of allowed origins. `["*"]` = any origin | `[]` (= Vite dev defaults) |
| `ServerUrl` | (Runner only) WebApi URL for remote mode | `null` |
| `ApiKey` | (Runner only) API key for remote auth | `null` |

**Environment variable overrides** — prefix with `AITESTCREW_`, use double underscores for nesting:
```
AITESTCREW_TestEnvironment__StorageProvider=Sqlite
AITESTCREW_TestEnvironment__SqliteConnectionString=Data Source=C:/data/aitestcrew.db
```

### User management

Users are identified by API keys (format: `atc_` + 48-char random hex). Auth is active only in SQLite mode (when `IUserRepository` is registered).

**Bootstrap** — the first user is created without authentication:
```bash
curl -X POST http://server:5050/api/users \
  -H "Content-Type: application/json" \
  -d '{"name": "YourName"}'
```
The response includes the generated API key. Store it securely — it cannot be retrieved later.

**Subsequent users** require an existing user's API key in the `X-Api-Key` header:
```bash
curl -X POST http://server:5050/api/users \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: atc_<existing-key>" \
  -d '{"name": "AnotherUser"}'
```

The web UI shows a login page when auth is active. After entering a valid API key, the user's name and a logout button appear in the header.

### Distributed recording

When `ServerUrl` is configured in the Runner's `appsettings.json`, all recording commands sync to the remote server automatically.

- Each QA engineer runs the Runner CLI locally for recording (browser/desktop access is needed on the local machine).
- Recorded test cases appear in the shared web dashboard immediately after save.
- Test execution can happen centrally from the server via the WebApi `/api/runs` endpoint or from any Runner pointed at the same `ServerUrl`.

### Concurrent runs

- Multiple users can run different test sets simultaneously.
- The same test set cannot be run concurrently — the API returns `409 Conflict`.
- Module-level "Run All" runs are locked per module — only one module-level run at a time.

### Deployment options

**1. Docker Compose (Windows containers)**
```bash
docker compose up -d --build
```

**2. Self-contained publish**
```powershell
.\publish.ps1 -OutputDir C:\deploy
```
Run `AiTestCrew.WebApi.exe` directly or install as a Windows Service. The published output includes all dependencies — no .NET SDK required on the target machine.

---

## Run Modes

### Normal (default)
Generate new test cases via LLM, save them to disk, execute them.

**Legacy (flat) mode:**
```
dotnet run --project src/AiTestCrew.Runner -- "Test the /api/products endpoint"
```

**Module-scoped mode** (recommended):
```
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset controlled-loads "Test the /api/ControlledLoadDecodes endpoint"
```

**With a short objective name** (optional):
```
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset controlled-loads --obj-name "Ctrl Loads GET" "Test the /api/ControlledLoadDecodes endpoint"
```

The `--obj-name` flag assigns a short display name to the objective. This name is shown in the UI and CLI list output instead of the full objective text. If omitted, the full text is displayed (truncated where needed). In the Web UI, the "Short Name" field in the Run Objective dialog serves the same purpose.

In module-scoped mode, generated test cases are **merged** into the target test set. Running another objective against the same test set accumulates tests. Re-running the exact same objective text updates the existing entry in place (replaces its steps with the newly generated ones).

Output:
- Executes LLM-generated test steps per objective (count depends on objective specificity).
- Saves/merges the objective (with its steps) into the target test set.
- Prints a results table and LLM summary to the console.

---

### Reuse
Re-execute a previously saved test set without calling the LLM. The exact same test cases are run every time, making this suitable for regression checking.

```
dotnet run --project src/AiTestCrew.Runner -- --reuse <id>
```

Or module-scoped:
```
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset controlled-loads --reuse controlled-loads
```

Scope to a single test case within the set:
```
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset controlled-loads \
  --reuse controlled-loads --objective ctrl-loads-get
```

- By default `--reuse` runs every `TestObjective` (test case) in the set in parallel.
- `--objective <id>` restricts the run to a single test case — useful for iterating on one delivery / UI verification without re-running siblings. The ID is the `TestObjective.Id` slug visible in the test set JSON or the Web UI detail page.
- No LLM calls during test generation — uses saved step definitions directly.
- LLM is still used for response validation and the final summary.
- `RunCount` and `LastRunAt` are updated in the saved file after each reuse run.
- The same `--objective <id>` flag is also consumed by `--record-verification` (as the objective to attach the recorded verification to) — the meaning is context-dependent on whether you're reusing or recording.

---

### Rebaseline
Regenerate test cases from scratch via LLM (fresh set), overwrite the saved objective, and execute. Use this when the API has changed and you want new tests. Rebaseline only applies to **AI-generated** objectives — recorded objectives (created via `--record`) cannot be rebaselined.

```
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "Test the /api/products endpoint"
```

Or module-scoped:
```
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset controlled-loads --rebaseline "Test the /api/ControlledLoadDecodes endpoint"
```

---

### Generate aseXML transactions

Generate AEMO B2B aseXML payloads from templates using plain-English objectives. The LLM reads the template catalogue, picks the matching template, and extracts user field values from the objective. The renderer then produces the XML deterministically — auto-fields (`MessageID`, `TransactionID`, timestamps) are fresh on every run.

```bash
# Pre-req (once per module): create the module and test set
dotnet run --project src/AiTestCrew.Runner -- --create-module "AEMO B2B"
dotnet run --project src/AiTestCrew.Runner -- --create-testset aemo-b2b "MFN scenarios"

# Generate one MFN from an objective
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-scenarios \
  --obj-name "MFN One In All In (NMI 4103035611)" \
  "Send a MeterFaultAndIssueNotification for NMI 4103035611 checksum 3, identified 2025-05-04, start 2025-05-07 10:00:00+10:00, end 2025-05-07, duration 02:00, meter 060738, reason 'One In All In'."

# Re-render all saved cases — fresh MessageID/TransactionID each time
dotnet run --project src/AiTestCrew.Runner -- --module aemo-b2b --testset mfn-scenarios --reuse mfn-scenarios
```

Rendered XML is written to `src/AiTestCrew.Runner/bin/Debug/net8.0-windows/output/asexml/{yyyyMMdd_HHmmss}_{taskId}/{NN}-{caseName}.xml`. Each file's `TestStep` detail includes the resolved field values plus the generated `MessageID` / `TransactionID`.

**Adding a new transaction type** is a content-only change: drop a new `{templateId}.xml` + `{templateId}.manifest.json` pair under `templates/asexml/{TransactionType}/` and rebuild (the `<Content>` entry in `AiTestCrew.Runner.csproj` and `AiTestCrew.WebApi.csproj` copies it to each project's bin). No recompile of agents or orchestrator needed — `TemplateRegistry` discovers the new pair at next startup.

See [aseXML templates](#asexml-templates) above for the manifest schema and generator reference.

---

### Deliver aseXML transactions

Generate an aseXML payload and ship it to a real Bravo inbound drop location in one step. The `AseXmlDeliveryAgent` resolves the endpoint from the Bravo DB (`mil.V2_MIL_EndPoint` keyed by `EndPointCode`), renders the XML using the same template + manifest pipeline as `--module/--testset` generation, and uploads via SFTP or FTP (auto-detected from `OutBoxUrl`). If the endpoint has `IsOutboundFilesZiped = 1`, the XML is wrapped in a `{MessageID}.zip` archive before upload.

**Prerequisites** (in `appsettings.json`, never `appsettings.example.json`):

```json
"AseXml": {
  "BravoDb": {
    "ConnectionString": "Server=<bravo-db-host>;Database=<db-name>;User Id=<user>;Password=<pw>;TrustServerCertificate=True;"
  }
}
```

```bash
# Discover available endpoints from the Bravo DB
dotnet run --project src/AiTestCrew.Runner -- --list-endpoints

# Deliver an MFN to GatewaySPARQ (endpoint code extracted from the objective)
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-delivery \
  --obj-name "Deliver MFN One In All In to GatewaySPARQ" \
  "Deliver a MeterFaultAndIssueNotification for NMI 4103035611 checksum 3, identified 2025-05-04, start 2025-05-07 10:00:00+10:00, end 2025-05-07, duration 02:00, meter 060738, reason 'One In All In' to the GatewaySPARQ endpoint."

# Same case but force the endpoint explicitly (overrides the objective / saved default)
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-delivery \
  --endpoint GatewaySPARQ \
  --obj-name "MFN One In All In to GatewaySPARQ (explicit)" \
  "Send an MFN for NMI 4103035611 ..."

# Re-run saved deliveries — new MessageID/TransactionID each run, same endpoint
dotnet run --project src/AiTestCrew.Runner -- --reuse mfn-delivery --module aemo-b2b
```

Agent steps per case (visible in the run report):

- `render[N]` — XML body produced and written to the local debug dir at `output/asexml/{timestamp}_{taskId}_deliver/{NN}-*.xml`.
- `resolve-endpoint[N]` — Bravo DB lookup; summary line includes host, `OutBoxUrl`, and whether the endpoint is zipped.
- `package[N]` — only when `IsOutboundFilesZiped = 1`; reports uncompressed / compressed size and ratio.
- `upload[N]` — `SFTP` or `FTP` tag + remote path + bytes + duration.

**Security**: the endpoint `Password` is never written to logs or step details. Connection strings belong in `appsettings.json` only.

#### Attach UI verifications (Phase 3)

Once a delivery test case has been run at least once — so Bravo has actually processed a real file — you can attach UI verification steps to it. The steps run automatically after every subsequent delivery: the XML is dropped, a fixed delay elapses (configurable per verification), then the matching UI agent (Legacy MVC, Blazor, or WinForms) replays the recorded steps against the live UI. Values from each run's fresh render (`{{NMI}}`, `{{MessageID}}`, `{{TransactionID}}`, `{{Filename}}`, and any template field) are substituted into every UI step field at playback.

**Recording** — the recorder launches with the **context already loaded** from (a) the delivery case's `FieldValues` dictionary and (b) the latest successful run's MessageID / TransactionID / Filename / EndpointCode from execution history. As you interact with the UI, any typed literal that matches a context value is saved as the corresponding `{{Token}}` — no manual JSON editing needed.

```bash
# Pre-req: the delivery objective has been run at least once with status Passed.
# (Look up the objective id from the test set JSON or the web UI.)

# Record a Blazor verification
dotnet run --project src/AiTestCrew.Runner -- --record-verification \
  --module aemo-b2b --testset mfn-delivery-tests \
  --objective deliver-mfn-one-in-all-in \
  --target UI_Web_Blazor \
  --environment sumo-retail \
  --verification-name "MFN Process Overview shows 'One In All In'" \
  --wait 30

# Record a Legacy MVC verification against the same delivery case
dotnet run --project src/AiTestCrew.Runner -- --record-verification \
  --module aemo-b2b --testset mfn-delivery-tests \
  --objective deliver-mfn-one-in-all-in \
  --target UI_Web_MVC \
  --environment sumo-retail \
  --verification-name "Legacy MFN Search grid row exists"

# Record a WinForms desktop verification
dotnet run --project src/AiTestCrew.Runner -- --record-verification \
  --module aemo-b2b --testset mfn-delivery-tests \
  --objective deliver-mfn-one-in-all-in \
  --target UI_Desktop_WinForms \
  --environment sumo-retail \
  --verification-name "Desktop admin tool shows the transaction"
```

`--environment` is optional — defaults to `DefaultEnvironment`. Use it to record verifications against a non-default customer (e.g. `--environment ams-metering`). The cached auth state and base URL for that env are used automatically.

At record time, the CLI prints the auto-parameterise context (which `{{Tokens}}` are available) so you can see what matches to expect.

**Auto-parameterisation rules** — to keep false positives low:
- Minimum literal length of **4 characters** (short values like a checksum `3` or duration `02:00` stay literal).
- Longest-match-first — a MessageID wins over a substring that happens to collide.
- Exact substring match only (no regex, no word boundaries).
- First context key wins on collisions; a WARN is logged.

**Playback** — on every subsequent `--reuse`, each delivery case produces steps in this order:

```
render[1] <Template>        → XML body produced
resolve-endpoint[1]         → Bravo DB lookup for EndPointCode
[package[1]]                → zip if the endpoint has IsOutboundFilesZiped = 1
upload[1]                   → SFTP/FTP drop to the remote path
wait[1.1]                   → configured seconds before the first verification
verify[1.1] <child steps>   → first verification's UI steps, tokens substituted
wait[1.2]                   → seconds before the second verification
verify[1.2] <child steps>   → second verification
...
```

If a verification step fails, the overall test case is marked Failed. Use Playwright screenshots (`PlaywrightScreenshotDir`) or WinForms screenshots (`WinFormsScreenshotDir`) for post-mortem.

**Verify-only mode** — re-run only the post-delivery UI verifications without re-rendering or re-uploading the XML. Useful when a verification fails due to a recording error, timeout, or selector issue and the delivery itself is fine:

```bash
# Re-run verifications with recorded wait times
dotnet run --project src/AiTestCrew.Runner -- --verify-only \
  --reuse mfn-delivery-tests --module aemo-b2b \
  --objective "Deliver MFN One In All In to GatewaySPARQ"

# Skip wait delays (file already processed on the server)
dotnet run --project src/AiTestCrew.Runner -- --verify-only \
  --reuse mfn-delivery-tests --module aemo-b2b \
  --objective "Deliver MFN One In All In to GatewaySPARQ" \
  --wait 0
```

The context (MessageID, TransactionID, NMI, etc.) is reconstructed from the latest successful delivery in execution history, combined with the test definition's `FieldValues`. Requires at least one prior successful delivery run. The React UI exposes a teal **Verify** button alongside Run/Rebaseline for delivery objectives that have verifications — it triggers verify-only with `--wait 0` by default.

**Skip the login flow when recording MVC verifications**:

The recorder opens with whatever cached auth state is configured for the target:

- `UI_Web_Blazor` → `BraveCloudUiStorageStatePath` (Azure SSO session).
- `UI_Web_MVC` → `LegacyWebUiStorageStatePath` (forms-auth cookies).

If the corresponding path isn't cached yet, the CLI prints a hint:

```
No 'LegacyWebUiStorageStatePath' configured — recorder will start unauthenticated.
Run --auth-setup --target UI_Web_MVC first to skip the login flow during recording.
```

Run `--auth-setup --target UI_Web_MVC --environment <envKey>` (or `UI_Web_Blazor`) once per environment to populate the cache. Subsequent `--record-verification --environment <envKey>` runs open the recorder already logged in as the right customer, so your captured steps are only the actual verification actions — no credentials ever land in the saved test set.

**View, edit, and delete verifications in the web UI**:

Open the test set detail page (`http://localhost:5173` → module → test set). Under each delivery case, the **Post-delivery UI verifications** panel shows one row per verification.

- **View the recorded steps** — click a verification row (the ▸ chevron). The row expands to show a per-step table with action / selector / value / timeout. `{{Tokens}}` are highlighted in indigo so you can verify auto-parameterisation worked at a glance.
- **Edit a verification** — Web UI verifications show a pencil ✎ icon. Click it to open the same **Edit Web UI Test Case** dialog used for standalone web UI tests, pre-populated with the recorded definition. Change the Name, Description, Start URL, Screenshot-on-failure flag, or reorder / delete / edit individual steps (same editor). Saving calls `PUT /api/modules/{mod}/testsets/{ts}/objectives/{obj}/deliveries/{d}/verifications/{v}`.
- **Delete a verification** — trash icon + inline Yes/No confirm. The delete call is `DELETE` on the same path. Use this when you want to re-record from scratch.
- **Desktop (WinForms) verifications** — currently view + delete only (no edit dialog). To modify a recorded desktop verification, delete and re-record.

**Constraints**:
- Recording requires a prior Passed run for the target objective (otherwise there's no known MessageID/TransactionID to parameterise against).
- Only delivery objectives (`AseXml_Deliver`) support verifications. Attempting to attach a verification to another target fails with a clear error.
- Manual edits to the saved JSON are supported — unknown tokens at playback are left as literals (with a WARN) rather than failing silently.

---

### Record (Web UI only)

Record a Web UI test case by interacting with a real browser. The CLI opens a non-headless Chromium window and captures every form fill and button click as exact `WebUiTestDefinition` steps with verified DOM selectors.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record \
  --module <moduleId> \
  --testset <testSetId> \
  --case-name "Happy path - valid credentials reach home" \
  --target UI_Web_MVC \
  --environment sumo-retail
```

- `--target` is `UI_Web_MVC` (uses `LegacyWebUiUrl`) or `UI_Web_Blazor` (uses `BraveCloudUiUrl`).
- `--environment <key>` picks which customer environment to record against — defaults to `DefaultEnvironment` if omitted. The recorder loads the matching cached auth state (saved by `--auth-setup --environment <key>`) so it starts authenticated.
- Module and test set are created automatically if they do not exist.
- Module/test set names are slugified so they match the WebApi directory structure.
- Both MVC and Blazor targets replay at 1920×1080 viewport. The recorder uses `NoViewport` (maximized window) for MVC; Blazor uses 1920×1080 to match replay. Saved auth state is loaded if available.
- The session ends when you click **Save & Stop** in the overlay, close the browser, or after 15 minutes.
- After recording, the tool validates steps and warns about weak selectors, missing assertions, and SPA timing risks.

See [Web UI Testing — Recording Mode](#option-2--recording-mode-recommended-for-reliability) for full details.

### Record (Desktop UI — WinForms)

Record a Windows Forms desktop test case by interacting with the real application. The CLI launches the target exe and captures clicks, typing, and clipboard paste via Windows hooks.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record \
  --module <moduleId> \
  --testset <testSetId> \
  --case-name "NMI Search" \
  --target UI_Desktop_WinForms \
  --environment sumo-retail
```

- `--target UI_Desktop_WinForms` uses the env's `WinFormsAppPath` (falling back to the top-level field). Different customers can point at different desktop builds.
- `--environment <key>` selects which customer app/build to record against — defaults to `DefaultEnvironment`.
- The app launches with its working directory set to the exe's folder (so sibling DLLs load correctly).
- Console keys add assertions: **T** (text), **V** (visible), **E** (enabled), **S** (save & stop).
- Ctrl+V paste is captured with the actual clipboard content, not just the "V" keystroke.
- Title bar clicks, taskbar clicks, and system UI elements are automatically filtered out.
- Post-recording validation warns about TreePath-only selectors, consecutive clicks, and missing assertions.

See [Desktop UI Testing](#desktop-ui-testing-winforms) for full details.

### Record Setup Steps (Web UI only)

Record reusable setup steps (e.g. login) that run automatically before every test case in a test set. This avoids duplicating login steps in each test and lets you change credentials in one place.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record-setup \
  --module <moduleId> \
  --testset <testSetId> \
  --target UI_Web_MVC \
  --environment sumo-retail
```

- Opens a browser — perform your login/setup actions, then click **Save & Stop**.
- `--environment <key>` selects which customer env's URL + cached auth state to use — defaults to `DefaultEnvironment`.
- The recorded steps are saved as the test set's `setupSteps` (not as a test objective).
- During replay (`--reuse`), setup steps run before each test case in a fresh browser context: navigate to `setupStartUrl`, execute setup steps, then navigate to the test case's own start URL and execute its steps.
- Setup steps can also be viewed, edited, and cleared in the web dashboard under the test set detail page.
- If a setup step fails during replay, the remaining setup steps and all test case steps are skipped for that test case.
- Running `--record-setup` again for the same test set replaces the existing setup steps.

### Data Teardown (SQL)

Attach one or more SQL `DELETE`/`UPDATE` statements to a test set that AITestCrew runs **once per objective**, immediately before dispatching the agent, to clear server-side state written by prior runs. Use case: re-running an MDN delivery for NMI `6203575700` on `2026-04-01` fails on duplicate reads unless the prior run's rows are removed first — a teardown step handles that automatically.

**Where it's edited:** the test set detail page in the web dashboard has a *Data Teardown (SQL)* panel next to *Setup Steps*. Each step has a name + SQL body; rows are ordered, can be reordered/removed, and the whole list can be cleared.

#### Token substitution

SQL bodies may reference `{{Token}}` placeholders. At run time, tokens resolve from (ascending priority — later sources win):

1. The **prior successful run's delivery context** from execution history (`MessageID`, `TransactionID`, `Filename`, `EndpointCode`, `RemotePath`, `UploadedAs`). This is what lets you target the previous run's data for cleanup using auto-generated identifiers — the same identifiers the AEMO B2B chain uses to thread MDN/MFN/etc. transactions through the DB.
2. The objective's per-environment parameters (`EnvironmentParameters[envKey]`).
3. The first `AseXmlDeliverySteps[0].FieldValues` entry (e.g. `{{NMI}}`, `{{ReadDate}}`, `{{MeterSerial}}`).

On the very first run for an objective (no prior history), `MessageID`/`TransactionID` are absent from the context and any teardown step that references them will fail strict-mode substitution — that's the right behaviour, since there's no prior data to clean. Unknown tokens always fail the teardown loudly — nothing runs.

#### When it runs (by mode)

| Trigger | Mode | Teardown? |
|---|---|---|
| Dashboard **Run** button (test set or single objective), CLI `--reuse` | `Reuse` | **Yes** (when configured + env opted in) |
| Dashboard **Verify** button, CLI `--verify-only` | `VerifyOnly` | **No** — verifications inspect the just-delivered data, deleting it would defeat the purpose |
| CLI Normal run (no `--reuse`) | `Normal` | No (initial test-set creation) |
| CLI `--rebaseline` | `Rebaseline` | No (regenerates definitions, not data) |
| Any mode with `--skip-teardown` | any | No (explicit bypass) |

#### Per-environment opt-in

Teardown is gated by `TestEnvironment.Environments.<env>.DataTeardownEnabled` (or the top-level `TestEnvironment.DataTeardownEnabled` fallback), both defaulting to `false`. Attempting a run against an env that hasn't opted in fails fast with a clear error — no SQL executes.

> **Edit BOTH `appsettings.json` files.** The Runner (`src/AiTestCrew.Runner/appsettings.json`) and the WebApi (`src/AiTestCrew.WebApi/appsettings.json`) bind their own copies of `TestEnvironment` at startup. UI-driven runs read the WebApi's config; CLI-driven runs read the Runner's. Keep them in sync (or move both to a shared file via `--config`-style overrides). After any change, **restart the WebApi process** — config is bound once at startup, not hot-reloaded.

The dashboard's Data Teardown panel queries `/api/config/environments` to know whether the env has opted in; when it hasn't, the panel shows a yellow warning banner but still lets you edit the SQL so you can prepare ahead of enabling the flag.

#### Guardrails (applied at save AND run time)

- Every statement must contain `WHERE` (word-boundary) — prevents accidental table-wide deletes.
- Denylist: `TRUNCATE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, `EXECUTE`, `SHUTDOWN`, `GRANT`, `REVOKE`, `MERGE`.
- Line (`-- ...`) and block (`/* ... */`) comments are stripped before checking so they can't conceal or smuggle keywords.

#### CLI flags

- `--teardown-dry-run` — logs the fully substituted SQL and skips execution. Use for first-time sanity checks before any DELETE actually fires.
- `--skip-teardown` — bypass teardown entirely for this run.

#### Failure policy

On a teardown failure for one objective, that objective is marked `Error`, its teardown result is persisted to the execution history, and subsequent objectives in the test set continue — one bad teardown doesn't abort the whole suite. No SQL is partially executed: every step is token-substituted and guardrail-checked **before** any connection opens, and within a connection the first failed step aborts the rest for that objective.

#### Audit trail

Every executed statement is logged at Information level with environment, step name, substituted SQL, and rows affected. Teardown outcomes are also stored under each objective's `TeardownResults` in the execution history JSON (`executions/{testSetId}/{runId}.json`).

#### Worked example — MDN re-delivery cleanup

Test set `mdn-delivery-tests` in module `aemo-b2b` has one delivery objective with `FieldValues = { NMI: "6203575700", ReadDate: "2026-04-02" }`. After the first successful run, `MessageID` and `TransactionID` are also available from execution history.

```sql
-- Step 1: child reads (delete first to satisfy FKs)
DELETE FROM [mds].[V2_MDS_IntervalReadDay]
WHERE TransactionId IN (
  SELECT TransactionId FROM [mds].[V2_MDS_Transaction]
  WHERE ExternalTransactionReference = '{{TransactionID}}'
)

-- Step 2: also remove the index rows for this NMI/date
DELETE FROM [mds].[V2_MDS_IntervalIndexRead]
WHERE MeterReadStreamId IN (
  SELECT MeterReadStreamId FROM [mds].[V2_MDS_MeterReadStream]
  WHERE MarketIdentifier = '{{NMI}}'
) AND ReadDate = '{{ReadDate}}'

-- Step 3: response rows
DELETE FROM mds.V2_MDS_MeterDataResponse
WHERE TransactionId IN (
  SELECT TransactionId FROM [mds].[V2_MDS_Transaction]
  WHERE ExternalTransactionReference = '{{TransactionID}}'
)

-- Step 4: parent transaction (last)
DELETE FROM [mds].[V2_MDS_Transaction]
WHERE ExternalTransactionReference = '{{TransactionID}}'
```

Run sequence:

```bash
# 1. First run — no prior TransactionID exists, so dry-run any TransactionID-based steps first
dotnet run --project src/AiTestCrew.Runner -- \
  --reuse mdn-delivery-tests --module aemo-b2b \
  --environment sumo-retail --teardown-dry-run

# 2. Once you've confirmed tokens resolve, real run
dotnet run --project src/AiTestCrew.Runner -- \
  --reuse mdn-delivery-tests --module aemo-b2b \
  --environment sumo-retail
```

If a teardown step references `{{TransactionID}}` and the objective has never produced a successful delivery, the strict-mode substitution will fail — the very first run of a brand-new objective should either omit such steps or use `--skip-teardown` for that one run, then add the auto-token steps once history exists.

### Auth Setup

Save browser authentication state so that subsequent test runs start pre-authenticated, skipping the login flow entirely. Opens a visible browser — complete the login manually, and the session is saved automatically. **Environment-aware**: when multiple customer environments are configured, pass `--environment <key>` so the URL, credentials, and saved-state filename come from that env's block.

**Blazor SSO (default env):**
```bash
dotnet run --project src/AiTestCrew.Runner -- --auth-setup
```

**Blazor SSO (specific environment):**
```bash
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor --environment sumo-retail
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor --environment ams-metering
```

- Navigates to the env's `BraveCloudUiUrl` (falling back to the top-level field when the env block omits it) which redirects to Azure AD login.
- Complete the full login flow (including 2FA if required) in the visible browser.
- Once redirected back to the app, the auth state (cookies + localStorage) is saved to the env's `BraveCloudUiStorageStatePath` so each customer has its own cached state file.
- The saved state is valid for `BraveCloudUiStorageStateMaxAgeHours` (default 8). Re-run `--auth-setup --environment <key>` when it expires.
- If the env's `BraveCloudUiTotpSecret` is configured (base32 TOTP secret), the agent can automate 2FA during test runs — `--auth-setup` is only needed for initial or expired sessions.

**Legacy ASP.NET MVC:**
```bash
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC --environment sumo-retail
```

- Navigates to the env's `LegacyWebUiUrl` + top-level `LegacyWebUiLoginPath` and opens the login form.
- Complete the login in the visible browser.
- Once redirected away from the login page, the auth state is saved to the env's `LegacyWebUiStorageStatePath`.
- The saved state is valid for `LegacyWebUiStorageStateMaxAgeHours` (default 8).
- The state is also generated **automatically** on the first headless test run — `--auth-setup` is only needed if you prefer a manual interactive login.
- Under parallel execution, the first agent to run acquires a login lock, performs one headless login, and saves the state. All other concurrent agents reuse it.

On launch, the command prints the environment name and the exact storage-state path it will write to, so you can confirm the right file is being captured.

---

### List
Display all saved test sets and exit.

```
dotnet run --project src/AiTestCrew.Runner -- --list
```

Output includes: ID (slug), module, objective, objective count, total step count, created date, last run date, and total run count.

### Agent (Phase 4 — distributed execution)
Long-running worker mode. Turns the Runner into an agent that polls the central server for queued jobs (web UI or desktop UI tests the server can't execute on its own) and runs them locally on the current machine.

```
dotnet run --project src/AiTestCrew.Runner -- --agent --name "Alice-PC"
dotnet run --project src/AiTestCrew.Runner -- --agent --capabilities UI_Web_Blazor,UI_Web_MVC
```

`--name` defaults to the machine's hostname (`$env:COMPUTERNAME`). `--capabilities` defaults to all three UI targets (`UI_Web_Blazor,UI_Web_MVC,UI_Desktop_WinForms`). The agent registers, sends heartbeats every 30s on a dedicated task, polls the job queue every 10s, and deregisters gracefully on Ctrl+C. Requires `TestEnvironment.ServerUrl` and `ApiKey` to be set so it can reach the shared server. See `docs/deployment.md#local-agent-setup-phase-4` for the team setup.

**Behaviour notes:**
- **Screenshots** captured by the Web UI or Desktop UI agents (on step failure) are saved locally first, then uploaded to the server via `POST /api/screenshots` so the dashboard's execution-detail page can render them. Upload is silent on success; failures are logged as warnings but don't fail the run.
- **Legacy MVC serialization** — `UI_Web_MVC` objectives run sequentially inside one agent process via a static semaphore in `LegacyWebUiTestAgent`. This avoids 15-second Playwright timeouts caused by single-session enforcement on the legacy backend when multiple objectives in a set run concurrently. Blazor, API, aseXML, and Desktop agents still parallelize up to `MaxParallelAgents`.
- **Single-objective heading** — when the dashboard triggers a single test case (Run button on one row, not "Re-run Tests" on the whole set), the execution-detail heading shows that specific objective's name rather than the parent test set's original objective.
- **Force quit from dashboard** — the Agents panel on the dashboard exposes a red **Force quit** button next to each Online / Busy agent. Clicking it calls `POST /api/agents/{id}/force-quit`, which flags the agent and marks any in-flight job Failed. The agent's parallel heartbeat loop receives `shouldExit = true` on its next heartbeat (within ~30s) and calls `Environment.Exit(1)` — terminating the Runner process immediately, even when the main polling loop is blocked inside a stuck recording. Use this when a Playwright / FlaUI session hangs and Ctrl+C on the Runner machine is not draining. The agent row shows **Offline** in the dashboard the moment the endpoint returns, and the row stays that way until the Runner re-registers on next startup.

---

## Configuration

All settings are in `src/AiTestCrew.Runner/appsettings.json` under the `TestEnvironment` section.

| Setting | Description | Default |
|---|---|---|
| `LlmProvider` | `"Anthropic"` or `"OpenAI"` | `"OpenAI"` |
| `LlmApiKey` | API key for the LLM provider | *(required)* |
| `LlmModel` | Model identifier | `"gpt-4o"` |
| `ApiStacks` | Dictionary of named API stacks (see [Multi-Stack API Configuration](#multi-stack-api-configuration)) | `{}` *(required)* |
| `DefaultApiStack` | Default stack key when not specified per-run | `null` |
| `DefaultApiModule` | Default module key when not specified per-run | `null` |
| `OpenApiSpecUrl` | Optional URL to an OpenAPI/Swagger JSON spec | `null` |

> **Important — writing objectives with the right path**: Endpoint paths in test cases are generated relative to the resolved stack+module base URL. If the module's `PathPrefix` is `sdrapi/api/v1`, write objectives using paths that start after that prefix:
> - Correct: `"Test GET /NMIDiscoveryManagement/NMIDiscoveries"`
> - Incorrect: `"Test GET /sdrapi/api/v1/NMIDiscoveryManagement/NMIDiscoveries"` — duplicates the prefix
>
> Avoid pasting full URLs into objectives.
| `AuthToken` | Static token injected into every request (skip auto-login) | `null` |
| `AuthScheme` | `"Bearer"`, `"Basic"`, or `"None"` | `"Bearer"` |
| `AuthHeaderName` | Header name for auth | `"Authorization"` |
| `AuthUsername` | Username for auto-login (used when `AuthToken` is empty) | `null` |
| `AuthPassword` | Password for auto-login (used when `AuthToken` is empty) | `null` |
| `DefaultTimeoutSeconds` | Per-objective execution timeout | `300` |
| `VerboseLogging` | Show agent-level log lines in console | `true` |
| `MaxExecutionRunsPerTestSet` | Max execution runs to keep per test set (`0` = unlimited) | `10` |

### Multi-Stack API Configuration

The `ApiStacks` section defines one or more named API stacks. Each stack has a base URL, a security module (for auth), and a dictionary of API modules with path prefixes.

```json
"ApiStacks": {
  "bravecloud": {
    "BaseUrl": "https://sumo-dev.braveenergy.com.au",
    "SecurityModule": "security",
    "LoginPath": "/AccessManagement/Login",
    "Modules": {
      "sdr":      { "Name": "SDR (BraveCloud)",  "PathPrefix": "sdrbc/api/v1" },
      "security": { "Name": "Security (BraveCloud)", "PathPrefix": "sec/api/v1" },
      "mds":      { "Name": "MDS (BraveCloud)",  "PathPrefix": "mdsbc/api/v1" }
    }
  },
  "legacy": {
    "BaseUrl": "https://api-sumodev.braveenergy.com.au",
    "SecurityModule": "security",
    "LoginPath": "/AccessManagement/Login",
    "Modules": {
      "sdr":      { "Name": "SDR (Legacy)",      "PathPrefix": "sdrapi/api/v1" },
      "security": { "Name": "Security (Legacy)",  "PathPrefix": "secapi/api/v1" },
      "eb2b":     { "Name": "EB2B (Legacy)",      "PathPrefix": "eb2bapi/api/v1" }
    }
  }
}
```

When running tests, specify which stack and module to target:
- **CLI**: `--stack bravecloud --api-module sdr`
- **Web UI**: Select from the API Stack and API Module dropdowns in the Run Objective dialog
- **Persisted**: The `apiStackKey` and `apiModule` are saved on each test set and used automatically in reuse mode

The resolved base URL is `{stack.BaseUrl}/{module.PathPrefix}` (e.g. `https://sumo-dev.braveenergy.com.au/sdrbc/api/v1`).

Auth tokens are obtained per-stack from the security module's login endpoint: `{stack.BaseUrl}/{securityModule.PathPrefix}{LoginPath}`. Auth credentials (`AuthUsername`, `AuthPassword`, `AuthScheme`, `AuthHeaderName`) are shared across all stacks.

Users can add new stacks and modules by editing `appsettings.json` — no code changes required.

The `GET /api/config/api-stacks` endpoint exposes the configured stacks and modules to the React UI for populating dropdown selectors.

### Multi-Environment Support (customer-based)

A test set written once can run against multiple customer environments (e.g. `sumo-retail`, `ams-metering`, `tasn-networks`). An environment provides a block of **per-customer overrides** for the settings that differ between customers: UI URLs, credentials, storage-state filenames, WinForms app path + args, Bravo DB connection string, and per-stack API BaseUrls. Everything else (LLM keys, `ApiStacks` structure, module path prefixes, shared auth scheme) stays at the top level.

Orthogonal to the existing `--stack` / `--api-module` axes — environments multiplex customer deployments, stacks multiplex API platforms, modules multiplex services. You can combine all three at the same time.

#### Configuring environments

Add a block like this to `appsettings.json` under `TestEnvironment`:

```json
"DefaultEnvironment": "sumo-retail",
"Environments": {
  "sumo-retail": {
    "DisplayName": "Sumo Retail",
    "LegacyWebUiUrl":              "https://legacy-sumo-dev.braveenergy.com.au",
    "LegacyWebUiUsername":         "<forms-user>",
    "LegacyWebUiPassword":         "<forms-password>",
    "LegacyWebUiStorageStatePath": "legacy-auth-state.sumo.json",
    "BraveCloudUiUrl":             "https://sumo-dev.braveenergy.com.au",
    "BraveCloudUiUsername":        "<aad-email>",
    "BraveCloudUiPassword":        "<aad-password>",
    "BraveCloudUiTotpSecret":      "<base32>",
    "BraveCloudUiStorageStatePath": "bravecloud-auth-state.sumo.json",
    "WinFormsAppPath":             "C:\\Program Files\\Sumo\\BraveDesktop.exe",
    "WinFormsAppArgs":             "--tenant=sumo",
    "BravoDbConnectionString":     "Server=...;Database=...Sumo...;",
    "ApiStackBaseUrls": {
      "bravecloud": "https://sumo-dev.braveenergy.com.au",
      "legacy":     "https://api-sumodev.braveenergy.com.au"
    }
  },
  "ams-metering": { "DisplayName": "AMS Metering", "...": "..." },
  "tasn-networks": { "DisplayName": "TASN Networks", "...": "..." }
}
```

**Fallback behaviour** — any field omitted from an environment block falls back to the equivalent top-level field on `TestEnvironmentConfig`. This lets legacy configs (no `Environments` section at all) keep working unchanged. It also means you can migrate gradually: define one env at a time, leaving the top-level fields in place as defaults.

#### Selecting the environment at run time

| Where | How |
|---|---|
| CLI | `--environment <customerKey>` on Normal / Reuse / Rebaseline / VerifyOnly / Record / Record-setup / Record-verification / Auth-setup runs |
| WebApi | `environmentKey` field on `POST /api/runs` |
| UI | Environment dropdown in the **Run Objective** dialog |
| Persisted | The chosen env is saved on the test set; future runs use it automatically if `--environment` is omitted |

Precedence: explicit CLI / UI value → persisted test-set default → `DefaultEnvironment`.

List what's configured:

```bash
dotnet run --project src/AiTestCrew.Runner -- --list-environments
```

#### Per-objective scope and parameters

Each test objective carries two optional fields:

- **`AllowedEnvironments: string[]`** — which environments the objective is allowed to run on. Empty = "default environment only" (preserves legacy behaviour for tests recorded before multi-env was introduced). Objectives that are newly created or rebaselined by a run are auto-stamped with the active env. Widen the list via the UI editor to run the objective on additional environments.
- **`EnvironmentParameters: Dictionary<envKey, Dictionary<token, value>>`** — per-environment `{{Token}}` values applied at playback to every string / dict / list field on each step (`ApiTestDefinition.Endpoint`, `WebUiStep.Value`, `DesktopUiStep.AutomationId`, `AseXmlTestDefinition.FieldValues`, etc.).

The orchestrator:
1. Skips objectives excluded by `AllowedEnvironments` — shown as **Skipped** in the suite result, never **Failed**.
   - **Exception — explicit single-objective runs bypass the filter.** When the caller names one specific objective by Id or Name (the UI `Run` / `Verify` buttons on an individual row, or the CLI `--objective <idOrName>`), that objective runs regardless of `AllowedEnvironments` and a WARN is logged (`"… explicitly requested — running despite env filter (active='X', allowed='Y')"`). Bulk "run all" behaviour is unchanged. Rationale: explicit user intent overrides the env policy; without this, clicking Verify on a delivery whose allowed envs don't include the current default would fail with a misleading "not found" error.
2. For the remaining objectives, merges `EnvironmentParameters[activeEnv]` into the task's substitution context.
3. Each agent runs `StepParameterSubstituter.Apply(step, envParams)` before executing — step fields are cloned with `{{Token}}` literals replaced by the env's values. Unknown tokens stay as literals and a WARN is logged (lenient mode — mirrors post-delivery verification playback).

**Example** — one API objective runs on both customers with different NMIs and expected response codes:

```
AllowedEnvironments:      ["sumo-retail", "ams-metering"]
EnvironmentParameters:
  sumo-retail:  { NMI: "4103035611", ExpectedBody: "Active" }
  ams-metering: { NMI: "9999999999", ExpectedBody: "Active" }

ApiTestDefinition.Endpoint: "/Retail/Meters/{{NMI}}"
ApiTestDefinition.ExpectedBodyContains: ["{{ExpectedBody}}"]
```

At playback against `sumo-retail`, the URL becomes `/Retail/Meters/4103035611` and the assertion expects `Active`. The same objective against `ams-metering` gets `/Retail/Meters/9999999999`.

#### Editing environment parameters in the UI

Open a test objective (click a row in the test-case table) → the **Environment Parameters** card appears above the Steps section. Two controls:

1. **Allowed environments** — checkbox list populated from `/api/config/environments`. Tick the environments this objective should run on.
2. **Token values for `<env>`** — environment dropdown above a key/value grid. Add / rename / delete rows; each row is one `{{Token}} → value` pair for the selected environment.

Save writes back via `PUT /api/modules/{mod}/testsets/{ts}/objectives/{obj}`.

#### Tokenising existing recorded tests

Recorded steps contain literal values (NMIs, usernames, assertion text). To make them env-variable:

1. Record the test against one environment (e.g. `sumo-retail`) as usual. All values are captured as literals.
2. Open the objective's editor (API / Web UI / Desktop UI / aseXML) and replace the literal with `{{TokenName}}` in the step `Value` / `Endpoint` / `FieldValues`.
3. Open the Environment Parameters card, widen `AllowedEnvironments`, and add the matching `{{TokenName}} → value` pair for each environment.

Auto-tokenisation of existing recordings (analogous to the aseXML verification recorder) is on the roadmap but not in this release — tokenise manually per-objective for now.

#### Known limitations

- `--auth-setup --record --record-setup --record-verification` all accept `--environment`, but `--list-endpoints` always queries the default env's Bravo DB. If you need to list endpoints for another env, temporarily set it as `DefaultEnvironment` or override via the UI.
- Execution history stores `environmentKey` so you can filter runs by customer, but the dashboard currently renders it only in the run detail (a filter dropdown is a straightforward follow-up).

### Authentication

Authentication is injected automatically from config. The LLM is instructed not to add auth headers itself, so tests focus on functional behaviour rather than auth scenarios.

- **Auto-login (recommended)**: Set `AuthUsername` and `AuthPassword`, leave `AuthToken` empty. The system calls the stack's security module login endpoint to acquire a JWT automatically. Each stack gets its own cached token provider. Tokens are refreshed only when they expire (decoded from the JWT `exp` claim with a 60-second safety margin).
- **Static Bearer token**: Set `AuthToken` to a JWT, `AuthScheme` to `"Bearer"`, `AuthHeaderName` to `"Authorization"`. When `AuthToken` is set, auto-login is disabled.
- **API key header**: Set `AuthScheme` to `"None"`, `AuthHeaderName` to `"X-Api-Key"`, `AuthToken` to the key value.
- **No auth**: Leave all auth fields empty — the LLM will generate auth failure tests (401/403) as part of the test suite.

### OpenAPI Spec

If `OpenApiSpecUrl` is set, the spec is fetched before test case generation and passed to the LLM for additional context. This is optional — without it, the tool uses live endpoint discovery to infer the response shape.

---

## Output

### Console results table

| Column | Description |
|---|---|
| Objective | 8-character unique identifier for the test objective |
| Agent | Which agent executed the objective |
| Status | `Passed` / `Failed` / `Error` / `Skipped` |
| Steps | `{passed}/{total}` steps within the objective |
| Summary | LLM-generated one-line summary of the objective result |

### Overall result line

```
Overall: PASSED (3/3 objectives) in 01:42
```

### LLM narrative summary

3–5 sentences covering overall pass/fail, key findings, and recommended actions.

### Saved test set notification (Normal / Rebaseline modes)

```
Saved test set → C:\...\testsets\test-the-api-products-endpoint.json
Re-run later:  dotnet run -- --reuse test-the-api-products-endpoint
Regenerate:    dotnet run -- --rebaseline "Test the /api/products endpoint"
```

---

## Output Files

| Location | Contents |
|---|---|
| `modules/{moduleId}/module.json` | Module manifest (id, name, description, timestamps) |
| `modules/{moduleId}/{testSetId}.json` | Module-scoped test set (TestObjectives with ApiSteps/WebUiSteps + run metadata) |
| `testsets/{slug}.json` | Legacy flat test set (auto-migrated to `modules/default/` on first run) |
| `executions/{testSetId}/{runId}.json` | Execution history for each run |
| `logs/testrun_{timestamp}.log` | Full trace log of every run including HTTP request/response details |

All directories are created automatically next to the compiled binary. On first startup, existing `testsets/` files are auto-migrated into a "Default" module.

---

## Test Case Generation

The LLM generates test steps based on the **literal intent** of the objective:

| Objective style | What the LLM generates |
|---|---|
| **Specific** — exact endpoint, parameters, and validations (e.g. *"Test NMIDetails with NMI=123 and validate Meter Serial 456"*) | Exactly **1** test step matching the request |
| **Vague / open-ended** — no specific parameters or validations (e.g. *"Test the login API"*) | **3–5** reasonable steps covering obvious happy-path and error scenarios |
| **Comprehensive** — uses keywords like *"thorough"*, *"edge cases"*, *"boundary tests"* | Up to **8** steps including boundary and error variations |

Boundary tests, fuzzy matching, security checks, and error tests are **never** added unless the objective explicitly requests them.

Each step specifies:
- HTTP method and endpoint path
- Request headers, query parameters, and body
- Expected HTTP status code
- Strings expected to be present or absent in the response body

**Validation extraction** — When the objective mentions specific values to check (e.g. *"validate NMI property set to 6305824657"* or *"check there is a Meter Serial 444444"*), those values are included in `expectedBodyContains` automatically. The discovery call provides supplementary field names from the real API response, but user-requested validations always take priority.

The discovery call includes the full endpoint path and query parameters from the objective, so it captures a representative response sample for the exact request being tested.

---

## Validation

Each response goes through two stages:

1. **Rule-based checks** (fast, no LLM cost)
   - Status code match
   - Response body contains/not-contains assertions

2. **LLM-based deeper validation** (runs only if rule checks pass)
   - JSON well-formedness
   - Data reasonableness (not empty when populated data expected)
   - Field type correctness
   - Sensitive data exposure (passwords, tokens, internal paths)
   - Security headers (advisory notes only — not a failure condition)

If rule checks fail, the LLM validation is skipped to save tokens.

---

## Fail-Fast Behaviour

If more than 75% of test steps in the first API objective return HTTP 404 with zero passing steps, all subsequent API objectives are automatically skipped. This prevents burning LLM tokens when the configured API stack/module is wrong.

---

## Web Dashboard

AITestCrew includes a React-based web dashboard for browsing modules, managing test sets, viewing test cases, inspecting execution history, and triggering runs.

### Starting the Web UI

1. **Start the API server:**
   ```
   dotnet run --project src/AiTestCrew.WebApi
   ```
   Listens on `http://localhost:5050`.

2. **Start the React dev server:**
   ```
   cd ui
   npm run dev
   ```
   Opens at `http://localhost:5173`.

### Module List (Home Page)

The home page shows all modules as cards. Each card displays:
- Module name and description
- Number of test sets and total test cases
- Creation date

Click **+ Create Module** to add a new module.

### Module Detail

Shows the test sets within a module as a card grid. Features:
- **+ Test Set** button to create an empty test set
- **Run Objective** button to enter an objective, select a target test set, and trigger a Normal-mode run that merges generated tests into the selected set
- Each test set card shows name, objective count, step count, run count, and last run status
- **Search bar** — live text filter matching test set name, objective, or ID
- **Sort controls** — sort by Name, Last Run date, or Status with ascending/descending toggle
- **Status filter chips** — filter by Passed, Failed, Error, Running, or No runs. Passed and Failed chips are always visible; others appear when relevant test sets exist
- **Progressive loading** — initially renders up to 12 cards; additional cards load automatically as the user scrolls down

### Test Set Detail (Master-Detail)

The test set detail page uses a **master-detail** pattern:

- **Master list** — shows all Test Objectives (test cases) in the set. Each row displays:
  - Objective name (short name if set, full text as tooltip)
  - Step count (number of API calls or UI test cases)
  - Type (API / Web UI)
  - **Per-objective status** — each test case shows its own individual status from its most recent execution, not a shared test set status. If objective A passed in run 5 and objective B failed in run 6, A shows "Passed" and B shows "Failed".
  - **Per-objective last run date** — the date of each objective's most recent execution
  - **Run** button — triggers execution of just that single test case (Reuse mode with `objectiveId` filter). Shows a spinner while running and refreshes statuses on completion.
  - **Move** button to relocate the objective (and its steps) to a different test set/module
  - **Delete** button to remove the objective (`DELETE /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}`)
- **Test set level status badge** — computed as the worst-case aggregate across all individual objective statuses: if any objective is "Error" → Error, if any is "Failed" → Failed, otherwise "Passed".
- **Detail panel** — clicking an objective opens a panel showing:
  - Its steps (API test definitions or Web UI test definitions), each editable
  - Execution history for that objective
  - For API steps: HTTP method (colour-coded), endpoint, test name, expected status code
  - For Web UI steps: test name, start URL, step count, screenshot-on-failure flag
- **AI Edit Test Cases button** — opens the AI patch panel for natural language corrections (see [Editing Test Cases](#editing-test-cases))
- **Execution history** — all previous runs with status, pass/fail counts, duration, and date
- **Trigger buttons** — "Re-run Tests" (reuse mode) at the test set level; "Run" and "Rebaseline" per objective
- **Delete Test Set** button — permanently removes the test set and all execution history after confirmation

### Execution Detail

Shows a single run's results:
- Overall status, duration, and objective pass/fail counts (`totalObjectives`, `passedObjectives`, `failedObjectives`)
- LLM-generated summary
- Expandable objective sections, each showing individual test steps
- Click a step to expand its full response detail
- For failed Web UI steps: if `TakeScreenshotOnFailure` is enabled and `PlaywrightScreenshotDir` is configured, the failure detail includes an inline screenshot image and a "View Screenshot" link

### Triggering Runs from the UI

From a module detail page, click **Run Objective** to:
1. Select the **Environment** from the dropdown (populated from `GET /api/config/environments`) — defaults to the server's `DefaultEnvironment`
2. Select the **API Stack** and **API Module** from dropdown selectors (populated from `GET /api/config/api-stacks`)
3. Select a target test set
4. Enter a test objective (use paths relative to the module's base URL, e.g. `/NMIDiscoveryManagement/NMIDiscoveries`)
5. Optionally enter a **Short Name** for the objective (e.g. `"NMI Discovery GET"`) — displayed in place of the full text throughout the UI
6. The UI sends a POST to `/api/runs` with `moduleId`, `testSetId`, `apiStackKey`, `apiModule`, `environmentKey`, and optional `objectiveName`

Individual per-objective Run / Verify / Rebaseline buttons carry over the test set's persisted `environmentKey` automatically — no re-selection needed per click.
6. Shows a spinner while polling every 3 seconds
7. Automatically navigates to the results page when the run completes

From a test set detail page:
- **Re-run Tests** — re-executes all test cases in the set (Reuse mode). Each objective's status updates independently.
- **Run (per objective)** — the **Run** button on each objective row triggers execution of only that single objective. The API receives `objectiveId` in the run request, and the orchestrator filters to only that task. The objective's status and last run date update independently without affecting other objectives.
- **Rebaseline (per objective)** — the **Rebaseline** button appears only on AI-generated objectives (not on recorded ones). It shows a confirmation dialog before regenerating all test cases for that objective via LLM. Recorded objectives display a "Recorded" badge and only offer the Run action.

Each test objective tracks its origin via the `source` field: `"Generated"` for AI-created objectives or `"Recorded"` for objectives captured via the `--record` CLI command.

From a module detail page:
- **Run All** — triggers sequential execution of all test sets in the module (Reuse mode). Each test set runs in order; if a test set fails, execution continues to the next. A progress banner shows a segmented progress bar with per-test-set status (Pending / Running / Completed / Failed) and clickable links to each test set.

Only one run can be active at a time (the API returns 409 if another is in progress).

### Run Progress Persistence

Run progress is tracked globally across the UI, not in local component state. This means:
- Navigating away from a page and returning will still show progress for any active run
- The module list page shows a progress bar and "Running X/Y test sets..." on module cards during a module-level run
- The module detail page shows a segmented progress banner during a module-level run, and each test set card highlights when it is the currently executing test set
- After a page refresh, the UI recovers any active run by calling `GET /api/runs/active` on startup

### URL Routes

| Route | Page |
|---|---|
| `/` | Module list |
| `/modules/{moduleId}` | Module detail with test sets |
| `/modules/{moduleId}/testsets/{tsId}` | Test set detail |
| `/modules/{moduleId}/testsets/{tsId}/runs/{runId}` | Execution detail |
| `/testsets/{id}` | Legacy test set detail (backward compat) |
| `/testsets/{id}/runs/{runId}` | Legacy execution detail (backward compat) |

### Chat Assistant

Every page has an **Assistant** button in the header that opens a right-edge drawer. The assistant turns natural-language requests into actions the user confirms with a single click — no CLI flag lookup, no digging through URLs for module/test-set IDs.

**How it works:** each message is sent with the page's URL context (`moduleId`, `testSetId` if on a test-set page) and the client-held chat history to `POST /api/chat/message`. The server asks the LLM — with the full live catalog of modules, test sets, environments, API stacks, aseXML endpoints, and online agents in the system prompt — to emit a structured `ChatResponse { reply, actions }`. The UI renders the reply as an assistant bubble and each action below it as a card.

**Action kinds:**

| Kind | Card UI | Effect when user confirms |
|---|---|---|
| `navigate` | "Open /modules/foo" link | Router navigation (drawer closes) |
| `showData` | Inline table / bulleted list / JSON pretty-print | Read-only — data already resolved server-side |
| `confirmRun` | Resolved `RunRequest` (mode, module, test set, objective, env, stack) + Execute | `POST /api/runs`; `runId` pushed into `ActiveRunContext` so the existing run banner takes over |
| `confirmCreate` | Resolved payload for a new module or test set + Execute | `POST /api/modules` or `POST /api/modules/{id}/testsets` then auto-navigate to the new entity |
| `confirmRecord` | Resolved recording params + agent dropdown (online agents with matching capability) + Execute | `POST /api/recordings` enqueues the job. The card morphs into a live progress card that polls `/api/queue` and narrates Queued → Claimed → Running → Completed/Failed |

**What the assistant can do today:**

- **Discovery** — "list modules", "which environments are configured?", "show connected agents", "what endpoints are available?"
- **Navigation** — "open the MFN delivery test set", "go to the retail module"
- **Run triggers** — "reuse the MFN delivery against Sumo", "rebaseline the Deliver MFN objective", "verify-only for the latest delivery without waiting". Modes: Normal / Reuse / Rebaseline / VerifyOnly.
- **API test generation (Normal mode)** — "generate a test case for the SDR legacy API `api/v1/DataSourceManagement/DataSources` GET method". The assistant resolves the stack (`legacy` / `bravecloud`) and API module (e.g. `sdr`) from the user's words by matching against `catalog.apiStacks`, fills in the target module + test set (from page context or catalog match), and emits a `confirmRun` card with `mode=Normal`. Clicking Execute kicks off an in-process LLM run that generates fresh API test cases and saves them into the target test set. Normal mode is restricted to API targets — UI test generation still goes through recording.
- **Module / test-set creation** — "create a module called Smoke Tests", "add a test set 'nmi-loads' to the sdr module"
- **Recording dispatch** — "record a login for retail on Blazor", "record a verification for the MFN delivery on Legacy MVC", "do an auth-setup for AMS". The card's dropdown lists online agents that advertise the matching capability; clicking Execute enqueues the job for that agent via `POST /api/recordings`. The agent's Runner picks it up on its next poll and launches the interactive session on that machine's desktop.

**What it won't do:**

- Normal-mode generation for UI or aseXML targets — these still require the recorder (`confirmRecord`) or the relevant CLI flow.
- Mutations without a confirmation card — all creates, run triggers, and recording dispatches route through an Execute button.
- Recording when no matching agent is online — the assistant declines and suggests starting an agent with `dotnet run --project src/AiTestCrew.Runner -- --agent --name "MyPC"`.

**Catalog resolution:** the assistant only uses IDs, keys, and codes it finds literally in the server-provided catalog. Fuzzy phrases like "sumo" resolve to `sumo-retail` by name → substring → case-insensitive matching. If the request cannot be satisfied from the catalog, the assistant replies explaining why and emits no actions. When the user is on a test-set page, the catalog is enriched with that test set's `TestObjectives` so objective-level run scoping ("rebaseline the Deliver MFN objective") can resolve the objective id.

Chat history is held client-side only (in-memory, cleared on page refresh or via the **clear** button in the drawer header). There is no server-side persistence of transcripts.

---

## Deleting Test Sets

A test set can be deleted from the test set detail page by clicking the **Delete Test Set** button. This is a destructive action that:

1. Deletes all execution history (runs) for the test set
2. Deletes the test set file itself
3. Redirects back to the module detail page

A confirmation dialog is shown before deletion. This action cannot be undone.

**API:** `DELETE /api/modules/{moduleId}/testsets/{tsId}`

---

## Deleting Modules

A module can be deleted from the module detail page by clicking the **Delete Module** button. This is a cascading destructive action that:

1. Deletes all execution history (runs) for every test set in the module
2. Deletes every test set file in the module
3. Deletes the module directory / row itself
4. Redirects back to the module list

A confirmation dialog shows the module name and the number of test sets that will be removed. The button is disabled while a module-level run is in progress, and the API returns `409 Conflict` if a run is active at the moment of deletion. This action cannot be undone.

**API:** `DELETE /api/modules/{moduleId}`

---

## Moving Objectives

An objective (and all its associated steps) can be moved from one test set to another, including across modules. This is useful for reorganising test cases after they've been generated.

From the test set detail page, click the **Move** button next to any objective. A dialog allows you to select:
1. The destination module
2. The destination test set (within that module)

Moving an objective:
- Removes the objective and its steps from the source test set
- Appends them to the destination test set (deduplicating by objective ID)
- If the source test set has no remaining objectives after the move, it is deleted automatically
- Execution history is **not** moved — it stays with the original test set

**API:** `POST /api/modules/{moduleId}/testsets/{tsId}/move-objective`

Request body:
```json
{
  "objective": "Test the /api/products endpoint",
  "destinationModuleId": "target-module",
  "destinationTestSetId": "target-testset"
}
```

---

## Execution History

Every test run (regardless of mode) is automatically persisted to `executions/{testSetId}/{runId}.json`. This enables:
- Viewing run history in the web dashboard
- Comparing pass/fail trends across runs
- Inspecting detailed step-by-step results from past executions

History is saved in a try/catch wrapper — if persistence fails, the CLI output and test execution are unaffected.

### Automatic Retention (Pruning)

To prevent execution history from growing indefinitely, the system automatically prunes old runs after each new run is saved. Only the most recent N runs per test set are retained; older runs are deleted.

| Setting | Description | Default |
|---|---|---|
| `MaxExecutionRunsPerTestSet` | Maximum execution runs to keep per test set. `0` = no limit (keep all). | `10` |

The setting is configured in `appsettings.json` under `TestEnvironment`. Pruning happens transparently after every `SaveAsync` — the new run is always written to disk first, so even if pruning fails, no data is lost.

The run count displayed on test set cards and the CLI `--list` output reflects the actual number of execution run files on disk, not a historical total.

---

## Web UI Testing

AITestCrew can test web applications using Playwright. Two agents are available:

| Agent | Target Type | Application |
|---|---|---|
| **Legacy Web UI Agent** | `UI_Web_MVC` | Legacy ASP.NET MVC application — forms authentication |
| **Brave Cloud UI Agent** | `UI_Web_Blazor` | Brave Cloud UI (Blazor) — Azure OpenID SSO |

### Authoring Web UI Test Cases

Two authoring paths are available. LLM generation is an optional shortcut; **Recording Mode** gives fully deterministic, human-verified test cases with no LLM involvement.

---

#### Option 1 — LLM Generation (from an objective)

Write objectives in plain English exactly as you would for API tests:

```bash
# Legacy MVC app
dotnet run --project src/AiTestCrew.Runner -- \
  --module legacy --testset login \
  "Test the login page with valid credentials, wrong password, and a locked account"

# Brave Cloud UI (Blazor)
dotnet run --project src/AiTestCrew.Runner -- \
  --module bravecloud --testset dashboard \
  "Test that the dashboard loads correctly and shows the expected widgets after login"
```

The agent uses a **two-phase approach** to reduce hallucinated selectors:

1. **Exploration phase** — a browser opens, the LLM navigates with live Playwright tools (`snapshot`, `navigate`, `click`, `fill`), observing real page titles and URLs.
2. **Generation phase** — the LLM receives the observed page facts (actual URL + title per page visited) and generates JSON test cases. Assertion values are taken exclusively from what was observed — no invented values.

Credentials are never passed to or invented by the LLM. They are read from `LegacyWebUiUsername` / `LegacyWebUiPassword` in config and injected into the exploration prompt as authoritative values.

---

#### Option 2 — Recording Mode (recommended for reliability)

Record a test case by performing the scenario yourself in a real browser. No LLM is involved — selectors and values are captured directly from the DOM.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record \
  --module <moduleId> \
  --testset <testSetId> \
  --case-name "Happy path - valid credentials reach home" \
  --target UI_Web_MVC
```

| Flag | Purpose |
|---|---|
| `--record` | Activates recording mode |
| `--module` | Module ID or display name (slugified automatically) |
| `--testset` | Test set ID or display name (slugified automatically) |
| `--case-name` | Name for the new `WebUiTestDefinition` |
| `--target` | `UI_Web_MVC` (uses `LegacyWebUiUrl`) or `UI_Web_Blazor` (uses `BraveCloudUiUrl`) |

**What happens:**

1. A non-headless **maximized** Chromium window opens at the configured base URL.
2. An **overlay panel** appears in the **bottom-right corner** of the browser. If the page is still loading when it first appears, the overlay attaches automatically once the DOM is ready.
3. Interact with the page normally — every form fill (captured via `input` events as you type) and click (on buttons, links, menu items, tree nodes) is recorded with its CSS or Playwright `text=` selector and value. Keyboard events like **Escape** (for dismissing modals) are also captured.
4. Use the overlay buttons to add assertions at any point:
   - **+ Assert current URL (…)** — records an `assert-url-contains` step with the current path
   - **+ Assert page title (…)** — records an `assert-title-contains` step with the current title
   - **+ Assert element…** — enters **pick mode**: hover to highlight elements, click to select one, then choose an assertion type from the context menu:
     - **Assert text contains** — records `assert-text` with the element's visible text
     - **Assert value equals** — records `assert-text` with the form field's current value (shown only for input/textarea/select)
     - **Assert is visible** — records `assert-visible`
     - **Assert is hidden** — records `assert-hidden`
   - Press **Escape** to cancel pick mode at any time
   - Buttons update to show `✓` after being clicked to confirm the step was captured
5. Click **Save & Stop** to end the session.
6. The captured steps are printed to the console in a table and saved to the test set file.

The module directory and manifest are created automatically if they do not exist. Module/test set names are slugified before saving so they match the WebApi's directory structure.

**Example output:**
```
Saved 5 steps → standing-data-replication-sdr/test-recording
Replay: dotnet run -- --reuse test-recording
```

---

#### Editing Web UI Test Cases in the UI

Recorded or LLM-generated web UI test cases can be reviewed and edited directly in the web dashboard.

On the test set detail page, the **Web UI Tests** section shows a table with columns: Name, Start URL, Steps count, Screenshot on failure. Click any row to open the editor.

**Edit Web UI Test Case** dialog fields:
- **Name** and **Description**
- **Start URL** — relative path where the test begins (e.g. `/Account/Login`)
- **Take screenshot on failure** — checkbox
- **Steps** — ordered list, each with:
  - Action dropdown (see step types below)
  - Selector (disabled for actions that don't use one)
  - Value
  - Timeout (ms) — default 5 000 ms for fills, 15 000 ms for clicks
  - ↑ / ↓ reorder buttons, ✕ delete button
- **+ Add Step** — appends a new blank step
- **Delete Test Case** button (bottom-left) — removes the entire test case after inline confirmation

Changes are saved via `PUT /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}` (with step index in the request body).

---

### Playwright step types

| Action | Selector | Value | Description |
|---|---|---|---|
| `navigate` | — | URL path | Go to a URL |
| `click` | CSS selector | — | Click an element (15 s timeout; auto-dismisses modal overlays on failure; Force + JS fallback) |
| `fill` | CSS selector | text | Type text into an input (dispatches `input`, `change`, and `keyup` events for JS filter compatibility) |
| `type` | CSS selector | text | Type text character-by-character (fires `keydown`/`keypress`/`keyup` per key — use for search-as-you-type inputs) |
| `select` | CSS selector | option value | Choose a dropdown option |
| `check` / `uncheck` | CSS selector | — | Tick/untick a checkbox |
| `hover` | CSS selector | — | Hover over an element |
| `press` | CSS selector | key name | Press a keyboard key |
| `assert-text` | CSS selector | expected text | Assert element text contains a value. Set `matchFirst: true` to assert only against the first matching element (useful for grids with multiple rows). |
| `assert-visible` / `assert-hidden` | CSS selector | — | Assert element visibility. Supports `matchFirst`. |
| `assert-url-contains` | — | URL fragment | Assert the current URL contains a value |
| `assert-title-contains` | — | title fragment | Assert the page title contains a value |
| `wait` | CSS selector or — | ms (if no selector) | Wait for a selector or a fixed delay |
| `wait-for-stable` | — | ms threshold (default 1000) | Wait until DOM stops changing for N ms (uses MutationObserver — ideal after SPA navigation) |
| `click-icon` | — | SVG path prefix | Click an icon-only button by its SVG path fingerprint. Format: `svgPathPrefix\|N` where N is the 0-based occurrence index |

### Match-First Assertions

By default, assertion steps use Playwright's strict mode — the selector must resolve to exactly one element. When a selector matches multiple elements (e.g. grid rows accumulating over repeated test runs), the assertion fails with a strict-mode violation.

Setting `matchFirst: true` on an assertion step wraps the locator with `.First`, so only the first matching element (typically the newest row in a date-sorted grid) is checked. This flag applies to `assert-text`, `assert-visible`, and `assert-hidden` actions.

**Usage**: toggle the "first" checkbox in the Web UI edit dialog for the relevant assertion step. In JSON, set `"matchFirst": true` on the step object.

### Per-Step Execution Reporting

Web UI test execution reports each Playwright step individually in the execution detail view. For a test case with 9 steps, you will see 9 separate result entries (plus the load-cases and browser-launch infrastructure steps).

Each step is labelled as `"Test Case Name [N/Total] action"` (e.g. `"Happy path [5/9] click"`), showing:
- Pass/fail/error status
- The action, selector, and value
- Duration

When a step fails, remaining steps in that test case are marked as **"Skipped — previous step failed"** so you can see exactly which step broke and what was left untested.

### Failure Screenshots

When `TakeScreenshotOnFailure` is enabled on a test case and `PlaywrightScreenshotDir` is configured, a full-page screenshot is captured at the moment of failure. Screenshots are taken for all failure types: Playwright errors, assertion failures, and unexpected exceptions.

**How it works:**
1. The agent captures a PNG screenshot via Playwright and saves it to `PlaywrightScreenshotDir`.
2. The filename is stored in the step's `detail` field (e.g. `"Timeout exceeded | Screenshot: Happy_path_20260704_122709.png"`).
3. The WebApi serves the screenshot directory as static files at `/screenshots/{filename}`.
4. The UI parses the screenshot reference from the detail text and renders it as an inline image with a "View Screenshot" link.

### Configuration

Add these settings to `appsettings.json` under `TestEnvironment`:

```json
"PlaywrightBrowser":  "chromium",
"PlaywrightHeadless": true,
"PlaywrightScreenshotDir": "screenshots",

"LegacyWebUiUrl":      "https://your-legacy-app.example.com",
"LegacyWebUiLoginPath": "/Account/Login",
"LegacyWebUiUsername": "your-test-username",
"LegacyWebUiPassword": "your-test-password",

"BraveCloudUiUrl":                   "https://your-bravecloud-app.example.com",
"BraveCloudUiStorageStatePath":       "bravecloud-auth-state.json",
"BraveCloudUiUsername":               "test-account@yourdomain.com",
"BraveCloudUiPassword":               "your-aad-password",
"BraveCloudUiStorageStateMaxAgeHours": 8,
"BraveCloudUiTotpSecret":             ""
```

`PlaywrightScreenshotDir` is required for failure screenshots. If not set, screenshots are silently skipped. The WebApi serves this directory at the `/screenshots/` URL path.

### Authentication

**Legacy MVC** — the agent navigates to `LegacyWebUiLoginPath`, fills the username and password from config, and submits the login form. The session is maintained for the duration of the test run. In recording mode, authentication is left to the user — navigate and log in manually within the browser window.

**Brave Cloud UI (Azure SSO + 2FA)** — the first run performs a full SSO login via the Azure AD login page and saves the resulting browser auth state to `BraveCloudUiStorageStatePath`. Subsequent runs within `BraveCloudUiStorageStateMaxAgeHours` (default: 8 hours) reuse the saved state and skip the SSO flow entirely.

**MFA / TOTP support** (three modes):
1. **Fully automated** — set `BraveCloudUiTotpSecret` to the base32 TOTP shared secret. The agent computes and enters the 6-digit code automatically during SSO login.
2. **Semi-automated** — leave `BraveCloudUiTotpSecret` empty and set `PlaywrightHeadless: false`. The agent fills email/password automatically, then pauses up to 120 seconds for you to enter the MFA code manually in the visible browser.
3. **Manual auth setup** — run `--auth-setup` to open a visible browser, complete the full SSO + 2FA flow manually, and save the auth state. Recordings and test runs then reuse this saved state.

To obtain the TOTP secret: sign in to https://mysignins.microsoft.com/security-info, add a new Authenticator app, and click "Can't scan image?" to reveal the base32 key. If the account already has an authenticator configured, an Azure AD admin can reset MFA to re-enrol and capture the secret.

### Browser installation

Playwright requires browser binaries to be installed once. Run this after first checkout or package update:

```bash
pwsh -Command playwright install chromium
```

---

## Desktop UI Testing (WinForms)

The `WinFormsUiTestAgent` tests Windows Forms desktop applications using FlaUI (UI Automation 3). It supports both LLM-driven test generation and human-driven recording, matching the web UI testing experience.

### Two ways to create desktop tests

**Option 1 — LLM Generation**: Provide a natural language objective and the agent explores the application via the UI Automation tree, then generates test cases automatically.

```bash
dotnet run --project src/AiTestCrew.Runner -- --module desktop --testset calc --target UI_Desktop_WinForms "Verify the main form loads and displays the title bar"
```

**Option 2 — Recording Mode (recommended for complex apps like Bravo)**: The recorder launches the application, captures your interactions via Windows hooks, and saves them as deterministic test steps.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record --module sdr --testset cats-search --case-name "NMI Search" --target UI_Desktop_WinForms
```

### Recording process

1. The target application launches (using `WinFormsAppPath` from config, with working directory set to the exe's folder so sibling DLLs are found)
2. You interact with the application normally — clicks, typing, and paste (Ctrl+V) are captured automatically
3. Use **console keys** to add assertions:
   - **T** — Assert Text: click the target element to capture its text content
   - **V** — Assert Visible: click the target element to verify it's visible
   - **E** — Assert Enabled: click the target element to verify it's enabled
   - **S** — Save & Stop: end recording and save all captured steps
4. The recorder shows a summary table of captured steps and saves them to the test set

### What the recorder captures

- **Mouse clicks** — Captured via `WH_MOUSE_LL` Windows hook, resolved to UI Automation elements via `automation.FromPoint()`. The recorder automatically filters out:
  - Window chrome (title bar, scroll bars, resize grips)
  - System UI elements (taskbar buttons, notification area, shell tray)
- **Keyboard input** — Captured via `WH_KEYBOARD_LL` hook. Consecutive keystrokes are coalesced into `fill` steps. Special keys (Enter, Tab, Escape) are recorded as `press` steps
- **Clipboard paste (Ctrl+V)** — Detected as a modifier key combination. The recorder reads the actual clipboard content and records it as the fill value, rather than just "V"
- **Element selectors** — A composite selector is built for each element with cascading priority:
  1. `AutomationId` — most stable (maps to `Control.Name` in WinForms code)
  2. `Name` — visible text/label of the control
  3. `ClassName` + `ControlType` — used only when unambiguous (single match). If multiple elements share the same class and type (e.g. several text boxes on a form), falls through to TreePath
  4. `TreePath` — positional indexed path from window root (e.g. `Edit[3]`), distinguishes sibling controls of the same type

### Desktop step actions

| Action | What it does |
|---|---|
| `click` | Click an element — tries InvokePattern first (reliable for ToolStrip/ribbon buttons), then coordinate click, then Mouse.Click fallback |
| `double-click` | Double-click an element |
| `right-click` | Right-click an element |
| `fill` | Type text into an input — uses ValuePattern, falls back to keyboard focus + type |
| `select` | Pick an item from a ComboBox by text |
| `check` / `uncheck` | Toggle a CheckBox via TogglePattern |
| `press` | Send a keyboard key (Enter, Tab, Escape, etc.) |
| `hover` | Move mouse to element's clickable point |
| `assert-text` | Assert element's text contains expected value — **polls every 500ms** until text matches or timeout expires (handles async operations like search completion) |
| `assert-visible` / `assert-hidden` | Assert element visibility (IsOffscreen property) |
| `assert-enabled` / `assert-disabled` | Assert element enabled state |
| `wait-for-window` | Wait for a window with matching title to appear |
| `switch-window` | Set focus to a window matching title |
| `close-window` | Close a window matching title |
| `menu-navigate` | Walk a MenuBar chain (e.g. "File > Save As") |
| `wait` | Wait for element to appear or fixed delay |

### Element resolution during replay

During replay, the step executor searches for elements across progressively wider scopes:

1. **Primary window** — fastest path for most elements
2. **All top-level app windows** — catches MDI child forms
3. **Desktop root** — last resort, catches deeply nested MDI children and floating toolbars

For assertions, a **quick non-retrying search** is used in a polling loop (every 500ms) so the text can be re-read as it changes (e.g. "Search is in progress" transitioning to "Search completed").

When a click triggers a window transition (dialog closes, new form opens), the executor automatically detects the window count change and waits 1.5s for the app to settle.

### Text extraction for assertions

When recording an `assert-text` step, the recorder extracts text from the clicked element by searching:
1. ValuePattern on the element (text boxes, editable controls)
2. Name property (labels, buttons, tree items)
3. Immediate child elements (handles containers like Pane/Group)
4. All descendant Text and Edit controls (deeply nested layouts)

The same thorough extraction runs during replay, so assertions work even when the text is inside a child element of the clicked container.

### Configuration

| Setting | Default | Description |
|---|---|---|
| `WinFormsAppPath` | `""` | Full path to the .exe under test |
| `WinFormsAppArgs` | `null` | Optional command-line arguments |
| `WinFormsAppLaunchTimeoutSeconds` | `30` | How long to wait for the main window to appear |
| `WinFormsScreenshotDir` | `null` | Directory for failure screenshots (falls back to `PlaywrightScreenshotDir`) |
| `WinFormsCloseAppBetweenTests` | `true` | Relaunch app for clean state between test cases |

### Editing recorded steps

Desktop test steps can be edited in the React web portal just like web UI steps. The edit dialog shows:
- Action dropdown with all 19 desktop actions
- Five cascading selector fields (AutomationId, Name, ClassName, ControlType, TreePath)
- Context-specific fields: Value, MenuPath (for menu-navigate), WindowTitle (for window actions)
- Add, remove, reorder steps; save and delete operations

---

## Planned Future Capabilities

The following are scaffolded in the codebase but not yet active:

| Capability | Status |
|---|---|
| Parallel objective execution | **Implemented** — `MaxParallelAgents` controls concurrency (default 4). Applies to both objectives within a test set and test sets within a module "Run All". |
| Objective dependency ordering | `DependsOn` field on `TestTask` exists; not yet enforced |
| UI testing — WinForms | **Implemented** — `WinFormsUiTestAgent` uses FlaUI (UI Automation) for both LLM-driven generation and recorded replay. Desktop recorder captures via Windows hooks. |
| Background job testing (Hangfire) | Target type defined; no agent implemented |
| Message bus testing | Target type defined; no agent implemented |
| Database validation | Target type defined; no agent implemented |
| Retry and adaptive re-planning | Planned |
| aseXML Generate → Deliver → Verify | **Implemented** — all three phases (Phase 1: template-driven generation; Phase 2: SFTP/FTP delivery with Bravo DB endpoint lookup + optional zip wrap; Phase 3: post-delivery UI verification with `{{Token}}` substitution, auto-parameterised recording, view/edit/delete UI) |
| aseXML wait strategy beyond fixed delay | Field `WaitBeforeSeconds` ships; richer strategies (SFTP-pickup poll, Bravo DB status poll) planned |
| Desktop verification edit dialog | View + delete shipped in the web UI; structured edit dialog deferred (delete-and-re-record today) |
| Aggregate reporting / dashboards | UI shows per-objective status + run history; aggregated cross-module views planned |

---

## Command reference (alphabetical)

Every flag the Runner CLI accepts, one row per flag. Scope column shows which run modes / commands use it.

| Flag | Scope | Value | Purpose |
|---|---|---|---|
| `--api-module <key>` | Normal, Reuse | e.g. `sdr`, `security` | Pick a module within the active API stack (`ApiStacks.<stack>.Modules.<key>`). Persists on the test set. |
| `--auth-setup` | Auth | (none) | Launch a browser for manual login; save storage state to the env-specific path. Combine with `--target` and `--environment`. |
| `--environment <key>` | Every run + Recording + Auth | e.g. `sumo-retail`, `ams-metering` | Customer environment. Picks URLs, creds, WinForms app path, Bravo DB connection, per-stack BaseUrls, and cached auth-state filenames. Persists on the test set. Omit for `DefaultEnvironment`. |
| `--case-name "<name>"` | Record, Record-setup | string | Display name for the captured test case or setup. |
| `--create-module "<name>"` | Management | string | Create a module directory + manifest. Name is slugified for the id. |
| `--create-testset <moduleId> "<name>"` | Management | slug + string | Create an empty test set in a module. |
| `--delivery-step-index <n>` | Record-verification | int, default 0 | Which delivery case inside the objective to attach to (objectives rarely have more than one, so usually omit). |
| `--endpoint <EndPointCode>` | Delivery (Normal/Reuse/Rebaseline) | e.g. `GatewaySPARQ` | Bravo outbound endpoint from `mil.V2_MIL_EndPoint`. Overrides LLM extraction; persists on the test set. |
| `--list` | List | (none) | List legacy-flat test sets. |
| `--list-endpoints` | List | (none) | Query Bravo DB and print all `EndPointCode`s. Requires `AseXml.BravoDb.ConnectionString`. |
| `--list-environments` | List | (none) | Print configured customer environments (from `TestEnvironment.Environments`) with the default marked. |
| `--list-modules` | List | (none) | List all modules and their test-set counts. |
| `--migrate-to-sqlite` | Migration | (none) | Reads all JSON files from the current data directory and inserts them into SQLite. Requires `SqliteConnectionString` to be configured. |
| `--module <moduleId>` | Every run | slug | Module scope. Required for module-scoped runs and nearly all recording. |
| `--obj-name "<name>"` | Normal, Rebaseline | string | Short display name for the objective (otherwise the full objective text is used). |
| `--objective <idOrName>` | Reuse, Record-verification | slug OR display name | Reuse mode: scope run to a single test case. Record-verification: target delivery objective. Case-insensitive; matches `TestObjective.Id` first, falls back to `TestObjective.Name`. |
| `--rebaseline` | Rebaseline | flag | Regenerate saved test cases via LLM and overwrite. AI-generated objectives only (recorded ones refuse). |
| `--record` | Recording | flag | Launch the matching recorder (Playwright for web, DesktopRecorder for WinForms). Requires `--module`, `--testset`, `--case-name`, `--target`. |
| `--record-setup` | Recording | flag | Record reusable setup steps (e.g. login) at the test-set level. Steps run before every test case at replay. |
| `--record-verification` | Recording | flag | Record a post-delivery UI verification attached to a delivery objective. Requires `--module`, `--testset`, `--objective`, `--target`, `--verification-name`. |
| `--reuse <testSetId>` | Reuse | slug | Replay a saved test set. With `--module`, `--testset` is auto-derived from this id. |
| `--stack <key>` | Normal, Reuse | e.g. `bravecloud`, `legacy` | API stack key from `TestEnvironmentConfig.ApiStacks`. Persists on the test set. |
| `--target <type>` | Recording | `UI_Web_MVC` \| `UI_Web_Blazor` \| `UI_Desktop_WinForms` | UI surface for the recording. For `--auth-setup`, determines which auth flow / storage state is captured. |
| `--testset <testSetId>` | Every module-scoped run | slug | Test set scope. Auto-derived from `--reuse` when module is scoped without this. |
| `--verification-name "<name>"` | Record-verification | string | Display label for the recorded verification. |
| `--wait <seconds>` | Record-verification | int, default = `AseXml.DefaultVerificationWaitSeconds` (30) | Delay between delivery and this verification at playback. |
| `--skip-teardown` | Reuse | flag | Bypass test-set data teardown for this run (SQL DELETE statements are not executed). |
| `--teardown-dry-run` | Reuse | flag | Log substituted teardown SQL at Information but don't execute. Useful for sanity-checking tokens before letting real DELETEs run. |

### Environment prerequisites by command

| Command | Needs in `appsettings.json` |
|---|---|
| Any run (LLM) | `LlmProvider`, `LlmApiKey`, `LlmModel` |
| API tests | `ApiStacks.<stack>.BaseUrl` + `.Modules`; per-env override via `Environments.<env>.ApiStackBaseUrls.<stack>`; optional `AuthToken` / `AuthUsername` / `AuthPassword` |
| Legacy MVC UI tests | `LegacyWebUiUrl`, `LegacyWebUiUsername`, `LegacyWebUiPassword`, `LegacyWebUiStorageStatePath` — all per-env in `Environments.<env>.*` (falls back to top-level) |
| Blazor UI tests | `BraveCloudUiUrl`, `BraveCloudUiUsername`, `BraveCloudUiPassword`, `BraveCloudUiStorageStatePath`, optional `BraveCloudUiTotpSecret` — all per-env (falls back to top-level) |
| WinForms UI tests | `WinFormsAppPath`, `WinFormsAppArgs` — per-env (falls back to top-level) |
| aseXML Generate | `AseXml.TemplatesPath` (default `templates/asexml`), `AseXml.OutputPath` |
| aseXML Deliver / `--list-endpoints` / `GET /api/config/endpoints` | `BravoDbConnectionString` per-env (falls back to top-level `AseXml.BravoDb.ConnectionString`) — never committed to `appsettings.example.json`. The WebApi endpoint returns an empty list with an `error` hint when the DB is unreachable, so the chat catalog degrades gracefully. |
| aseXML verification recording | Same as the matching UI target above; tip: run `--auth-setup --target <UI_*> --environment <envKey>` first to cache per-env auth state |
| Multi-environment | `DefaultEnvironment`, `Environments.<key>.*` — omit to fall back to legacy single-env behaviour using top-level fields |
