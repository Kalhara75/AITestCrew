# AITestCrew — Architecture Documentation

## Solution Structure

The solution (`AiTestCrew.slnx`) contains four .NET 8 projects with a strict layered dependency graph — each layer only references layers below it.

```
AiTestCrew.Runner          (CLI entry point, DI wiring, console output)
       │
AiTestCrew.Orchestrator    (objective decomposition, task routing, result aggregation)
       │
AiTestCrew.Agents          (agent implementations, test execution, persistence)
       │
AiTestCrew.Core            (models, interfaces, configuration — no external dependencies)
```

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
    ApiTestAgent.cs      — REST/GraphQL test execution (544 lines)
    ApiTestCase.cs       — LLM-generated test case model + validation verdict
  Base/
    BaseTestAgent.cs     — Shared LLM communication and JSON utilities
  Persistence/
    PersistedTestSet.cs  — JSON envelope model for saved test sets
    TestSetRepository.cs — File I/O service for testsets/ directory
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

### Test Set Storage

Test sets are stored as JSON files in the `testsets/` directory, created alongside the compiled binary (same pattern as `logs/`).

**Filename:** `testsets/{slug}.json`

**Slug algorithm** (`TestSetRepository.SlugFromObjective`):
1. Lowercase the objective string
2. Replace all non-alphanumeric characters with hyphens
3. Collapse consecutive hyphens to one
4. Trim leading/trailing hyphens
5. Truncate to 80 characters at the last hyphen boundary

Example: `"Test GET /api/products endpoint"` → `"test-get-api-products-endpoint"`

The slug is deterministic — the same objective always produces the same filename. This means `--rebaseline` with the same objective always overwrites the same file.

### PersistedTestSet Schema

```json
{
  "id": "test-get-api-products-endpoint",
  "objective": "Test GET /api/products endpoint",
  "createdAt": "2026-04-04T14:30:00Z",
  "lastRunAt": "2026-04-04T15:45:00Z",
  "runCount": 3,
  "tasks": [
    {
      "taskId": "a1b2c3d4",
      "taskDescription": "Test the GET /api/products endpoint for listing products",
      "agentName": "API Agent",
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
- `AskLlmForJsonAsync<T>(prompt, ct)` — sends a prompt, cleans and deserialises JSON response
- `SummariseResultsAsync(steps, ct)` — generates a one-paragraph summary of a list of test steps
- `CleanJson(raw)` / `ExtractJson(raw)` — utility methods for stripping markdown and extracting JSON from LLM output

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
TestOrchestrator            → Singleton (receives IEnumerable<ITestAgent> + TestSetRepository)
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
