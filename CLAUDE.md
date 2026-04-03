# AITestCrew — Claude Code Context

## What this project is

An AI-powered test automation framework that uses LLMs (Claude via Anthropic SDK) to generate, execute, and validate API tests from plain English objectives. Tests are run as real HTTP requests; responses are validated using a hybrid rule-based + LLM approach.

## Solution structure

```
AiTestCrew.Runner          ← CLI entry point, DI, Spectre.Console output
AiTestCrew.Orchestrator    ← Objective decomposition, task routing, aggregation
AiTestCrew.Agents          ← Agent implementations + persistence layer
AiTestCrew.Core            ← Models, interfaces, config — no external dependencies
```

Dependency direction is strict: `Runner → Orchestrator → Agents → Core`. Never introduce upward references.

## Key files

| File | What it does |
|---|---|
| `src/AiTestCrew.Runner/Program.cs` | CLI arg parsing, DI wiring, console output |
| `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` | RunAsync with Normal/Reuse/Rebaseline/List modes |
| `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | REST API test generation and execution |
| `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` | `AskLlmAsync`, `AskLlmForJsonAsync`, `SummariseResultsAsync` |
| `src/AiTestCrew.Agents/Persistence/TestSetRepository.cs` | Save/load/list test sets as JSON in `testsets/` |
| `src/AiTestCrew.Core/Models/` | `TestTask`, `TestStep`, `TestResult`, `TestSuiteResult`, `RunMode` |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Bound from `appsettings.json → TestEnvironment` |

## Run modes

```bash
dotnet run --project src/AiTestCrew.Runner -- "objective"         # Normal
dotnet run --project src/AiTestCrew.Runner -- --list              # List saved test sets
dotnet run --project src/AiTestCrew.Runner -- --reuse <id>        # Reuse saved test set
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "obj"  # Regenerate & save
```

## Agent pattern

All agents extend `BaseTestAgent` and implement `ITestAgent`:
- `CanHandleAsync(task)` — return true for the handled `TestTargetType` values
- `ExecuteAsync(task, ct)` — must always return a `TestResult`, never throw
- Check `task.Parameters["PreloadedTestCases"]` at the start of `ExecuteAsync` for reuse mode
- Return `Metadata["generatedTestCases"] = testCases` so the orchestrator can persist them

## Conventions

- All LLM calls via `AskLlmAsync` / `AskLlmForJsonAsync` — never call `IChatCompletionService` directly from an agent
- Auth is injected from `TestEnvironmentConfig` via `InjectAuth()`, never from LLM-generated headers
- Use `TestStep.Pass/Fail/Err` factories, never construct `TestStep` directly
- JSON serialisation: `PropertyNamingPolicy = CamelCase`, `PropertyNameCaseInsensitive = true`
- New config settings go in `TestEnvironmentConfig` + `appsettings.example.json` (not `appsettings.json`)
- Saved test set models (`PersistedTestSet`, `PersistedTaskEntry`) live in `AiTestCrew.Agents/Persistence/`

## Available slash commands

| Command | Purpose |
|---|---|
| `/add-agent <TargetType> "<desc>"` | Scaffold a new test agent |
| `/run-aitest <args>` | Build and run the test suite |
| `/add-validation <agent> "<rule>"` | Add a new validation rule |
| `/implement-feature "<description>"` | Implement any new feature |
| `/review-agent <AgentName>` | Review an agent for quality and pattern compliance |

## Documentation

- `docs/functional.md` — user-facing feature reference
- `docs/architecture.md` — component structure, data flow, design decisions

Keep both docs updated when behaviour or structure changes.
