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
| `Models/TestResult.cs` | Aggregated output of one agent execution |
| `Models/TestResult.cs` (`TestSuiteResult`) | Aggregated output of the full test run |
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
    ApiTestCase.cs                — LLM-generated test case model + validation verdict
  Base/
    BaseTestAgent.cs              — Shared LLM communication (delegates JSON utilities to LlmJsonHelper)
    LlmJsonHelper.cs              — Static JSON cleaning/parsing utilities (shared with WebApi endpoints)
  Persistence/
    PersistedModule.cs            — Module manifest model (id, name, description, timestamps)
    PersistedTestSet.cs           — JSON envelope model for saved test sets (supports multiple objectives)
    PersistedExecutionRun.cs      — Execution history models (run, task, step results)
    ModuleRepository.cs           — File I/O for modules/{id}/module.json
    TestSetRepository.cs          — File I/O for test sets (legacy flat + module-scoped, incl. move objective)
    ExecutionHistoryRepository.cs — File I/O for executions/{testSetId}/{runId}.json
    SlugHelper.cs                 — Shared slugification logic
    MigrationHelper.cs            — Auto-migrates legacy testsets/ to modules/default/
```

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
  AnthropicChatCompletionService.cs — Bridges Anthropic.SDK to Semantic Kernel's IChatCompletionService
  FileLoggerProvider.cs            — Writes all log messages to a timestamped file in logs/
  appsettings.json                 — Runtime configuration (copied to output directory on build)
  appsettings.example.json         — Template with placeholder values for source control
```

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
| `PUT` | `/api/modules/{id}/testsets/{tsId}/tasks/{taskId}/testcases/{index}` | Update a single test case directly |
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
    ExecutionDetailPage.tsx        — Task results with expandable step details (module-aware)
  components/
    Layout.tsx                     — Header, nav, content area
    StatusBadge.tsx                — Color-coded Passed/Failed/Error/Running badge
    TestSetCard.tsx                — Test set summary card (module-scoped links)
    TestCaseTable.tsx              — HTTP method, endpoint, expected status table
    RunHistoryTable.tsx            — Run list with status, duration, date (module-aware links)
    StepList.tsx                   — Expandable task/step rows with detail
    TriggerRunButton.tsx           — Mode selector + trigger + progress polling (module-aware)
    CreateModuleDialog.tsx         — Modal form to create a module
    CreateTestSetDialog.tsx        — Modal form to create a test set within a module
    RunObjectiveDialog.tsx         — Modal to select test set, enter objective + optional short name, trigger run
    ConfirmDialog.tsx              — Reusable confirmation modal (used for destructive actions)
    MoveObjectiveDialog.tsx        — Modal to move an objective to another module/test set
    EditTestCaseDialog.tsx         — Modal form to directly edit all fields of a single test case
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

All agents implement this interface. The orchestrator calls `CanHandleAsync` on each registered agent to route each task. Adding a new agent type requires only implementing this interface and registering it in DI — no changes to the orchestrator.

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
    │       │       LLM decomposes objective → List<TestTask>
    │       │
    │       ├─ For each TestTask:
    │       │       FindAgentAsync() → routes to ApiTestAgent (if API_REST/GraphQL)
    │       │       agent.ExecuteAsync(task)
    │       │           ├─ TryLoadOpenApiSpecAsync()       [optional]
    │       │           ├─ DiscoverEndpointAsync()         [live GET, captures real fields]
    │       │           ├─ GenerateTestCasesAsync()        [LLM → List<ApiTestCase>]
    │       │           └─ For each ApiTestCase:
    │       │                   ExecuteTestCaseAsync()
    │       │                       ├─ Build HttpRequestMessage
    │       │                       ├─ InjectAuth()
    │       │                       ├─ HttpClient.SendAsync()
    │       │                       └─ ValidateResponseAsync()
    │       │                               ├─ Rule checks (status, contains)
    │       │                               └─ LLM validation (JSON, types, security)
    │       │
    │       ├─ SaveTestSetAsync()   [persists to testsets/{slug}.json]
    │       └─ GenerateSummaryAsync()  [LLM narrative]
    │
    └── RunMode.Reuse
            │
            ▼
        TestOrchestrator.RunAsync()
            │
            ├─ TestSetRepository.LoadAsync(reuseId)
            │       Deserialises testsets/{id}.json → PersistedTestSet
            │       Restores objective from saved data
            │
            ├─ Injects List<ApiTestCase> into each TestTask.Parameters["PreloadedTestCases"]
            │
            ├─ For each TestTask:
            │       agent.ExecuteAsync(task)
            │           ├─ Detects "PreloadedTestCases" in Parameters
            │           ├─ Skips spec load, discovery, and LLM generation
            │           └─ Executes saved ApiTestCase objects directly
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

Tests are organised in a **Module > Test Set** hierarchy. On disk:

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

On first startup, `MigrationHelper.MigrateToModulesAsync` runs automatically:
1. If `modules/default/module.json` already exists, skips (idempotent)
2. Creates a "Default" module
3. Copies each `testsets/*.json` into `modules/default/`, populating `ModuleId` and `Objectives`
4. Leaves the original `testsets/` directory intact

### PersistedTestSet Schema

```json
{
  "id": "controlled-load-decodes",
  "name": "Controlled Load Decodes",
  "moduleId": "sdr",
  "objectives": [
    "Test GET /api/ControlledLoadDecodes endpoint",
    "Test POST /api/ControlledLoadDecodes endpoint"
  ],
  "objective": "Test GET /api/ControlledLoadDecodes endpoint",
  "objectiveNames": {
    "Test GET /api/ControlledLoadDecodes endpoint": "Ctrl Load GET",
    "Test POST /api/ControlledLoadDecodes endpoint": "Ctrl Load POST"
  },
  "createdAt": "2026-04-04T14:30:00Z",
  "lastRunAt": "2026-04-04T15:45:00Z",
  "runCount": 3,
  "tasks": [
    {
      "taskId": "a1b2c3d4",
      "taskDescription": "Test the GET /api/products endpoint for listing products",
      "agentName": "API Agent",
      "objective": "Test GET /api/ControlledLoadDecodes endpoint",
      "testCases": [
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
      ]
    }
  ]
}
```

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
  "summary": "All 7 test cases passed...",
  "totalTasks": 1,
  "passedTasks": 1,
  "failedTasks": 0,
  "errorTasks": 0,
  "taskResults": [
    {
      "taskId": "a1b2c3d4",
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

In reuse mode, saved `ApiTestCase` objects are placed into `TestTask.Parameters["PreloadedTestCases"]` before the task reaches the agent. `ApiTestAgent.ExecuteAsync` checks for this key at the very start and, if found, skips the discovery call and all LLM generation, proceeding directly to HTTP execution of the saved cases.

**Important:** `ApiTestCase.Body` is typed as `object?`. When deserialised from JSON by `System.Text.Json`, it becomes a `JsonElement`. This is handled correctly in `ExecuteTestCaseAsync` which serialises the body back to a string regardless of whether it is a `string` or `JsonElement`.

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
    ├─ LLM → List<TestTask>
    │         [ { Id="a1b2c3d4", Target=API_REST, Description="Test GET /api/products..." } ]
    │
    ├─ ApiTestAgent.ExecuteAsync(task)
    │       │
    │       ├─ GET /api/products → EndpointDiscovery { StatusCode=200, Fields="id,name,price" }
    │       │
    │       ├─ LLM → List<ApiTestCase> (7 cases)
    │       │
    │       └─ For each ApiTestCase → TestStep { Status=Passed/Failed, Summary="...", Detail="..." }
    │
    ├─ TestResult { TaskId, Status=Passed, PassedSteps=6, Steps=[...] }
    │
    ├─ testsets/test-the-api-products-endpoint.json  ← persisted
    │
    ├─ LLM → suite summary string
    │
    └─ TestSuiteResult { Objective, Results, Summary, TotalDuration }
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
