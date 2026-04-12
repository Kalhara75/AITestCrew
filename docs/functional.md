# AITestCrew — Functional Documentation

## Overview

AITestCrew is an AI-powered test automation tool that uses a large language model (LLM) to generate, execute, and validate API tests from plain English objectives. Instead of writing test scripts manually, you describe what you want to test in natural language and the tool does the rest.

---

## What It Does

1. **Understands your objective** — You provide a plain English description of what to test (e.g. *"Test the /api/products endpoint"*).
2. **Decomposes the objective** — The LLM breaks it into specific, actionable test tasks.
3. **Generates test steps** — The LLM interprets the objective literally and generates only the test steps it asks for. A specific objective (e.g. *"call X with params Y and validate Z"*) produces exactly one test. Vague objectives produce 3–5 steps; objectives that explicitly request comprehensive coverage produce up to 8. Assertions are grounded in values from the objective text and actual response field names discovered from a live pre-flight call.
4. **Executes the tests** — Each test case is sent as a real HTTP request to the target API.
5. **Validates responses** — A two-stage validation process (rule-based + LLM reasoning) checks status codes, response bodies, and data quality.
6. **Reports results** — A summary table and an LLM-written narrative are printed to the console. Everything is also written to a timestamped log file.
7. **Saves test sets** — Generated test cases are persisted to disk so they can be re-executed repeatedly without calling the LLM again.

---

## Available Agents

AITestCrew routes each test task to a specialised agent based on the task's `TestTargetType`. The orchestrator calls `CanHandleAsync` on each registered agent and dispatches to the first match.

| Agent | Target Type(s) | What it does |
|---|---|---|
| **API Agent** (`ApiTestAgent`) | `API_REST`, `API_GraphQL` | LLM-generated REST/GraphQL test cases — endpoint discovery, HTTP execution, hybrid rule-based + LLM response validation |
| **Brave Cloud UI Agent** (`BraveCloudUiTestAgent`) | `UI_Web_Blazor` | Playwright-based Blazor web UI testing with Azure AD SSO + TOTP/MFA authentication |
| **Legacy Web UI Agent** (`LegacyWebUiTestAgent`) | `UI_Web_MVC` | Playwright-based legacy ASP.NET MVC web UI testing with forms authentication and StorageState caching |
| **WinForms Desktop UI Agent** (`WinFormsUiTestAgent`) | `UI_Desktop_WinForms` | FlaUI-based Windows Forms desktop application testing — LLM-driven exploration via UI Automation tree, recording via Windows hooks, deterministic replay |

All agents extend `BaseTestAgent`, which provides shared LLM communication (`AskLlmAsync`, `AskLlmForJsonAsync`). The two UI agents share an additional base class (`BaseWebUiTestAgent`) for Playwright browser lifecycle, step execution, and recording infrastructure.

```
BaseTestAgent                          ← LLM helpers
  ├── ApiTestAgent                     ← REST / GraphQL
  └── BaseWebUiTestAgent               ← Playwright shared infra (abstract)
        ├── BraveCloudUiTestAgent      ← Blazor (UI_Web_Blazor)
        └── LegacyWebUiTestAgent       ← Legacy MVC (UI_Web_MVC)
```

The following target types are defined but do not yet have agent implementations: `Background_Hangfire`, `MessageBus`, `Database`.

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

### Editing Test Cases

Test cases can be reviewed and modified after generation via two methods:

**Direct edit** — Click any test objective in the test set detail page, then click a specific step to open an editor. All fields are editable: name, method, endpoint, headers, query params, body, expected status, and body assertions. The edit dialog accepts a `stepIndex` to target a specific step within the objective.

**Delete individual steps** — Each step row in the test case table has an inline delete icon (trash). Click it, confirm with Yes/No, and only that step is removed from the objective. The "Delete Step" button in the edit dialog works the same way. If you delete the last remaining step in an objective, the entire objective is removed. This uses the existing PUT endpoint — the step is removed from the array client-side and the modified objective is saved.

**API endpoint:** `PUT /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}` (full objective body in request).

**AI edit (natural language patch)** — Click the **AI Edit Test Cases** button above the test case table. Describe the change in plain English (e.g., *"remove the /api/v1 prefix from all endpoints"* or *"change expectedStatus to 200 for the happy path test"*). The system uses the LLM to generate a preview of changes, which you can review field-by-field before applying. Scope can be set to all objectives or a specific objective.

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

- No LLM calls during test generation — uses saved `ApiTestDefinition` steps directly.
- LLM is still used for response validation and the final summary.
- `RunCount` and `LastRunAt` are updated in the saved file after each reuse run.

---

### Rebaseline
Regenerate test cases from scratch via LLM (fresh set), overwrite the saved test set, and execute. Use this when the API has changed and you want new tests.

```
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "Test the /api/products endpoint"
```

Or module-scoped:
```
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset controlled-loads --rebaseline "Test the /api/ControlledLoadDecodes endpoint"
```

---

### Record (Web UI only)

Record a Web UI test case by interacting with a real browser. The CLI opens a non-headless Chromium window and captures every form fill and button click as exact `WebUiTestDefinition` steps with verified DOM selectors.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record \
  --module <moduleId> \
  --testset <testSetId> \
  --case-name "Happy path - valid credentials reach home" \
  --target UI_Web_MVC
```

- `--target` is `UI_Web_MVC` (uses `LegacyWebUiUrl`) or `UI_Web_Blazor` (uses `BraveCloudUiUrl`).
- Module and test set are created automatically if they do not exist.
- Module/test set names are slugified so they match the WebApi directory structure.
- For Blazor targets, the recorder uses a 1920×1080 viewport (matching replay) and loads saved auth state if available.
- The session ends when you click **Save & Stop** in the overlay, close the browser, or after 15 minutes.
- After recording, the tool validates steps and warns about weak selectors, missing assertions, and SPA timing risks.

See [Web UI Testing — Recording Mode](#option-2--recording-mode-recommended-for-reliability) for full details.

### Record Setup Steps (Web UI only)

Record reusable setup steps (e.g. login) that run automatically before every test case in a test set. This avoids duplicating login steps in each test and lets you change credentials in one place.

```bash
dotnet run --project src/AiTestCrew.Runner -- --record-setup \
  --module <moduleId> \
  --testset <testSetId> \
  --target UI_Web_MVC
```

- Opens a browser — perform your login/setup actions, then click **Save & Stop**.
- The recorded steps are saved as the test set's `setupSteps` (not as a test objective).
- During replay (`--reuse`), setup steps run before each test case in a fresh browser context: navigate to `setupStartUrl`, execute setup steps, then navigate to the test case's own start URL and execute its steps.
- Setup steps can also be viewed, edited, and cleared in the web dashboard under the test set detail page.
- If a setup step fails during replay, the remaining setup steps and all test case steps are skipped for that test case.
- Running `--record-setup` again for the same test set replaces the existing setup steps.

### Auth Setup

Save browser authentication state so that subsequent test runs start pre-authenticated, skipping the login flow entirely. Opens a visible browser — complete the login manually, and the session is saved automatically.

**Blazor SSO (default):**
```bash
dotnet run --project src/AiTestCrew.Runner -- --auth-setup
```

- Navigates to `BraveCloudUiUrl` which redirects to Azure AD login.
- Complete the full login flow (including 2FA if required) in the visible browser.
- Once redirected back to the app, the auth state (cookies + localStorage) is saved to `BraveCloudUiStorageStatePath`.
- The saved state is valid for `BraveCloudUiStorageStateMaxAgeHours` (default 8). Re-run `--auth-setup` when it expires.
- If `BraveCloudUiTotpSecret` is configured (base32 TOTP secret), the agent can automate 2FA during test runs — `--auth-setup` is only needed for initial or expired sessions.

**Legacy ASP.NET MVC:**
```bash
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC
```

- Navigates to `LegacyWebUiUrl` + `LegacyWebUiLoginPath` and opens the login form.
- Complete the login in the visible browser.
- Once redirected away from the login page, the auth state is saved to `LegacyWebUiStorageStatePath`.
- The saved state is valid for `LegacyWebUiStorageStateMaxAgeHours` (default 8).
- The state is also generated **automatically** on the first headless test run — `--auth-setup` is only needed if you prefer a manual interactive login.
- Under parallel execution, the first agent to run acquires a login lock, performs one headless login, and saves the state. All other concurrent agents reuse it.

---

### List
Display all saved test sets and exit.

```
dotnet run --project src/AiTestCrew.Runner -- --list
```

Output includes: ID (slug), module, objective, objective count, total step count, created date, last run date, and total run count.

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
- **Trigger buttons** — "Re-run Tests" (reuse mode) and "Rebaseline" (regenerate tests)
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
1. Select the **API Stack** and **API Module** from dropdown selectors (populated from `GET /api/config/api-stacks`)
2. Select a target test set
3. Enter a test objective (use paths relative to the module's base URL, e.g. `/NMIDiscoveryManagement/NMIDiscoveries`)
4. Optionally enter a **Short Name** for the objective (e.g. `"NMI Discovery GET"`) — displayed in place of the full text throughout the UI
5. The UI sends a POST to `/api/runs` with `moduleId`, `testSetId`, `apiStackKey`, `apiModule`, and optional `objectiveName`
6. Shows a spinner while polling every 3 seconds
7. Automatically navigates to the results page when the run completes

From a test set detail page:
- **Re-run Tests** — re-executes all test cases in the set (Reuse mode). Each objective's status updates independently.
- **Rebaseline** — regenerates all test cases via LLM and re-executes.
- **Run (per test case)** — the **Run** button on each test case row triggers execution of only that single objective. The API receives `objectiveId` in the run request, and the orchestrator filters to only that task. The test case's status and last run date update independently without affecting other test cases.

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

---

## Deleting Test Sets

A test set can be deleted from the test set detail page by clicking the **Delete Test Set** button. This is a destructive action that:

1. Deletes all execution history (runs) for the test set
2. Deletes the test set file itself
3. Redirects back to the module detail page

A confirmation dialog is shown before deletion. This action cannot be undone.

**API:** `DELETE /api/modules/{moduleId}/testsets/{tsId}`

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
| `assert-text` | CSS selector | expected text | Assert element text contains a value |
| `assert-visible` / `assert-hidden` | CSS selector | — | Assert element visibility |
| `assert-url-contains` | — | URL fragment | Assert the current URL contains a value |
| `assert-title-contains` | — | title fragment | Assert the page title contains a value |
| `wait` | CSS selector or — | ms (if no selector) | Wait for a selector or a fixed delay |
| `wait-for-stable` | — | ms threshold (default 1000) | Wait until DOM stops changing for N ms (uses MutationObserver — ideal after SPA navigation) |
| `click-icon` | — | SVG path prefix | Click an icon-only button by its SVG path fingerprint. Format: `svgPathPrefix\|N` where N is the 0-based occurrence index |

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
| Retry and adaptive re-planning | Planned for Phase 2 |
