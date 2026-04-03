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

## Run Modes

### Normal (default)
Generate new test cases via LLM, save them to disk, execute them.

```
dotnet run --project src/AiTestCrew.Runner -- "Test the /api/products endpoint"
```

Output:
- Executes 5–8 LLM-generated test cases per task.
- Saves the test set to `testsets/{slug}.json`.
- Prints a results table and LLM summary to the console.
- Tells you the reuse command to run the same tests again later.

---

### Reuse
Re-execute a previously saved test set without calling the LLM. The exact same test cases are run every time, making this suitable for regression checking.

```
dotnet run --project src/AiTestCrew.Runner -- --reuse <id>
```

Where `<id>` is the test set slug shown in `--list` output. Example:

```
dotnet run --project src/AiTestCrew.Runner -- --reuse test-the-api-products-endpoint
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

The same slug is computed from the objective, so it overwrites the existing saved file.

---

### List
Display all saved test sets and exit.

```
dotnet run --project src/AiTestCrew.Runner -- --list
```

Output includes: ID (slug), objective, task count, total test case count, created date, last run date, and total run count.

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
| `testsets/{slug}.json` | Saved test set (tasks + test cases + run metadata) |
| `logs/testrun_{timestamp}.log` | Full trace log of every run including HTTP request/response details |

Both directories are created automatically next to the compiled binary.

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

AITestCrew includes a React-based web dashboard for browsing test sets, viewing test cases, inspecting execution history, and triggering re-runs.

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

### Dashboard

The home page shows all saved test sets as cards. Each card displays:
- Test objective
- Last run status (colour-coded badge)
- Number of tasks, test cases, and runs
- Last run date

Click a card to view its details.

### Test Set Detail

Shows the full test set with:
- **Test cases table** — HTTP method (colour-coded), endpoint, test name, expected status code
- **Execution history** — all previous runs with status, pass/fail counts, duration, and date
- **Trigger buttons** — "Re-run Tests" (reuse mode) and "Rebaseline" (regenerate tests)

### Execution Detail

Shows a single run's results:
- Overall status, duration, and task pass/fail counts
- LLM-generated summary
- Expandable task sections, each showing individual test steps
- Click a step to expand its full response detail

### Triggering Runs from the UI

Click "Re-run Tests" or "Rebaseline" on a test set detail page. The UI:
1. Sends a POST to `/api/runs`
2. Shows a spinner while polling every 3 seconds
3. Automatically navigates to the results page when the run completes

Only one run can be active at a time (the API returns 409 if another is in progress).

---

## Execution History

Every test run (regardless of mode) is automatically persisted to `executions/{testSetId}/{runId}.json`. This enables:
- Viewing run history in the web dashboard
- Comparing pass/fail trends across runs
- Inspecting detailed step-by-step results from past executions

History is saved in a try/catch wrapper — if persistence fails, the CLI output and test execution are unaffected.

---

## Planned Future Capabilities

The following are scaffolded in the codebase but not yet active:

| Capability | Status |
|---|---|
| Parallel task execution | `MaxParallelAgents` setting exists; sequential only in Phase 1 |
| Task dependency ordering | `DependsOn` field on `TestTask` exists; not yet enforced |
| UI testing (MVC, Blazor, WinForms) | Target types defined; no agent implemented |
| Background job testing (Hangfire) | Target type defined; no agent implemented |
| Message bus testing | Target type defined; no agent implemented |
| Database validation | Target type defined; no agent implemented |
| Retry and adaptive re-planning | Planned for Phase 2 |
