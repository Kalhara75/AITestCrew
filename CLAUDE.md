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
| `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | REST API test generation and execution (multi-stack aware via `IApiTargetResolver`) |
| `src/AiTestCrew.Agents/Auth/ApiTargetResolver.cs` | Resolves API base URLs and per-stack token providers from `ApiStacks` config |
| `src/AiTestCrew.Core/Configuration/ApiStackConfig.cs` | Config models for API stacks and modules (`ApiStackConfig`, `ApiModuleConfig`) |
| `src/AiTestCrew.Core/Interfaces/IApiTargetResolver.cs` | Interface for multi-stack URL and auth resolution |
| `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` | `AskLlmAsync`, `AskLlmForJsonAsync`, `SummariseResultsAsync` |
| `src/AiTestCrew.Agents/Persistence/PersistedModule.cs` | Module model (id, name, description, timestamps) |
| `src/AiTestCrew.Agents/Persistence/ModuleRepository.cs` | CRUD for modules in `modules/{id}/module.json` |
| `src/AiTestCrew.Agents/Persistence/TestSetRepository.cs` | Save/load/move/delete test sets (legacy flat + module-scoped) |
| `src/AiTestCrew.Agents/Persistence/ExecutionHistoryRepository.cs` | Save/load/delete/prune execution runs in `executions/`, auto-retention via `MaxExecutionRunsPerTestSet` |
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
| `src/AiTestCrew.Agents/DesktopUiBase/BaseDesktopUiTestAgent.cs` | FlaUI desktop agent base — app launch, two-phase LLM generation, step execution, window transition detection |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopRecorder.cs` | Desktop recording — Windows hooks with message pump, Ctrl+V clipboard capture, system element filtering |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopStepExecutor.cs` | Desktop step dispatcher — InvokePattern click, polling assertions, multi-scope element search |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopElementResolver.cs` | Cascading element lookup — AutomationId > Name > ClassName+ControlType (if unambiguous) > TreePath |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopAutomationTools.cs` | Semantic Kernel plugin for LLM exploration — snapshot, click, fill, screenshot, list_windows |
| `src/AiTestCrew.Agents/WinFormsUiAgent/WinFormsUiTestAgent.cs` | WinForms agent — `UI_Desktop_WinForms` target type |
| `ui/src/components/DesktopUiTestCaseTable.tsx` | Desktop test case list with edit/delete, step preview |
| `ui/src/components/EditDesktopUiTestCaseDialog.tsx` | Desktop step editor — 5 selector fields, action-specific context fields |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlGenerationAgent.cs` | aseXML generation agent — template-driven AEMO B2B payload renderer (`AseXml_Generate` target type) |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs` | aseXML delivery agent — renders + uploads to a Bravo endpoint via SFTP/FTP (`AseXml_Deliver` target type) |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlTestDefinition.cs` | aseXML generation step persistence model (templateId + user field values) |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryTestDefinition.cs` | aseXML delivery step persistence model (generation fields + `EndpointCode`) |
| `src/AiTestCrew.Agents/AseXmlAgent/Templates/TemplateRegistry.cs` | Loads `*.xml` + `*.manifest.json` pairs from `templates/asexml/` at startup |
| `src/AiTestCrew.Agents/AseXmlAgent/Templates/AseXmlRenderer.cs` | Deterministic `{{token}}` substitution — enforces required user fields, runs generators for auto fields |
| `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs` | Generators for `MessageID`, `TransactionID`, timestamps (add a method to extend) |
| `src/AiTestCrew.Agents/AseXmlAgent/Delivery/BravoEndpointResolver.cs` | Queries `mil.V2_MIL_EndPoint` by `EndPointCode`; returns SFTP/FTP creds + `OutBoxUrl` + zip flag. Singleton cached for process lifetime |
| `src/AiTestCrew.Agents/AseXmlAgent/Delivery/SftpDropTarget.cs` | SSH.NET SFTP uploader — parses host/port, ensures remote dir, uploads, verifies |
| `src/AiTestCrew.Agents/AseXmlAgent/Delivery/FtpDropTarget.cs` | FluentFTP uploader for plain FTP endpoints |
| `src/AiTestCrew.Agents/AseXmlAgent/Delivery/DropTargetFactory.cs` | Picks SFTP vs FTP based on `OutBoxUrl` scheme; SFTP default |
| `src/AiTestCrew.Agents/AseXmlAgent/Delivery/XmlZipPackager.cs` | Wraps XML in `{MessageID}.zip` when endpoint has `IsOutboundFilesZiped = 1` |
| `src/AiTestCrew.Core/Interfaces/IEndpointResolver.cs` | `ResolveAsync(code)` + `ListCodesAsync()`; returns `BravoEndpoint` record |
| `templates/asexml/` | Checked-in aseXML templates + manifests grouped by transaction type (copied into each project's `bin/templates/asexml/` at build) |
| `ui/src/components/AseXmlTestCaseTable.tsx` | Read-only viewer for `AseXmlSteps` (no edit dialog in Phase 1) |
| `ui/src/components/AseXmlDeliveryTestCaseTable.tsx` | Read-only viewer for `AseXmlDeliverySteps` (adds Endpoint column) |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Bound from `appsettings.json → TestEnvironment` — `ApiStacks`, auth, execution, Playwright, desktop UI settings |
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
- `TestObjective` — one per user objective, contains `ApiSteps: List<ApiTestDefinition>`, `WebUiSteps: List<WebUiTestDefinition>`, `DesktopUiSteps: List<DesktopUiTestDefinition>`, `AseXmlSteps: List<AseXmlTestDefinition>`, and `AseXmlDeliverySteps: List<AseXmlDeliveryTestDefinition>`. `Source` field tracks origin: `"Generated"` (AI) or `"Recorded"` (user recording). Rebaseline is only allowed for generated objectives.
- `PersistedTestSet` — contains `List<TestObjective> TestObjectives` (v2 schema), optional `ApiStackKey` + `ApiModule` for multi-stack targeting, optional `EndpointCode` for aseXML delivery targeting
- `PersistedExecutionRun` — contains `List<PersistedObjectiveResult> ObjectiveResults`
- `PersistedTaskEntry` — **deprecated** (v1 schema, kept only for migration deserialization)

## Run modes

```bash
dotnet run --project src/AiTestCrew.Runner -- "objective"                                    # Normal (legacy flat)
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset ctrl-loads "objective"   # Normal (module-scoped, merges)
dotnet run --project src/AiTestCrew.Runner -- --stack bravecloud --api-module sdr --module sdr --testset nmi "objective"  # Multi-stack: target BraveCloud SDR
dotnet run --project src/AiTestCrew.Runner -- --stack legacy --api-module sdr --module sdr --testset nmi "objective"      # Multi-stack: target Legacy SDR
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
dotnet run --project src/AiTestCrew.Runner -- --record --module desktop --testset calc --case-name "Basic Add" --target UI_Desktop_WinForms  # Record WinForms test
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
- Auth is injected via `IApiTargetResolver` → per-stack `ITokenProvider`, never from LLM-generated headers
- API base URLs are resolved via `IApiTargetResolver.ResolveApiBaseUrl(stackKey, moduleKey)` — never hardcode URLs in agents
- API stacks and modules are configured in `TestEnvironmentConfig.ApiStacks` — no legacy flat `BaseUrl`/`ApiBaseUrl`
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
| `/add-asexml-template <TransactionType> <templateId> "<desc>"` | Scaffold a new aseXML template + manifest pair (content-only — no agent changes) |
| `/implement-feature "<description>"` | Implement any new feature |
| `/review-agent <AgentName>` | Review an agent for quality and pattern compliance |
| `/bravo-web-reference` | Bravo Web DOM patterns, Kendo UI selectors, and recorder/replay rules |
| `/blazor-cloud-reference` | Brave Cloud DOM patterns, MudBlazor selectors, SPA timing, and recorder/replay rules |
| `/desktop-winui-reference` | Desktop UI Automation patterns, FlaUI selectors, Windows hooks, recording/replay architecture |

## Documentation

- `docs/functional.md` — user-facing feature reference
- `docs/architecture.md` — component structure, data flow, design decisions

Keep both docs updated when behaviour or structure changes.
