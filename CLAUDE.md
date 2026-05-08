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

These are entry points — start here when navigating the codebase. For the full file map (~95 entries: aseXML delivery, deferred verification, auth recovery, distributed agents, data packs, DB Assert step, UI components), see `docs/file-map.md`.

| File | What it does |
|---|---|
| `src/AiTestCrew.Runner/Program.cs` | CLI arg parsing, DI wiring, console output |
| `src/AiTestCrew.WebApi/Program.cs` | WebApi DI wiring, CORS, migration, minimal API endpoints |
| `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` | RunAsync with Normal/Reuse/Rebaseline/List modes, module-aware |
| `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` | Agent base — `AskLlmAsync`, `AskLlmForJsonAsync`, `SummariseResultsAsync` |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Bound from `appsettings.json → TestEnvironment` — `ApiStacks`, `Environments`, `DefaultEnvironment`, auth, execution, Playwright, desktop UI settings |
| `src/AiTestCrew.Core/Interfaces/IApiTargetResolver.cs` | Multi-stack URL + auth resolution (env-aware overloads) |
| `src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs` | Customer-environment resolution (per-env URLs, creds, DB, app path) |
| `src/AiTestCrew.Agents/Persistence/TestObjective.cs` | Test objective persistence model (wraps Api/WebUi/Desktop/aseXML steps) |
| `src/AiTestCrew.Agents/Persistence/MigrationHelper.cs` | Auto-migrates legacy layouts: `testsets/` → `modules/default/`, v1 → v2 schema |
| `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` | Clones step defs and substitutes `{{Tokens}}` (lenient). Handles all step shapes |
| `src/AiTestCrew.Core/Utilities/TokenSubstituter.cs` | Shared `{{FieldName}}` regex + lenient/strict substitution |
| `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` | Schema migrations — current head includes deferred-verification + auth-refresh tables |
| `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` | `BackgroundService` — 30s sweeps: stale agents, stale claims, expired pending verifications, stale auth refreshes |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs` | aseXML delivery + deferred post-delivery verifications (sibling-agent dispatch with `{{Token}}` substitution) |
| `src/AiTestCrew.Runner/AgentMode/JobExecutor.cs` | Agent-mode dequeue → `TestOrchestrator.RunAsync` bridge; deferred-verification + auth-refresh routing |
| `ui/src/contexts/ActiveRunContext.tsx` | Frontend run state: module + individual run tracking, polling, page-refresh recovery |
| `src/AiTestCrew.Core/Models/` | `TestTask`, `TestStep`, `TestResult`, `TestSuiteResult`, `RunMode` |

## Test organisation

Tests are organised into **Modules > Test Sets > Test Objectives > Steps**.

Each Test Objective corresponds to ONE user objective and contains multiple test steps (API calls or UI test cases). The objective's pass/fail is the aggregate of its steps.

```
modules/{moduleId}/module.json           ← Module manifest
modules/{moduleId}/{testSetId}.json      ← Test set with TestObjectives (schema v2)
executions/{testSetId}/{runId}.json      ← Execution history with per-objective results
```

### Key persistence models
- `TestObjective` — one per user objective, contains `ApiSteps: List<ApiTestDefinition>`, `WebUiSteps: List<WebUiTestDefinition>`, `DesktopUiSteps: List<DesktopUiTestDefinition>`, `AseXmlSteps: List<AseXmlTestDefinition>`, and `AseXmlDeliverySteps: List<AseXmlDeliveryTestDefinition>`. Each `AseXmlDeliveryTestDefinition` can own `PostDeliveryVerifications: List<VerificationStep>` — recorded UI steps that run after delivery with `{{Token}}` substitution from the render context. `Source` field tracks origin: `"Generated"` (AI) or `"Recorded"` (user recording). Rebaseline is only allowed for generated objectives. Multi-env fields: `AllowedEnvironments: List<string>` (empty = default-env only; otherwise restricts to the listed env keys) and `EnvironmentParameters: Dictionary<envKey, Dictionary<token, value>>` (per-env `{{Token}}` values applied at playback).
- `PersistedTestSet` — contains `List<TestObjective> TestObjectives` (v2 schema), optional `ApiStackKey` + `ApiModule` for multi-stack targeting, optional `EndpointCode` for aseXML delivery targeting, optional `EnvironmentKey` for the default customer environment
- `PersistedExecutionRun` — contains `List<PersistedObjectiveResult> ObjectiveResults` + `EnvironmentKey` (which customer env the run executed against)
- `PersistedTaskEntry` — **deprecated** (v1 schema, kept only for migration deserialization)

## Run modes & CLI flags

The Runner supports six run modes — Normal, Reuse, Rebaseline, Recording, Auth-setup, Verify-only — plus an Agent mode for distributed execution. The most common shapes:

```bash
dotnet run --project src/AiTestCrew.Runner -- --module <moduleId> --testset <testSetId> "objective"      # Normal
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId>                    # Reuse — runs all test cases
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId> --objective <id>   # Reuse a single objective
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "<objective>"                                 # Regenerate (AI-generated objectives only)
dotnet run --project src/AiTestCrew.Runner -- --record --module <m> --testset <ts> --case-name "<name>" --target <UI_*>
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target <UI_*> --environment <envKey>
dotnet run --project src/AiTestCrew.Runner -- --verify-only --reuse <testSetId> --module <m> --objective <id>
dotnet run --project src/AiTestCrew.Runner -- --agent --name "<host>" --capabilities UI_Web_Blazor,UI_Web_MVC
```

`--module`, `--testset`, `--stack`, `--api-module`, `--environment`, and `--endpoint` persist on the test set on first use. `--environment <key>` picks per-customer URLs / creds / DB / app path / auth-state file; omit for `DefaultEnvironment`.

**Full reference** — every example, every flag, and per-command env prerequisites are in `docs/functional.md` (sections "Run Modes" and "Command reference (alphabetical)"). Always check there before adding a new flag — extending the existing pattern is usually the right move.

## Agent pattern

All agents extend `BaseTestAgent` and implement `ITestAgent`:
- `CanHandleAsync(task)` — return true for the handled `TestTargetType` values
- `ExecuteAsync(task, ct)` — returns ONE `TestResult` per task, never throw
- The `TestResult.Steps` list contains one `TestStep` per test case (API call or UI test)
- Check `task.Parameters["PreloadedTestCases"]` at the start of `ExecuteAsync` for reuse mode
- Return `Metadata["generatedTestCases"] = testCases` (list) so the orchestrator can persist them as steps in a `TestObjective`

## Conventions

- All LLM calls via `AskLlmAsync` / `AskLlmForJsonAsync` — never call `IChatCompletionService` directly from an agent
- Auth is injected via `IApiTargetResolver` → per-(env,stack) `ITokenProvider`, never from LLM-generated headers
- API base URLs are resolved via `IApiTargetResolver.ResolveApiBaseUrl(stackKey, moduleKey, envKey)` — never hardcode URLs in agents
- API stacks and modules are configured in `TestEnvironmentConfig.ApiStacks` — no legacy flat `BaseUrl`/`ApiBaseUrl`
- Customer environments are configured in `TestEnvironmentConfig.Environments` (with `DefaultEnvironment`) — read via `IEnvironmentResolver` methods, never from `TestEnvironmentConfig` top-level fields directly in an agent (top-level fields are fallbacks for when an env block omits a setting)
- Agents read their active env from `task.Parameters["EnvironmentKey"]` and env-specific `{{Token}}` values from `task.Parameters["EnvironmentParameters"]`, then apply them via `StepParameterSubstituter.Apply(caseOrDef, envParams)` before executing each step
- Web/Desktop agents set `CurrentEnvironmentKey` at the top of `ExecuteAsync` so subclass `TargetBaseUrl` / `TargetAppPath` getters resolve per-env via `_envResolver`
- Use `TestStep.Pass/Fail/Err` factories, never construct `TestStep` directly
- JSON serialisation: `PropertyNamingPolicy = CamelCase`, `PropertyNameCaseInsensitive = true`
- New config settings go in `TestEnvironmentConfig` + `appsettings.example.json` (not `appsettings.json`)
- All persistence models (`PersistedModule`, `PersistedTestSet`, `TestObjective`, `PersistedExecutionRun`, etc.) live in `AiTestCrew.Agents/Persistence/`
- Slugification uses `SlugHelper.ToSlug()` — shared between `ModuleRepository` and `TestSetRepository`
- WebApi uses the same DI wiring pattern as Runner — if you add a new service, register it in both `Program.cs` files

## Available slash commands

### Action skills (scaffold / implement)

| Command | Purpose |
|---|---|
| `/add-agent <TargetType> "<desc>"` | Scaffold a new test agent for a new target type |
| `/add-validation <agent> "<rule>"` | Add a new response validation rule to an existing agent |
| `/add-asexml-template <TransactionType> <templateId> "<desc>"` | Scaffold a new aseXML template + manifest pair (content-only — no agent changes) |
| `/add-asexml-verification` | Scaffold a post-delivery UI verification attached to an existing delivery objective (recorder + auto-parameterisation) |
| `/add-db-assert <moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"` | Scaffold a DB Assert post-step attached to an existing parent test step |
| `/add-data-pack-script` | Scaffold a startup-time SQL data-pack script (stored proc install, data prep, or cleanup) in the right folder with idempotency template |
| `/add-delivery-protocol <scheme> "<desc>"` | Scaffold a new `IXmlDropTarget` implementation (AS2, HTTP POST, SMB, etc.) |
| `/tune-deferred-verification` | Tune or debug deferred post-delivery verification (retry cadence, deadline, timeouts) + stuck-Awaiting diagnosis |
| `/implement-feature "<description>"` | Implement any new feature (general-purpose planner + builder) |
| `/run-aitest <args>` | Build and run the test suite |
| `/review-agent <AgentName>` | Review an agent implementation for correctness + pattern compliance |

### Reference skills (read before you build)

| Command | Purpose |
|---|---|
| `/asexml-reference` | End-to-end overview of the aseXML subsystem — Generate + Deliver + Verify, data model, extension points |
| `/bravo-web-reference` | Bravo Web DOM patterns, Kendo UI selectors, and recorder/replay rules |
| `/blazor-cloud-reference` | Brave Cloud DOM patterns, MudBlazor selectors, SPA timing, and recorder/replay rules |
| `/desktop-winui-reference` | Desktop UI Automation patterns, FlaUI selectors, Windows hooks, recording/replay architecture |

## Where to extend — quick map

Adding a ___ is → ___

| You want to add... | Use | Files touched |
|---|---|---|
| A new aseXML transaction type (e.g. CDN, MDM messages) | `/add-asexml-template` | `templates/asexml/<TransactionType>/*.xml` + manifest only. Zero C# changes. |
| A new auto-field generator (e.g. sequenced counter, GUID format) | Step 7 of `/add-asexml-template` | `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs` |
| A new delivery protocol (e.g. AS2, HTTP POST, SMB, GPG-encrypted SFTP) | `/add-delivery-protocol` | `src/AiTestCrew.Agents/AseXmlAgent/Delivery/*DropTarget.cs` + `DropTargetFactory.cs` |
| A new UI surface for verification (e.g. React single-page app) | `/add-agent` first (for standalone use) → then `VerificationStep.Target` already routes to it via `CanHandleAsync` | New agent + recorder; `VerificationStep.cs` already supports any `TestTargetType` |
| A new test target type (e.g. message bus, database check) | `/add-agent` | New agent; `TestTargetType` enum; DI registration |
| A new response validation rule | `/add-validation` | `ValidateResponseAsync` in the target agent |
| A new desktop assertion (count / region OCR / screenshot diff / pixel sampling) | Manual — read `/desktop-winui-reference` first | Add action string + `DesktopStepExecutor` switch case + recorder hotkey + UI editor option. Existing primitives: `assert-text` (UIA + auto-OCR fallback), `assert-text-ocr` (force OCR), `assert-count` (UIA descendant count of `ItemControlType`). For new primitives, follow the schema-change checklist in the skill. |
| A new CLI flag | Manual — `src/AiTestCrew.Runner/Program.cs` | `ParseArgs` + `CliArgs` + handler. Thread through `orchestrator.RunAsync` if it affects execution. |
| A new WebApi endpoint | Manual — `src/AiTestCrew.WebApi/Endpoints/*Endpoints.cs` | Map into `app.MapGroup` in `Program.cs`. Match naming style of sibling routes. |
| A new UI edit dialog | Manual — parallel to `EditWebUiTestCaseDialog.tsx` | Or reuse the existing one via its generic `definition` / `onSave` / `onDelete` props |
| A new wait strategy for verifications (SFTP pickup poll, DB status poll) | Extend `VerificationStep` with an optional richer strategy object; `AseXmlDeliveryAgent.RunVerificationAsync` dispatch | Currently fixed-delay only (`WaitBeforeSeconds`) |
| A new persistence field on any existing model | Extend the class; update `FromTestCase`/`ToTestCase` if applicable; update TS type | Re-reads old JSON via lenient deserialisation; no migration needed for additive changes |
| A new customer environment (e.g. TASN Networks) | Manual — `appsettings.json` only | Add an entry under `TestEnvironment.Environments.<key>` with the customer's URLs/creds/DB/per-stack BaseUrls. Run `--auth-setup --environment <key>` per target to cache auth state. Zero code changes. |
| A new per-environment setting (e.g. customer reporting URL) | Manual — three files | Add field to `EnvironmentConfig`; add `ResolveXxx(envKey)` on `IEnvironmentResolver` + `EnvironmentResolver` (with top-level fallback); inject the resolver wherever the setting is consumed. |
| A new teardown protocol (e.g. API-based purge, blob/file cleanup) | Manual — implement `ITeardownExecutor` | New class in `src/AiTestCrew.Agents/Teardown/`; register in DI alongside `BravoTeardownExecutor` (Runner + WebApi `Program.cs`). The orchestrator already invokes `ITeardownExecutor.ExecuteAsync` — no orchestrator change needed if a single implementation handles all teardown. For protocol routing, introduce a factory keyed off step metadata. |
| A startup-time SQL data-pack script (proc install / seed / cleanup) | `/add-data-pack-script` | `data/datapacks/<phase>/<envKey>/<NN.subfolder>/<NN.script>.sql` only. Zero C# changes. Per-env opt-in via `Environments.<envKey>.RunDataPacksOnStartup: true`. Must be re-runnable (`CREATE OR ALTER`, `MERGE`, `IF NOT EXISTS`). Verify on the dashboard's "Startup Data Packs" panel. See `docs/data-packs.md`. |
| Tuning deferred-verification retry / deadline behaviour | `/tune-deferred-verification` | Config-only (`appsettings.json → TestEnvironment.AseXml`). Key knobs: `DeferVerifications`, `VerificationEarlyStartFraction`, `VerificationRetryIntervalSeconds`, `VerificationGraceSeconds` (set to 0 for a hard wait ceiling). |
| Debugging a stuck "Awaiting Verification" run | `/tune-deferred-verification` | Query `run_pending_verifications` + `run_queue` on the WebApi's DB; check `agents` table for matching capability; janitor sweeps on 30s tick. |
| A new deferred job kind (beyond verifications — e.g. deferred API polling) | Manual — new `"kind": "..."` discriminator + branch in `JobExecutor.TryParseDeferredRequest` | Reuse `run_queue` with `not_before_at` + `run_pending_verifications` OR model a new state table. Agent enqueues via `IRunQueueRepository.EnqueueAsync` (HTTP in remote mode, direct SQLite server-side). |
| Tuning seamless authentication recovery | Config-only — `appsettings.json → TestEnvironment.Auth` | Knobs: `AutoRecoverApi`, `AutoRecoverUi`, `LoginRedirectUrlPatterns`, `AuthRefreshMaxLatencySeconds`, `PauseOnAuthFailure` (CI escape hatch), `ExpiryWarningHours`. See "Seamless Authentication Recovery" in `architecture.md`. |
| Hiding an env from the pre-flight auth-health panel | Config-only — `Environments.<key>.AuthHealthEnabled = false` | Agent scanner skips it on next heartbeat; endpoint also filters server-side. Useful for API-only envs. |
| Adding a new auth surface (beyond Api / WebBlazor / WebMvc) | Manual — new `AuthSurface` enum value + agent override | Add to `AuthSurface` in `Core/Models/Enums.cs`; override `Surface` + `TryRecoverFromLoginRedirectAsync` on the new agent; plumb through `AuthHealthEndpoints` if pre-flight is needed. |
| Debugging "Refresh in progress" stuck forever | Check `JobExecutor.ExecuteAuthSetupAsync` calls `_authRefreshRepo.MarkCompletedAsync(authRefreshId)` after `RecordingService.AuthSetupAsync` | Without it the `run_auth_refreshes` row sits `InProgress` until the janitor's 5-minute timeout marks it Failed. Also confirm `/start` payload threads `target` (not `surface`) + `authRefreshId`. |
| Debugging "auth state saved but tests still fail" | Inspect the saved file — empty `{"cookies":[],"origins":[]}` means `RecordingService.AuthSetupAsync` saved without real auth | Blazor flow needs `sawSsoRedirect` to flip true (URL visited login.microsoftonline.com) AND non-empty cookies before saving. If your customer URL doesn't trigger Azure SSO at the root, point `BraveCloudUiUrl` at a path that does. |
| A new DB connection (e.g. SDR Reporting DB) | Manual — config-only | `Environments.<key>.DbConnections.<connectionKey>` (or top-level `TestEnvironment.DbConnections.<connectionKey>` fallback). Zero code changes. |
| A new column-assertion operator | Manual — enum + evaluator branch | Add to `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs`; branch in `src/AiTestCrew.Agents/DbAgent/ColumnAssertionEvaluator.cs`. Update editor dropdown in `EditDbCheckStepDialog.tsx` + chat prompt examples in `ChatIntentService.cs`. |

## Documentation

- `docs/file-map.md` — full file-by-file map (subsystems: aseXML, deferred verification, auth recovery, distributed agents, data packs, UI). The `Key files` table above only lists entry points
- `docs/functional.md` — user-facing feature reference and CLI runbook
- `docs/architecture.md` — component structure, data flow, design decisions, extension patterns. Includes deep-dive sections for **Distributed Execution (Phase 4)**, **Deferred Post-Delivery Verification**, **Seamless Authentication Recovery** (silent retry → AwaitingAuth pause-and-resume → pre-flight AuthHealthPanel, schemas v8 + v9), **Chat Assistant**, **Startup Data Packs**, and **DB Assert Step** (data model, JSONPath evaluator, capture semantics, multi-DB resolution, security envelope).
- `docs/data-packs.md` — startup-time SQL data-pack guide (folder layout, opt-in config, authoring rules, dashboard troubleshooting)
- `docs/agentic-development-team.md` — five-agent pipeline (`feature-coordinator` → `implementation-planner` → `implementer` → `doc-writer` → `code-reviewer`) that turns a `requirements/REQ-*.md` file into a review-ready feature branch. Hand a requirement to `feature-coordinator` for hands-off implementation.
- Phase 3 decision: the aseXML feature is feature-complete through **Generate → Deliver → Wait → Verify**. Future work is extension (new transaction types, new protocols, richer wait strategies, desktop edit dialog, Phase 1.5 UI edit for non-verification aseXML steps) rather than new phases.

Keep docs updated when behaviour or structure changes. The `/add-*` skills codify the "right way" to extend — reach for them first before hand-editing.
