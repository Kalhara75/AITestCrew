# AITestCrew — Claude Code Context

## What this project is

An AI-powered test automation framework that uses LLMs (Claude via Anthropic SDK) to generate, execute, and validate API tests from plain English objectives. Tests are run as real HTTP requests; responses are validated using a hybrid rule-based + LLM approach.

## Solution structure

```
AiTestCrew.Runner          ← CLI entry point, DI, Spectre.Console output
AiTestCrew.WebApi          ← REST API backend for the React UI (port 5050)
AiTestCrew.Orchestrator    ← Objective decomposition, task routing, aggregation
AiTestCrew.Agents          ← Agent implementations + persistence layer
AiTestCrew.Core            ← Models, interfaces, config — no external dependencies
ui/                        ← React 18 + TypeScript + Vite frontend (port 5173)
```

Dependency direction is strict: `Runner/WebApi → Orchestrator → Agents → Core`. Never introduce upward references. Runner and WebApi are at the same layer — neither references the other.

## Key files

| File | What it does |
|---|---|
| `src/AiTestCrew.Runner/Program.cs` | CLI arg parsing, DI wiring, console output |
| `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` | RunAsync with Normal/Reuse/Rebaseline/List modes, module-aware |
| `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | REST API test generation and execution |
| `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` | `AskLlmAsync`, `AskLlmForJsonAsync`, `SummariseResultsAsync` |
| `src/AiTestCrew.Agents/Persistence/PersistedModule.cs` | Module model (id, name, description, timestamps) |
| `src/AiTestCrew.Agents/Persistence/ModuleRepository.cs` | CRUD for modules in `modules/{id}/module.json` |
| `src/AiTestCrew.Agents/Persistence/TestSetRepository.cs` | Save/load/move/delete test sets (legacy flat + module-scoped) |
| `src/AiTestCrew.Agents/Persistence/ExecutionHistoryRepository.cs` | Save/load/delete execution runs in `executions/` |
| `src/AiTestCrew.Agents/Persistence/TestObjective.cs` | Test objective model (wraps ApiTestDefinition or WebUiTestDefinition) |
| `src/AiTestCrew.Agents/ApiAgent/ApiTestDefinition.cs` | API test definition (HTTP request + expected response) |
| `src/AiTestCrew.Agents/Shared/WebUiTestDefinition.cs` | Web UI test definition (start URL + Playwright steps) |
| `src/AiTestCrew.Agents/Persistence/MigrationHelper.cs` | Auto-migrates legacy layouts: `testsets/` → `modules/default/`, v1 → v2 schema |
| `src/AiTestCrew.WebApi/Program.cs` | WebApi DI wiring, CORS, migration, minimal API endpoints |
| `src/AiTestCrew.WebApi/Endpoints/ModuleEndpoints.cs` | Module CRUD + nested test set/run/move-objective endpoints |
| `src/AiTestCrew.WebApi/Endpoints/RunEndpoints.cs` | Trigger runs with optional moduleId/testSetId, active run recovery |
| `src/AiTestCrew.WebApi/Services/RunTracker.cs` | In-memory tracking of individual active runs |
| `src/AiTestCrew.WebApi/Services/ModuleRunTracker.cs` | In-memory tracking of module-level composite runs |
| `ui/src/contexts/ActiveRunContext.tsx` | Global run state: module + individual run tracking, polling, page-refresh recovery |
| `src/AiTestCrew.Core/Models/` | `TestTask`, `TestStep`, `TestResult`, `TestSuiteResult`, `RunMode` |
| `src/AiTestCrew.Agents/WebUiBase/PlaywrightRecorder.cs` | Recording — selector builder, MudBlazor/Kendo detection, overlay UI, post-recording validation |
| `src/AiTestCrew.Agents/WebUiBase/BaseWebUiTestAgent.cs` | Replay — step execution, click-icon, wait-for-stable, SPA settle, overlay dismissal |
| `src/AiTestCrew.Agents/BraveCloudUiAgent/BraveCloudUiTestAgent.cs` | Blazor agent — Azure SSO + TOTP, StorageState, 1920×1080 viewport |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Bound from `appsettings.json → TestEnvironment` |
| `src/AiTestCrew.Core/Services/AgentConcurrencyLimiter.cs` | Global semaphore for parallel execution, bounded by `MaxParallelAgents` |

## Test organisation

Tests are organised into **Modules > Test Sets > Test Objectives > Steps**.

Each Test Objective corresponds to ONE user objective and contains multiple test steps (API calls or UI test cases). The objective's pass/fail is the aggregate of its steps.

```
modules/{moduleId}/module.json           ← Module manifest
modules/{moduleId}/{testSetId}.json      ← Test set with TestObjectives (schema v2)
executions/{testSetId}/{runId}.json      ← Execution history with per-objective results
```

### Key persistence models
- `TestObjective` — one per user objective, contains `ApiSteps: List<ApiTestDefinition>` and `WebUiSteps: List<WebUiTestDefinition>`
- `PersistedTestSet` — contains `List<TestObjective> TestObjectives` (v2 schema)
- `PersistedExecutionRun` — contains `List<PersistedObjectiveResult> ObjectiveResults`
- `PersistedTaskEntry` — **deprecated** (v1 schema, kept only for migration deserialization)

## Run modes

```bash
dotnet run --project src/AiTestCrew.Runner -- "objective"                                    # Normal (legacy flat)
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset ctrl-loads "objective"   # Normal (module-scoped, merges)
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset ctrl-loads --obj-name "Short Name" "objective"  # With short display name
dotnet run --project src/AiTestCrew.Runner -- --list                                         # List saved test sets
dotnet run --project src/AiTestCrew.Runner -- --list-modules                                 # List modules
dotnet run --project src/AiTestCrew.Runner -- --reuse <id>                                   # Reuse saved test set
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "obj"                             # Regenerate & save
dotnet run --project src/AiTestCrew.Runner -- --create-module "Name"                         # Create a module
dotnet run --project src/AiTestCrew.Runner -- --create-testset <moduleId> "Name"             # Create empty test set
dotnet run --project src/AiTestCrew.Runner -- --record-setup --module sdr --testset nmi      # Record reusable setup steps (e.g. login)
dotnet run --project src/AiTestCrew.Runner -- --auth-setup                                   # Save Blazor SSO + 2FA auth state
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC               # Save Legacy MVC forms auth state
dotnet run --project src/AiTestCrew.Runner -- --record --module sec --testset users --case-name "Search" --target UI_Web_Blazor  # Record Blazor test
```

## Agent pattern

All agents extend `BaseTestAgent` and implement `ITestAgent`:
- `CanHandleAsync(task)` — return true for the handled `TestTargetType` values
- `ExecuteAsync(task, ct)` — returns ONE `TestResult` per task, never throw
- The `TestResult.Steps` list contains one `TestStep` per test case (API call or UI test)
- Check `task.Parameters["PreloadedTestCases"]` at the start of `ExecuteAsync` for reuse mode
- Return `Metadata["generatedTestCases"] = testCases` (list) so the orchestrator can persist them as steps in a `TestObjective`

## Conventions

- All LLM calls via `AskLlmAsync` / `AskLlmForJsonAsync` — never call `IChatCompletionService` directly from an agent
- Auth is injected from `TestEnvironmentConfig` via `InjectAuth()`, never from LLM-generated headers
- Use `TestStep.Pass/Fail/Err` factories, never construct `TestStep` directly
- JSON serialisation: `PropertyNamingPolicy = CamelCase`, `PropertyNameCaseInsensitive = true`
- New config settings go in `TestEnvironmentConfig` + `appsettings.example.json` (not `appsettings.json`)
- All persistence models (`PersistedModule`, `PersistedTestSet`, `TestObjective`, `PersistedExecutionRun`, etc.) live in `AiTestCrew.Agents/Persistence/`
- Slugification uses `SlugHelper.ToSlug()` — shared between `ModuleRepository` and `TestSetRepository`
- WebApi uses the same DI wiring pattern as Runner — if you add a new service, register it in both `Program.cs` files

## Available slash commands

| Command | Purpose |
|---|---|
| `/add-agent <TargetType> "<desc>"` | Scaffold a new test agent |
| `/run-aitest <args>` | Build and run the test suite |
| `/add-validation <agent> "<rule>"` | Add a new validation rule |
| `/implement-feature "<description>"` | Implement any new feature |
| `/review-agent <AgentName>` | Review an agent for quality and pattern compliance |
| `/bravo-web-reference` | Bravo Web DOM patterns, Kendo UI selectors, and recorder/replay rules |
| `/blazor-cloud-reference` | Brave Cloud DOM patterns, MudBlazor selectors, SPA timing, and recorder/replay rules |

## Documentation

- `docs/functional.md` — user-facing feature reference
- `docs/architecture.md` — component structure, data flow, design decisions

Keep both docs updated when behaviour or structure changes.
