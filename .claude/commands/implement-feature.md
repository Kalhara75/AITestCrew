Implement a new feature or enhancement in AITestCrew.

Arguments: $ARGUMENTS

## What you must do

### Step 1 — Understand the request

Read the feature description carefully: $ARGUMENTS

Before writing any code, read the relevant existing files to understand the current state. At minimum read:
- `docs/architecture.md` — understand which layer the feature belongs in
- `docs/functional.md` — understand current user-facing behaviour
- The specific files most likely to change

### Step 2 — Identify the right layer

Use this guide to determine where the code belongs:

| What you're adding | Where it lives |
|---|---|
| New CLI flag or console output change | `src/AiTestCrew.Runner/Program.cs` |
| New REST API endpoint | `src/AiTestCrew.WebApi/Endpoints/` + register in `Program.cs` |
| New React UI page or component | `ui/src/pages/` or `ui/src/components/` |
| New run mode or test set management | `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` |
| New test execution capability for APIs | `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` |
| New test agent for a different target type | `src/AiTestCrew.Agents/{Type}Agent/` (use `/add-agent` skill) |
| New validation rule | Use `/add-validation` skill |
| New shared model | `src/AiTestCrew.Core/Models/` |
| New configuration setting | `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` + `appsettings.json` |
| New persistence operation | `src/AiTestCrew.Agents/Persistence/` |
| New execution history feature | `src/AiTestCrew.Agents/Persistence/ExecutionHistoryRepository.cs` |

### Step 3 — Check the dependency rules

The layering is strict:
```
Runner/WebApi → Orchestrator → Agents → Core
```
- `Core` has no project references — keep it that way
- `Agents` can reference `Core` only
- `Orchestrator` can reference `Core` and `Agents`
- `Runner` and `WebApi` can reference everything below them
- `Runner` and `WebApi` are at the same layer — neither references the other

Do not introduce circular dependencies or upward references.

**Important:** If you add a new service or DI registration, you must register it in **both** `src/AiTestCrew.Runner/Program.cs` and `src/AiTestCrew.WebApi/Program.cs`.

### Step 4 — Implement minimally

- Only write code that the feature actually requires. No speculative abstractions.
- Reuse existing patterns: `AskLlmForJsonAsync`, `TestStep.Pass/Fail/Err`, `InjectAuth`, `CleanJsonResponse`.
- Follow the exact naming conventions already in the codebase (PascalCase for public members, camelCase for private fields with `_` prefix).
- Do not add error handling for scenarios that cannot happen.
- Do not add comments unless the logic is genuinely non-obvious.

### Step 5 — Configuration

If the feature needs a new setting:
1. Add a property to `TestEnvironmentConfig` in `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs`
2. Add the property with a sensible default to `src/AiTestCrew.Runner/appsettings.example.json`
3. Do NOT add it to `appsettings.json` (that file contains real credentials and is not in source control)

### Step 6 — Test set compatibility

If the feature changes `ApiTestCase` or `PersistedTestSet`, existing saved JSON files may break on load. Handle this with nullable properties and sensible defaults — never add required properties to models that are serialised to disk.

### Step 7 — Build

Run `dotnet build` from the solution root. Fix all errors. The build must succeed with 0 errors and 0 warnings introduced by your changes.

### Step 8 — Update documentation

After implementation:
- Update `docs/functional.md` if user-facing behaviour changed (new CLI option, new output, new config setting)
- Update `docs/architecture.md` if a new component was added or the data flow changed

Keep documentation updates minimal and accurate — only change what is actually different.
