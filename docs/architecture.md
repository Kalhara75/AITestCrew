# AITestCrew — Architecture Documentation

## Solution Structure

The solution (`AiTestCrew.slnx`) contains five .NET 8 projects with a strict layered dependency graph — each layer only references layers below it. A React frontend communicates with the WebApi over REST.

```
┌──────────────┐     ┌──────────────────┐
│  React UI    │────▶│  AiTestCrew      │
│  (Vite+TS)   │ HTTP│  .WebApi (REST)  │──┐
│  Port 5173   │◀────│  Port 5050       │  │
└──────────────┘     └──────────────────┘  │
                                           │
┌──────────────────┐                       │
│  AiTestCrew      │──┐                   │
│  .Runner (CLI)   │  │                   │
└──────────────────┘  │                   │
                      ▼                   ▼
              AiTestCrew.Orchestrator
                      │
              AiTestCrew.Agents
                      │
              AiTestCrew.Core
```

Both Runner (CLI) and WebApi reference the same Orchestrator/Agents/Core layers. They share the same `modules/`, `testsets/` (legacy), and `executions/` file storage.

---

## Project Responsibilities

### AiTestCrew.Core

Pure domain layer. No NuGet dependencies beyond the .NET BCL.

| File | Purpose |
|---|---|
| `Models/TestTask.cs` | Input to an agent — a single decomposed test task |
| `Models/TestStep.cs` | Atomic test action with pass/fail/error result |
| `Models/TestResult.cs` | Aggregated output of one agent execution (has `ObjectiveId`, `ObjectiveName`) |
| `Models/TestResult.cs` (`TestSuiteResult`) | Aggregated output of the full test run (`TotalObjectives`, not TotalTasks) |
| `Models/Enums.cs` | `TestStatus`, `TestTargetType`, `TestPriority` |
| `Models/RunMode.cs` | `Normal`, `Reuse`, `Rebaseline`, `List` CLI modes |
| `Interfaces/ITestAgent.cs` | Contract all agents must implement |
| `Configuration/TestEnvironmentConfig.cs` | Strongly-typed settings binding from `appsettings.json` |

---

### AiTestCrew.Agents

Contains agent implementations and test set persistence. References Core only.

```
Agents/
  ApiAgent/
    ApiTestAgent.cs               — REST/GraphQL test execution
    ApiTestDefinition.cs          — LLM-generated API test step model + validation verdict
  Base/
    BaseTestAgent.cs              — Shared LLM communication (delegates JSON utilities to LlmJsonHelper)
    LlmJsonHelper.cs              — Static JSON cleaning/parsing utilities (shared with WebApi endpoints)
  Shared/
    WebUiTestCase.cs              — WebUiTestCase + WebUiStep models (legacy, shared by both UI agents)
    WebUiTestDefinition.cs        — WebUiTestDefinition model (v2 step definition for TestObjective.WebUiSteps)
  WebUiBase/
    BaseWebUiTestAgent.cs         — Shared Playwright logic: browser lifecycle, two-phase LLM generation,
                                    step execution (with JS click fallback), screenshot capture
    PlaywrightBrowserTools.cs     — Semantic Kernel plugin exposing snapshot/navigate/click/fill as kernel
                                    functions; tracks PageObservation list (real URL+title per page visited)
    PlaywrightRecorder.cs         — Human-driven recording mode: non-headless Chromium, JS overlay panel,
                                    ExposeFunctionAsync step capture, returns WebUiTestDefinition with real selectors
  LegacyWebUiAgent/
    LegacyWebUiTestAgent.cs       — ASP.NET MVC web UI agent (forms auth, UI_Web_MVC)
  BraveCloudUiAgent/
    BraveCloudUiTestAgent.cs      — Blazor web UI agent (Azure SSO + storage state, UI_Web_Blazor)
  Persistence/
    PersistedModule.cs            — Module manifest model (id, name, description, timestamps)
    PersistedTestSet.cs           — JSON envelope model for saved test sets (contains List<TestObjective> TestObjectives, v2 schema)
    PersistedExecutionRun.cs      — Execution history models (run, objective results with PersistedObjectiveResult, step results)
    ModuleRepository.cs           — File I/O for modules/{id}/module.json
    TestSetRepository.cs          — File I/O for test sets (legacy flat + module-scoped, incl. move objective)
                                    SaveAsync creates the module directory if it does not exist
    ExecutionHistoryRepository.cs — File I/O for executions/{testSetId}/{runId}.json
    SlugHelper.cs                 — Shared slugification logic
    MigrationHelper.cs            — Auto-migrates legacy testsets/ to modules/default/
```

#### Web UI Agents

Two Playwright-powered agents extend `BaseWebUiTestAgent`:

```
BaseTestAgent  (LLM, AskLlmAsync/AskLlmForJsonAsync)
    └── BaseWebUiTestAgent  (Playwright: browser lifecycle, two-phase generation, step execution)
            ├── LegacyWebUiTestAgent   (UI_Web_MVC,    forms auth)
            └── BraveCloudUiTestAgent  (UI_Web_Blazor, Azure SSO + storage state)
```

**Browser lifecycle** — A new `IPlaywright` + `IBrowser` instance is created at the start of each `ExecuteAsync` call and disposed in a `finally` block. Agents are registered as singletons but hold no browser state between calls.

**Two-phase LLM generation** — `BaseWebUiTestAgent.ExploreAndGenerateTestCasesAsync`:
1. **Phase 1 — Exploration**: a `Kernel.Clone()` with the `PlaywrightBrowserTools` plugin registered runs the LLM with `FunctionChoiceBehavior.Auto()`. The LLM navigates and inspects the real page. `PlaywrightBrowserTools` accumulates a `List<PageObservation>` (actual URL + title per `snapshot()` call).
2. **Phase 2 — JSON generation**: the exploration result and `PageObservation` list are injected into a new prompt that instructs the LLM to output only a JSON array. The base `Kernel` (no tools) is used so no further navigation occurs. Observed page facts are passed as authoritative ground truth to prevent hallucinated assertion values.

**Credentials** — `GetConfiguredCredentials()` is a virtual method on `BaseWebUiTestAgent`. Each subclass overrides it to return `(Username, Password)` from config. The base class injects these into the Phase 1 exploration prompt, ensuring the LLM never invents or hard-codes credential values.

**Click execution** — `ExecuteUiStepAsync` uses a minimum 15 s timeout for `click` steps (regardless of the stored `timeoutMs`) because clicks frequently trigger form submissions and full-page navigations. If Playwright's actionability check stalls (e.g. a covering overlay), it falls back to a JS `el.click()` via `EvalOnSelectorAsync`.

**Storage state** — `BraveCloudUiTestAgent` saves browser cookies/localStorage to `BraveCloudUiStorageStatePath` after a successful SSO login. Subsequent calls within `BraveCloudUiStorageStateMaxAgeHours` pass this file to `browser.NewContextAsync()` via `StorageStatePath`, skipping the full Azure AD redirect flow.

**Test case persistence** — `TestObjective` has two step collections:
- `ApiSteps` (`List<ApiTestDefinition>`) — populated by API agents
- `WebUiSteps` (`List<WebUiTestDefinition>`) — populated by UI agents and the recorder

`TargetType` (string, default `"API_REST"`) is also stored so the orchestrator can reconstruct tasks with the correct `TestTargetType` on reuse.

> **Note:** `PersistedTaskEntry` is **deprecated** (v1 schema only). It is retained solely for deserializing legacy test set files during migration. New code should use `TestObjective` exclusively.

#### PlaywrightRecorder

`PlaywrightRecorder.RecordAsync` provides a human-driven alternative to LLM generation. It:
- Always launches non-headless Chromium (`SlowMo = 50`)
- Calls `page.ExposeFunctionAsync("aitcRecordStep", ...)` — JS→.NET bridge, survives page navigation
- Calls `page.ExposeFunctionAsync("aitcStopRecording", ...)` — signals a `TaskCompletionSource`
- Calls `page.AddInitScriptAsync(...)` — re-injects event listeners and overlay panel on every page load (deferred via `DOMContentLoaded` so `document.body` is ready)

JS event listeners capture `change` events on inputs (→ `fill`) and `click` events on buttons/links (→ `click`). Selector computation uses `bestSelector(el)`: `#id → tag[name="x"] → tag[type="submit"] → input[type="x"] → a[href="path"] → tag`.

The overlay panel (fixed, bottom-right, dark theme) provides:
- **+ Assert current URL (path)** — records `assert-url-contains` with `location.pathname`
- **+ Assert page title (title)** — records `assert-title-contains` with `document.title`
- **Save & Stop** — signals `aitcStopRecording()`

Duplicate `fill` steps on the same selector are deduplicated (update-in-place). Session ends on Save & Stop, browser close, or 15-minute timeout.

---

### AiTestCrew.Orchestrator

Decomposes objectives, routes tasks to agents, aggregates results, manages run modes. References Core and Agents.

```
Orchestrator/
  TestOrchestrator.cs   — RunAsync, DecomposeObjectiveAsync, SaveTestSetAsync (308 lines)
```

---

### AiTestCrew.Runner

CLI entry point. Wires up DI, handles argument parsing, drives the run, renders output. References all other projects.

```
Runner/
  Program.cs                       — Top-level statements: arg parsing, DI, console output
                                     Includes --record mode short-circuit (before DI host build):
                                     slugifies module/testset IDs, calls PlaywrightRecorder.RecordAsync,
                                     creates module manifest if missing, saves WebUiTestDefinition to test set
  AnthropicChatCompletionService.cs — Bridges Anthropic.SDK to Semantic Kernel's IChatCompletionService
  FileLoggerProvider.cs            — Writes all log messages to a timestamped file in logs/
  appsettings.json                 — Runtime configuration (copied to output directory on build)
  appsettings.example.json         — Template with placeholder values for source control
```

**`--record` mode** runs before the DI host is built (no Orchestrator or agents needed). It resolves the module ID and test set ID via `SlugHelper.ToSlug` so the saved file path matches what the WebApi expects, then creates the module manifest via `ModuleRepository` if it does not exist.

---

### AiTestCrew.WebApi

REST API backend for the React UI. Mirrors Runner's DI wiring but exposes HTTP endpoints instead of a CLI.

```
WebApi/
  Program.cs                       — DI wiring, CORS, minimal API endpoints, migration
  AnthropicChatCompletionService.cs — Copy of Runner's bridge (same layer, can't reference Runner)
  Endpoints/
    ModuleEndpoints.cs             — Module CRUD + nested test set and run access
    TestSetEndpoints.cs            — Legacy flat test set endpoints (backward compat)
    RunEndpoints.cs                — POST /api/runs (trigger), GET /api/runs/{id}/status (poll)
  Services/
    RunTracker.cs                  — ConcurrentDictionary tracking active/completed runs
  appsettings.example.json         — Template config
```

**REST API endpoints:**

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/api/modules` | List all modules with test set counts |
| `POST` | `/api/modules` | Create a module |
| `GET` | `/api/modules/{id}` | Module detail |
| `PUT` | `/api/modules/{id}` | Update module name/description |
| `DELETE` | `/api/modules/{id}` | Delete empty module |
| `GET` | `/api/modules/{id}/testsets` | List test sets in module |
| `POST` | `/api/modules/{id}/testsets` | Create empty test set |
| `GET` | `/api/modules/{id}/testsets/{tsId}` | Test set detail |
| `DELETE` | `/api/modules/{id}/testsets/{tsId}` | Delete test set (cascades to runs) |
| `GET` | `/api/modules/{id}/testsets/{tsId}/runs` | Run history |
| `GET` | `/api/modules/{id}/testsets/{tsId}/runs/{runId}` | Run detail |
| `POST` | `/api/modules/{id}/testsets/{tsId}/move-objective` | Move objective to another test set |
| `PUT` | `/api/modules/{id}/testsets/{tsId}/objectives/{objectiveId}` | Update a step within an objective (step index in body) |
| `DELETE` | `/api/modules/{id}/testsets/{tsId}/objectives/{objectiveId}` | Delete an objective from the test set |
| `POST` | `/api/modules/{id}/testsets/{tsId}/ai-patch` | Preview LLM-applied natural language patch to test cases |
| `POST` | `/api/modules/{id}/testsets/{tsId}/ai-patch/apply` | Apply a previewed AI patch to the test set |
| `GET` | `/api/testsets` | List all test sets (legacy, combined view) |
| `GET` | `/api/testsets/{id}` | Full test set detail (legacy) |
| `GET` | `/api/testsets/{id}/runs` | Execution history (legacy) |
| `GET` | `/api/testsets/{id}/runs/{runId}` | Full execution detail (legacy) |
| `POST` | `/api/runs` | Trigger a test run (supports `moduleId` + `testSetId`) |
| `GET` | `/api/runs/{runId}/status` | Poll run progress |
| `GET` | `/api/health` | Health check |

---

### React Frontend (`ui/`)

Single-page application built with React 18, TypeScript, and Vite. Communicates with WebApi over REST.

```
ui/src/
  main.tsx                         — React root + QueryClientProvider + BrowserRouter
  App.tsx                          — Route definitions with Layout wrapper
  api/
    client.ts                      — fetch wrapper with base URL + error handling
    modules.ts                     — API functions for modules and module-scoped test sets/runs
    testSets.ts                    — API functions for legacy flat test sets and runs
    runs.ts                        — API functions for triggering and polling runs
  pages/
    ModuleListPage.tsx             — Module card grid (root page)
    ModuleDetailPage.tsx           — Test sets within a module + create/run dialogs
    TestSetDetailPage.tsx          — Test cases table + run history + trigger button (module-aware)
    ExecutionDetailPage.tsx        — Objective results with expandable step details (module-aware)
  components/
    Layout.tsx                     — Header, nav, content area
    StatusBadge.tsx                — Color-coded Passed/Failed/Error/Running badge
    TestSetCard.tsx                — Test set summary card (module-scoped links)
    TestCaseTable.tsx              — API test cases: HTTP method, endpoint, expected status table
    WebUiTestCaseTable.tsx         — Web UI test cases: name, start URL, step count, screenshot flag
    RunHistoryTable.tsx            — Run list with status, duration, date (module-aware links)
    StepList.tsx                   — Expandable objective/step rows with detail
    TriggerRunButton.tsx           — Mode selector + trigger + progress polling (module-aware)
    CreateModuleDialog.tsx         — Modal form to create a module
    CreateTestSetDialog.tsx        — Modal form to create a test set within a module
    RunObjectiveDialog.tsx         — Modal to select test set, enter objective + optional short name, trigger run
    ConfirmDialog.tsx              — Reusable confirmation modal (used for destructive actions)
    MoveObjectiveDialog.tsx        — Modal to move an objective to another module/test set
    EditTestCaseDialog.tsx         — Modal form to directly edit all fields of a single API test case
    EditWebUiTestCaseDialog.tsx    — Modal form to edit Web UI test case steps (action, selector, value,
                                    timeout per step; add/reorder/delete steps; delete entire test case)
    AiPatchPanel.tsx               — Panel for natural language AI patching of test cases with preview/apply flow
  types/
    index.ts                       — TypeScript interfaces matching API responses
```

---

## Key Dependencies (NuGet)

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.SemanticKernel` | 1.30.0 | LLM abstraction layer (chat history, chat completion service interface) |
| `Anthropic.SDK` | 4.7.2 | Native Anthropic API client (Claude) |
| `Microsoft.Extensions.Hosting` | 8.0.1 | .NET generic host, DI container, configuration |
| `Microsoft.Extensions.Http` | 8.0.1 | `IHttpClientFactory` for `HttpClient` lifecycle management |
| `Spectre.Console` | 0.49.1 | Rich console output (tables, spinners, markup) |

---

## Core Interfaces

### ITestAgent

```csharp
public interface ITestAgent
{
    string Name { get; }
    string Role { get; }
    Task<bool> CanHandleAsync(TestTask task);
    Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default);
}
```

All agents implement this interface. `ExecuteAsync` returns a single `TestResult` per task (one objective), with `ObjectiveId` and `ObjectiveName` identifying which objective it corresponds to. The `Steps` list inside the result contains one `TestStep` per API call or UI test case. The orchestrator calls `CanHandleAsync` on each registered agent to route each task. Adding a new agent type requires only implementing this interface and registering it in DI — no changes to the orchestrator.

---

## Execution Flow

```
User CLI args
    │
    ▼
ParseArgs()  ──── --list ──────────────────────────────────► Print test sets, exit
    │
    ├── RunMode.Normal / Rebaseline
    │       │
    │       ▼
    │   TestOrchestrator.RunAsync()
    │       │
    │       ├─ DecomposeObjectiveAsync()
    │       │       LLM decomposes objective → TestTask (one per objective)
    │       │
    │       ├─ For each objective (TestTask):
    │       │       FindAgentAsync() → routes to ApiTestAgent (if API_REST/GraphQL)
    │       │       agent.ExecuteAsync(task) → returns ONE TestResult with ObjectiveId
    │       │           ├─ TryLoadOpenApiSpecAsync()       [optional]
    │       │           ├─ DiscoverEndpointAsync()         [live GET, captures real fields]
    │       │           ├─ GenerateTestCasesAsync()        [LLM → List<ApiTestDefinition>]
    │       │           └─ For each ApiTestDefinition (step):
    │       │                   ExecuteTestCaseAsync()
    │       │                       ├─ Build HttpRequestMessage
    │       │                       ├─ InjectAuth()
    │       │                       ├─ HttpClient.SendAsync()
    │       │                       └─ ValidateResponseAsync()
    │       │                               ├─ Rule checks (status, contains)
    │       │                               └─ LLM validation (JSON, types, security)
    │       │       Metadata["generatedTestCases"] = list of ApiTestDefinition
    │       │
    │       ├─ SaveTestSetAsync()   [persists TestObjectives to modules/{moduleId}/{testSetId}.json]
    │       └─ GenerateSummaryAsync()  [LLM narrative]
    │
    └── RunMode.Reuse
            │
            ▼
        TestOrchestrator.RunAsync()
            │
            ├─ TestSetRepository.LoadAsync(reuseId)
            │       Deserialises modules/{moduleId}/{id}.json → PersistedTestSet
            │       Restores TestObjectives from saved data
            │
            ├─ For each TestObjective:
            │       Injects ApiSteps/WebUiSteps into TestTask.Parameters["PreloadedTestCases"]
            │       agent.ExecuteAsync(task) → returns ONE TestResult with ObjectiveId
            │           ├─ Detects "PreloadedTestCases" in Parameters
            │           ├─ Skips spec load, discovery, and LLM generation
            │           └─ Executes saved ApiTestDefinition steps directly
            │
            ├─ TestSetRepository.UpdateRunStatsAsync()  [bumps RunCount, LastRunAt]
            └─ GenerateSummaryAsync()  [LLM narrative]
```

---

## LLM Integration

### Semantic Kernel Abstraction

The application uses [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel) as an abstraction over the LLM. All LLM calls go through `IChatCompletionService` — a standard Semantic Kernel interface — so the underlying provider can be swapped without changing agent code.

### Anthropic Bridge

Anthropic does not have an official Semantic Kernel connector. `AnthropicChatCompletionService` is a custom implementation of `IChatCompletionService` that:
1. Accepts a `ChatHistory` object (Semantic Kernel's format)
2. Translates it to `Anthropic.SDK` message format
3. Calls the Anthropic Messages API
4. Translates the response back to a Semantic Kernel `ChatMessageContent`

This keeps all agent code provider-agnostic — switching to OpenAI requires only changing `appsettings.json`.

### LLM Prompt Pattern

All LLM calls follow this pattern in `BaseTestAgent`:

1. `AskLlmAsync(prompt, ct)` — returns raw string response
2. `AskLlmForJsonAsync<T>(prompt, ct)` — returns raw string, cleans markdown fences, extracts JSON, deserialises to `T`

JSON cleaning handles the LLM wrapping responses in ` ```json ... ``` ` blocks: the cleaner strips fences, finds the first `{` or `[`, and extracts through the last matching `}` or `]`.

### LLM Calls Per Run

| Step | LLM Used | Model Role |
|---|---|---|
| Objective decomposition | Yes | Senior QA Architect |
| Test case generation (Normal/Rebaseline) | Yes | Senior REST API Test Engineer |
| Test case generation (Reuse) | **No** | — |
| Response validation (per test case) | Yes | Reviewer |
| Suite summary | Yes | Test Results Analyst |

---

## Persistence Layer

### Module and Test Set Storage

Tests are organised in a **Module > Test Set > Test Objective > Steps** hierarchy. On disk:

```
{dataDir}/
  modules/
    sdr/
      module.json                          ← PersistedModule
      controlled-load-decodes.json         ← PersistedTestSet (can hold multiple objectives)
      meter-types.json                     ← PersistedTestSet
    default/
      module.json                          ← Auto-created by migration
      test-get-api-products-endpoint.json  ← Migrated from legacy testsets/
  testsets/                                ← Legacy directory (kept as read-only fallback)
  executions/                              ← Unchanged: executions/{testSetId}/{runId}.json
```

**Slug algorithm** (`SlugHelper.ToSlug`):
1. Lowercase the input string
2. Replace all non-alphanumeric characters with hyphens
3. Collapse consecutive hyphens to one
4. Trim leading/trailing hyphens
5. Truncate to 80 characters at the last hyphen boundary

Example: `"Standing Data Replication (SDR)"` → `"standing-data-replication-sdr"`

### Migration

On first startup, `MigrationHelper` runs two migrations automatically:

**Directory migration** (legacy `testsets/` → `modules/default/`):
1. If `modules/default/module.json` already exists, skips (idempotent)
2. Creates a "Default" module
3. Copies each `testsets/*.json` into `modules/default/`, populating `ModuleId`
4. Leaves the original `testsets/` directory intact

**Schema migration** (v1 → v2):
1. Detects test set files with `"tasks"` array (v1 schema, `PersistedTaskEntry`)
2. Converts each `PersistedTaskEntry` into a `TestObjective`: `testCases` → `apiSteps`, `webUiTestCases` → `webUiSteps`
3. Replaces `"tasks"` with `"testObjectives"` and sets `"schemaVersion": 2`
4. Writes the updated file back to disk

### PersistedTestSet Schema (v2)

```json
{
  "schemaVersion": 2,
  "id": "controlled-load-decodes",
  "name": "Controlled Load Decodes",
  "moduleId": "sdr",
  "createdAt": "2026-04-04T14:30:00Z",
  "lastRunAt": "2026-04-04T15:45:00Z",
  "runCount": 3,
  "testObjectives": [
    {
      "objectiveId": "a1b2c3d4",
      "objectiveText": "Test GET /api/ControlledLoadDecodes endpoint",
      "objectiveName": "Ctrl Load GET",
      "targetType": "API_REST",
      "apiSteps": [
        {
          "name": "Get all products - happy path",
          "method": "GET",
          "endpoint": "/api/products",
          "headers": {},
          "queryParams": {},
          "body": null,
          "expectedStatus": 200,
          "expectedBodyContains": ["id", "name", "price"],
          "expectedBodyNotContains": [],
          "isFuzzTest": false
        }
      ],
      "webUiSteps": []
    },
    {
      "objectiveId": "e5f6g7h8",
      "objectiveText": "Test POST /api/ControlledLoadDecodes endpoint",
      "objectiveName": "Ctrl Load POST",
      "targetType": "API_REST",
      "apiSteps": [ ... ],
      "webUiSteps": []
    }
  ]
}
```

> **v1 schema (deprecated):** Older test set files use `"tasks"` with `PersistedTaskEntry` objects. On first load, `MigrationHelper` automatically migrates v1 files to v2, converting each task entry into a `TestObjective` and renaming `testCases` → `apiSteps` / `webUiTestCases` → `webUiSteps`.

### Execution History Storage

Every test run (Normal, Reuse, Rebaseline) is persisted as a JSON file in `executions/{testSetId}/{runId}.json`.

**`PersistedExecutionRun` schema:**
```json
{
  "runId": "a1b2c3d4e5f6",
  "testSetId": "test-get-api-products-endpoint",
  "objective": "Test GET /api/products endpoint",
  "mode": "Reuse",
  "status": "Passed",
  "startedAt": "2026-04-04T14:30:00Z",
  "completedAt": "2026-04-04T14:31:42Z",
  "totalDuration": "00:01:42",
  "summary": "All 7 test steps passed...",
  "totalObjectives": 1,
  "passedObjectives": 1,
  "failedObjectives": 0,
  "errorObjectives": 0,
  "objectiveResults": [
    {
      "objectiveId": "a1b2c3d4",
      "objectiveName": "Ctrl Load GET",
      "agentName": "API Agent",
      "status": "Passed",
      "summary": "...",
      "passedSteps": 7,
      "failedSteps": 0,
      "totalSteps": 7,
      "steps": [
        {
          "action": "GET /api/products",
          "summary": "Happy path - 200 OK",
          "status": "Passed",
          "detail": "Response body: {...}",
          "duration": "00:00:00.342",
          "timestamp": "2026-04-04T14:30:05Z"
        }
      ]
    }
  ]
}
```

The orchestrator saves execution history after every run (wrapped in try/catch so failures are non-fatal). `ExecutionHistoryRepository` provides `SaveAsync`, `GetRunAsync`, `ListRuns`, `GetLatestRun`, and `DeleteRunsForTestSetAsync` (cascade-deletes all runs for a test set).

---

### How Reuse Works

In reuse mode, saved `ApiTestDefinition` steps (from `TestObjective.ApiSteps`) are placed into `TestTask.Parameters["PreloadedTestCases"]` before the task reaches the agent. `ApiTestAgent.ExecuteAsync` checks for this key at the very start and, if found, skips the discovery call and all LLM generation, proceeding directly to HTTP execution of the saved steps.

**Important:** `ApiTestDefinition.Body` is typed as `object?`. When deserialised from JSON by `System.Text.Json`, it becomes a `JsonElement`. This is handled correctly in `ExecuteTestCaseAsync` which serialises the body back to a string regardless of whether it is a `string` or `JsonElement`.

---

## Agent Pattern

### How Agents Are Registered and Routed

In `Program.cs`, agents are registered as both their concrete type and as `ITestAgent`:

```csharp
builder.Services.AddSingleton<ApiTestAgent>(...);
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<ApiTestAgent>());
```

The orchestrator receives `IEnumerable<ITestAgent>` via constructor injection (standard .NET DI behaviour for collections). For each task, it iterates the list and calls `CanHandleAsync(task)` on each agent, routing to the first match.

### Adding a New Agent

1. Implement `ITestAgent` in a new class under `AiTestCrew.Agents/`
2. Override `CanHandleAsync` to return `true` for the relevant `TestTargetType` values
3. Register in `Program.cs` alongside `ApiTestAgent`

No changes required to the orchestrator, models, or any existing agents.

### BaseTestAgent

Provides shared infrastructure for all agents:

- `AskLlmAsync(prompt, ct)` — sends a prompt, returns raw LLM response
- `AskLlmForJsonAsync<T>(prompt, ct)` — sends a prompt, cleans and deserialises JSON response (delegates to `LlmJsonHelper`)
- `SummariseResultsAsync(steps, ct)` — generates a one-paragraph summary of a list of test steps
- `CleanJsonResponse(raw)` — strips markdown fences, extracts JSON from LLM output (delegates to `LlmJsonHelper`)

`LlmJsonHelper` (static) provides the underlying JSON utilities (`CleanJsonResponse`, `DeserializeLlmResponse<T>`) so WebApi endpoints can also call the LLM directly without going through an agent.

---

## Discovery-Driven Test Generation

Before the LLM generates test cases, `ApiTestAgent.DiscoverEndpointAsync` makes a live GET request to the primary endpoint extracted from the task description (regex: first `/path` pattern). The response is used to:

1. Provide a real response body sample (first 1,500 characters) to the LLM
2. Extract top-level JSON field names
3. Instruct the LLM: *"only use field names you actually see here"*

This prevents hallucinated assertions (e.g. checking for a field called `productId` when the real field is `id`), which would cause tests to fail spuriously.

---

## Logging

### File Logger (`FileLoggerProvider`)

- Creates `logs/testrun_{yyyyMMdd_HHmmss}.log` on each run
- Captures all log levels (Trace through Critical)
- Thread-safe (lock-based writes)
- Format: `[HH:mm:ss.fff] [LEVEL] [Category,-30] Message`
- Includes full HTTP request/response details for every test case (at Debug level)

### Console Logger

- Filtered to `AiTestCrew.*` namespace only (suppresses `System.*` and `Microsoft.*` noise)
- Minimum level: `Warning` by default, `Information` when `VerboseLogging = true`
- Ensures the Spectre.Console spinner is not corrupted by framework log output

---

## Dependency Injection Wiring

All services are registered in `Program.cs` before the host is built:

```
TestEnvironmentConfig       → Singleton (bound from appsettings.json)
Kernel                      → Singleton (Semantic Kernel, with IChatCompletionService)
IChatCompletionService      → Singleton (AnthropicChatCompletionService or OpenAI)
IHttpClientFactory          → Managed by AddHttpClient()
ApiTestAgent                → Singleton (concrete + ITestAgent)
TestSetRepository           → Singleton (new instance, baseDir = AppContext.BaseDirectory)
ExecutionHistoryRepository  → Singleton (new instance, baseDir = AppContext.BaseDirectory)
ModuleRepository            → Singleton (new instance, baseDir = AppContext.BaseDirectory)
TestOrchestrator            → Singleton (receives IEnumerable<ITestAgent> + all repos)
RunTracker                  → Singleton (WebApi only — tracks async run state)
```

---

## Data Flow: Normal Run

```
CLI arg: "Test the /api/products endpoint"
    │
    ▼
CliArgs { Mode = Normal, Objective = "Test the /api/products endpoint" }
    │
    ▼
TestOrchestrator.RunAsync(objective, Normal)
    │
    ├─ LLM → TestTask (one per objective)
    │         { Id="a1b2c3d4", Target=API_REST, Description="Test GET /api/products..." }
    │
    ├─ ApiTestAgent.ExecuteAsync(task) → ONE TestResult
    │       │
    │       ├─ GET /api/products → EndpointDiscovery { StatusCode=200, Fields="id,name,price" }
    │       │
    │       ├─ LLM → List<ApiTestDefinition> (7 steps)
    │       │
    │       └─ For each ApiTestDefinition → TestStep { Status=Passed/Failed, Summary="...", Detail="..." }
    │
    ├─ TestResult { ObjectiveId, ObjectiveName, Status=Passed, PassedSteps=6, Steps=[...] }
    │
    ├─ modules/default/test-the-api-products-endpoint.json  ← persisted as TestObjective
    │
    ├─ LLM → suite summary string
    │
    └─ TestSuiteResult { Objective, Results, Summary, TotalDuration, TotalObjectives }
                │
                ▼
           Console table + overall line + LLM narrative
```

---

## Security Considerations

- **API keys and tokens** in `appsettings.json` must be protected. The file is copied to the binary output directory on build. It should not be committed to source control — use `appsettings.example.json` as the template.
- **Auth credentials are never passed to the LLM.** They are injected directly into `HttpRequestMessage` by `InjectAuth()` after the LLM-generated headers are applied.
- **LLM validation is advisory for security headers.** Missing `X-Content-Type-Options`, `X-Frame-Options`, and `Strict-Transport-Security` are noted in the validation reason text but do not cause test failures.
- **Response bodies are truncated** to 2,000 characters before being sent to the LLM for validation, and to 500 characters in the console detail view, to limit token usage and avoid leaking large payloads into logs inadvertently.
