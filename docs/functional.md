# AITestCrew — Functional Documentation

## Overview

AITestCrew is an AI-powered test automation tool that uses a large language model (LLM) to generate, execute, and validate API tests from plain English objectives. Instead of writing test scripts manually, you describe what you want to test in natural language and the tool does the rest.

---

## What It Does

1. **Understands your objective** — You provide a plain English description of what to test (e.g. *"Test the /api/products endpoint"*).
2. **Decomposes the objective** — The LLM breaks it into specific, actionable test tasks.
3. **Generates test cases** — For each task the LLM creates 5–8 concrete HTTP test cases: happy path, boundary, and error scenarios, using actual response field names discovered from a live pre-flight call.
4. **Executes the tests** — Each test case is sent as a real HTTP request to the target API.
5. **Validates responses** — A two-stage validation process (rule-based + LLM reasoning) checks status codes, response bodies, and data quality.
6. **Reports results** — A summary table and an LLM-written narrative are printed to the console. Everything is also written to a timestamped log file.
7. **Saves test sets** — Generated test cases are persisted to disk so they can be re-executed repeatedly without calling the LLM again.

---

## Modules and Test Sets

Tests are organised into **Modules** and **Test Sets**:

- **Module** — a top-level grouping representing an application area (e.g. "Standing Data Replication (SDR)").
- **Test Set** — a user-defined container within a module. Test cases from multiple objectives accumulate in a test set over time.

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

**Direct edit** — Click any test case row in the test set detail page to open an editor. All fields are editable: name, method, endpoint, headers, query params, body, expected status, and body assertions.

**AI edit (natural language patch)** — Click the **AI Edit Test Cases** button above the test case table. Describe the change in plain English (e.g., *"remove the /api/v1 prefix from all endpoints"* or *"change expectedStatus to 200 for the happy path test"*). The system uses the LLM to generate a preview of changes, which you can review field-by-field before applying. Scope can be set to all test cases or a specific task.

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

In module-scoped mode, generated test cases are **merged** into the target test set. Running another objective against the same test set accumulates tests.

Output:
- Executes 5–8 LLM-generated test cases per task.
- Saves/merges test cases into the target test set.
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

- No LLM calls during test generation — uses saved `ApiTestCase` objects directly.
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

Record a Web UI test case by interacting with a real browser. The CLI opens a non-headless Chromium window and captures every form fill and button click as exact `WebUiTestCase` steps with verified DOM selectors.

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
- The session ends when you click **Save & Stop** in the overlay, close the browser, or after 15 minutes.

See [Web UI Testing — Recording Mode](#option-2--recording-mode-recommended-for-reliability) for full details.

---

### List
Display all saved test sets and exit.

```
dotnet run --project src/AiTestCrew.Runner -- --list
```

Output includes: ID (slug), module, objective, task count, total test case count, created date, last run date, and total run count.

---

## Configuration

All settings are in `src/AiTestCrew.Runner/appsettings.json` under the `TestEnvironment` section.

| Setting | Description | Default |
|---|---|---|
| `LlmProvider` | `"Anthropic"` or `"OpenAI"` | `"OpenAI"` |
| `LlmApiKey` | API key for the LLM provider | *(required)* |
| `LlmModel` | Model identifier | `"gpt-4o"` |
| `BaseUrl` | Root URL of the target application | `"https://localhost:5001"` |
| `ApiBaseUrl` | Base URL prepended to all API endpoint paths | `"https://localhost:5001/api"` |

> **Important — writing objectives with the right path**: Endpoint paths in test cases are generated relative to `ApiBaseUrl`. If your `ApiBaseUrl` is `.../sdrapi/api/v1`, write objectives using paths that start after that prefix:
> - Correct: `"Test GET /NMIDiscoveryManagement/NMIDiscoveries"`
> - Incorrect: `"Test GET /api/v1/NMIDiscoveryManagement/NMIDiscoveries"` ← duplicates the version prefix
>
> Avoid pasting full URLs into objectives — they will include the base path the LLM will also prepend.
| `OpenApiSpecUrl` | Optional URL to an OpenAPI/Swagger JSON spec | `null` |
| `AuthToken` | Token injected into every request | `null` |
| `AuthScheme` | `"Bearer"`, `"Basic"`, or `"None"` | `"Bearer"` |
| `AuthHeaderName` | Header name for auth | `"Authorization"` |
| `DefaultTimeoutSeconds` | Per-task execution timeout | `300` |
| `VerboseLogging` | Show agent-level log lines in console | `true` |

### Authentication

Authentication is injected automatically from config. The LLM is instructed not to add auth headers itself, so tests focus on functional behaviour rather than auth scenarios.

- **Bearer token**: Set `AuthToken` to a JWT, `AuthScheme` to `"Bearer"`, `AuthHeaderName` to `"Authorization"`.
- **API key header**: Set `AuthScheme` to `"None"`, `AuthHeaderName` to `"X-Api-Key"`, `AuthToken` to the key value.
- **No auth**: Leave `AuthToken` empty — the LLM will generate auth failure tests (401/403) as part of the test suite.

### OpenAPI Spec

If `OpenApiSpecUrl` is set, the spec is fetched before test case generation and passed to the LLM for additional context. This is optional — without it, the tool uses live endpoint discovery to infer the response shape.

---

## Output

### Console results table

| Column | Description |
|---|---|
| Task ID | 8-character unique identifier for the task |
| Agent | Which agent executed the task |
| Status | `Passed` / `Failed` / `Error` / `Skipped` |
| Steps | `{passed}/{total}` test cases |
| Summary | LLM-generated one-line summary of the task result |

### Overall result line

```
Overall: PASSED (3/3 tasks) in 01:42
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
| `modules/{moduleId}/{testSetId}.json` | Module-scoped test set (tasks + test cases + run metadata) |
| `testsets/{slug}.json` | Legacy flat test set (auto-migrated to `modules/default/` on first run) |
| `executions/{testSetId}/{runId}.json` | Execution history for each run |
| `logs/testrun_{timestamp}.log` | Full trace log of every run including HTTP request/response details |

All directories are created automatically next to the compiled binary. On first startup, existing `testsets/` files are auto-migrated into a "Default" module.

---

## Test Case Generation

For each task, the LLM generates a mix of:

- **Happy path** — valid inputs, expected success responses
- **Boundary cases** — empty strings, zero values, maximum lengths
- **Error cases** — missing required fields, invalid types, wrong data

Each test case specifies:
- HTTP method and endpoint path
- Request headers, query parameters, and body
- Expected HTTP status code
- Strings expected to be present or absent in the response body

Field name assertions are grounded in a real discovery call made before generation, so the LLM uses actual response field names rather than guessing.

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

If more than 75% of test steps in the first API task return HTTP 404 with zero passing steps, all subsequent API tasks are automatically skipped. This prevents burning LLM tokens when the configured `ApiBaseUrl` is wrong.

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
- Each test set card shows name, objective count, test case count, run count, and last run status

### Test Set Detail

Shows the full test set with:
- **Objectives list** — all objectives that have contributed test cases. Objectives with a short name display that name with the full text as a tooltip. Each has a **Move** button to relocate the objective (and its tasks) to a different test set/module.
- **AI Edit Test Cases button** — opens the AI patch panel for natural language corrections (see [Editing Test Cases](#editing-test-cases))
- **Test cases table** — adapts to the type of test cases in the set:
  - *API tests* — HTTP method (colour-coded), endpoint, test name, expected status code. Click any row to open the direct editor.
  - *Web UI tests* — test name, start URL, step count, screenshot-on-failure flag. Click any row to open the Web UI step editor. If both API and UI tasks exist in the same test set, both tables are shown with labels.
- **Execution history** — all previous runs with status, pass/fail counts, duration, and date
- **Trigger buttons** — "Re-run Tests" (reuse mode) and "Rebaseline" (regenerate tests)
- **Delete Test Set** button — permanently removes the test set and all execution history after confirmation

### Execution Detail

Shows a single run's results:
- Overall status, duration, and task pass/fail counts
- LLM-generated summary
- Expandable task sections, each showing individual test steps
- Click a step to expand its full response detail

### Triggering Runs from the UI

From a module detail page, click **Run Objective** to:
1. Select a target test set
2. Enter a test objective (use paths relative to `ApiBaseUrl`, e.g. `/NMIDiscoveryManagement/NMIDiscoveries`)
3. Optionally enter a **Short Name** for the objective (e.g. `"NMI Discovery GET"`) — displayed in place of the full text throughout the UI
4. The UI sends a POST to `/api/runs` with `moduleId`, `testSetId`, and optional `objectiveName`
5. Shows a spinner while polling every 3 seconds
6. Automatically navigates to the results page when the run completes

From a test set detail page, click "Re-run Tests" or "Rebaseline" to re-execute or regenerate tests.

Only one run can be active at a time (the API returns 409 if another is in progress).

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

An objective (and all its associated tasks and test cases) can be moved from one test set to another, including across modules. This is useful for reorganising test cases after they've been generated.

From the test set detail page, click the **Move** button next to any objective. A dialog allows you to select:
1. The destination module
2. The destination test set (within that module)

Moving an objective:
- Removes the objective and its tasks from the source test set
- Appends them to the destination test set (deduplicating by task ID)
- If the source test set has no remaining objectives or tasks after the move, it is deleted automatically
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
| `--case-name` | Name for the new `WebUiTestCase` |
| `--target` | `UI_Web_MVC` (uses `LegacyWebUiUrl`) or `UI_Web_Blazor` (uses `BraveCloudUiUrl`) |

**What happens:**

1. A non-headless Chromium window opens at the configured base URL.
2. An **overlay panel** appears in the **bottom-right corner** of the browser. If the page is still loading when it first appears, the overlay attaches automatically once the DOM is ready.
3. Interact with the page normally — every form fill (captured on field `change`) and click (on buttons, links, submit inputs) is recorded with its exact CSS selector and value.
4. Use the overlay buttons to add assertions at any point:
   - **+ Assert current URL (…)** — records an `assert-url-contains` step with the current path
   - **+ Assert page title (…)** — records an `assert-title-contains` step with the current title
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

Changes are saved via `PUT /api/modules/{moduleId}/testsets/{tsId}/tasks/{taskId}/webuicases/{index}`.

---

### Playwright step types

| Action | Selector | Value | Description |
|---|---|---|---|
| `navigate` | — | URL path | Go to a URL |
| `click` | CSS selector | — | Click an element (15 s timeout; JS fallback if Playwright actionability check stalls) |
| `fill` | CSS selector | text | Type text into an input |
| `select` | CSS selector | option value | Choose a dropdown option |
| `check` / `uncheck` | CSS selector | — | Tick/untick a checkbox |
| `hover` | CSS selector | — | Hover over an element |
| `press` | CSS selector | key name | Press a keyboard key |
| `assert-text` | CSS selector | expected text | Assert element text contains a value |
| `assert-visible` / `assert-hidden` | CSS selector | — | Assert element visibility |
| `assert-url-contains` | — | URL fragment | Assert the current URL contains a value |
| `assert-title-contains` | — | title fragment | Assert the page title contains a value |
| `wait` | CSS selector or — | ms (if no selector) | Wait for a selector or a fixed delay |

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
"BraveCloudUiStorageStateMaxAgeHours": 8
```

### Authentication

**Legacy MVC** — the agent navigates to `LegacyWebUiLoginPath`, fills the username and password from config, and submits the login form. The session is maintained for the duration of the test run. In recording mode, authentication is left to the user — navigate and log in manually within the browser window.

**Brave Cloud UI (Azure SSO)** — the first run performs a full SSO login via the Azure AD login page and saves the resulting browser auth state to `BraveCloudUiStorageStatePath`. Subsequent runs within `BraveCloudUiStorageStateMaxAgeHours` (default: 8 hours) reuse the saved state and skip the SSO flow entirely.

> **Important:** The Azure AD test account must have MFA disabled, or be excluded from MFA via a conditional access policy. Automated browser flows cannot handle interactive MFA prompts.

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
| Parallel task execution | `MaxParallelAgents` setting exists; sequential only in Phase 1 |
| Task dependency ordering | `DependsOn` field on `TestTask` exists; not yet enforced |
| UI testing — WinForms | Target type defined; no agent implemented |
| Background job testing (Hangfire) | Target type defined; no agent implemented |
| Message bus testing | Target type defined; no agent implemented |
| Database validation | Target type defined; no agent implemented |
| Retry and adaptive re-planning | Planned for Phase 2 |
