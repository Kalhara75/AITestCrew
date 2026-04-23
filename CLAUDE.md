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
| `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | REST API test generation and execution (multi-stack + multi-env aware via `IApiTargetResolver` + `IEnvironmentResolver`) |
| `src/AiTestCrew.Agents/Auth/ApiTargetResolver.cs` | Resolves API base URLs (env-overridable) and per-(env,stack) token providers |
| `src/AiTestCrew.Agents/Environment/EnvironmentResolver.cs` | `IEnvironmentResolver` implementation — per-customer overrides for UI URLs, creds, WinForms path, Bravo DB, per-stack BaseUrls; falls back to top-level config fields |
| `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` | Clones step definitions / test cases and substitutes `{{Tokens}}` using `TokenSubstituter` (lenient). Handles API, WebUi, DesktopUi, aseXml, AseXmlDelivery, VerificationStep shapes plus their runtime test-case counterparts |
| `src/AiTestCrew.Core/Configuration/ApiStackConfig.cs` | Config models for API stacks and modules (`ApiStackConfig`, `ApiModuleConfig`) |
| `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` | Per-customer env config block (URLs, creds, WinForms path, Bravo DB connection string, per-stack BaseUrl overrides) |
| `src/AiTestCrew.Core/Interfaces/IApiTargetResolver.cs` | Interface for multi-stack URL and auth resolution (env-aware overloads) |
| `src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs` | Interface for customer-environment resolution (ResolveKey / ListKeys / per-setting resolvers) |
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
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs` | aseXML delivery agent — renders + uploads to a Bravo endpoint via SFTP/FTP (`AseXml_Deliver` target type). Phase 3: post-delivery UI verifications run via sibling-agent dispatch with `{{Token}}` substitution from the render context |
| `src/AiTestCrew.Agents/AseXmlAgent/VerificationStep.cs` | UI verification step attached to a delivery case (Legacy MVC / Blazor / WinForms target + wait + step list) |
| `src/AiTestCrew.Agents/AseXmlAgent/Recording/VerificationRecorderHelper.cs` | Auto-parameterises recorded step literals into `{{Token}}` placeholders using the delivery's known context values |
| `src/AiTestCrew.Core/Utilities/TokenSubstituter.cs` | Shared `{{FieldName}}` regex + lenient/strict substitution (used by renderer + UI verification playback) |
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
| `src/AiTestCrew.Agents/Teardown/BravoTeardownExecutor.cs` | `ITeardownExecutor` impl — runs per-test-set SQL `DELETE` statements once per objective in `Reuse` mode. Per-env opt-in (`DataTeardownEnabled`), guardrail-checked, strict `{{Token}}` substitution from history + env params + delivery FieldValues |
| `src/AiTestCrew.Agents/Teardown/SqlGuardrails.cs` | Static `Validate(sql)` — strips `--` and `/* */` comments, requires `WHERE`, rejects `TRUNCATE`/`DROP`/`ALTER`/`CREATE`/`EXEC[UTE]`/`SHUTDOWN`/`GRANT`/`REVOKE`/`MERGE` |
| `src/AiTestCrew.Core/Interfaces/ITeardownExecutor.cs` | Teardown executor contract + `SqlTeardownStepDto` |
| `src/AiTestCrew.Storage/Persistence/SqlTeardownStep.cs` | Persisted teardown step `{ Name, Sql }` on `PersistedTestSet.TeardownSteps` |
| `ui/src/components/TeardownStepsPanel.tsx` | Editor for `TeardownSteps` on the test set detail page (mirrors `SetupStepsPanel`); shows env-opt-in warning |
| `templates/asexml/` | Checked-in aseXML templates + manifests grouped by transaction type (copied into each project's `bin/templates/asexml/` at build) |
| `ui/src/components/AseXmlTestCaseTable.tsx` | Read-only viewer for `AseXmlSteps` (no edit dialog in Phase 1) |
| `ui/src/components/AseXmlDeliveryTestCaseTable.tsx` | Read-only viewer for `AseXmlDeliverySteps` (adds Endpoint column) |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Bound from `appsettings.json → TestEnvironment` — `ApiStacks`, `Environments`, `DefaultEnvironment`, auth, execution, Playwright, desktop UI settings |
| `src/AiTestCrew.Core/Services/AgentConcurrencyLimiter.cs` | Global semaphore for parallel execution, bounded by `MaxParallelAgents` |
| `src/AiTestCrew.Core/Models/Agent.cs` | Phase 4 agent record — registered Runner instance + capabilities + status |
| `src/AiTestCrew.Core/Models/RunQueueEntry.cs` | Queue row — includes deferred-verification fields (`NotBeforeAt`, `DeadlineAt`, `AttemptCount`, `ParentQueueEntryId`, `ParentRunId`) |
| `src/AiTestCrew.Core/Models/PendingVerification.cs` | Deferred-verification state row — stable `pending_id` across retries, `attempt_log_json`, terminal result |
| `src/AiTestCrew.Core/Interfaces/IPendingVerificationRepository.cs` | DI contract for pending-verification CRUD |
| `src/AiTestCrew.Storage/Sqlite/SqliteAgentRepository.cs` | Agent upsert / heartbeat / stale-offline marking |
| `src/AiTestCrew.Storage/Sqlite/SqliteRunQueueRepository.cs` | Run-queue enqueue / atomic claim (respects `not_before_at`) / progress / result / stale-claim reclaim |
| `src/AiTestCrew.Storage/Sqlite/SqlitePendingVerificationRepository.cs` | `run_pending_verifications` CRUD — authoritative "is this run still outstanding?" state |
| `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` | Schema v6 migration — `ALTER TABLE run_queue` (5 new columns) + `CREATE TABLE run_pending_verifications` + new indexes |
| `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` | Self-contained snapshot carried in `run_queue.request_json` for a deferred verification (delivery context + verifications + deadline). Discriminator `"kind": "DeferredVerification"` |
| `src/AiTestCrew.WebApi/Endpoints/AgentEndpoints.cs` | `/api/agents/*` — register, heartbeat, list, deregister |
| `src/AiTestCrew.WebApi/Endpoints/QueueEndpoints.cs` | `/api/queue/*` — next (claim), progress, result, enqueue, get-by-id, list, cancel; progress endpoint moves tracker to `AwaitingVerification` when pending rows exist |
| `src/AiTestCrew.WebApi/Endpoints/PendingVerificationEndpoints.cs` | `/api/pending-verifications/*` — insert / get / attempt / complete / fail / by-run / count; REST surface the Runner remote-mode repos consume |
| `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` | `BackgroundService` — three sweeps on a 30s tick: mark stale agents Offline, reclaim stale queue claims, expire pending verifications past `VerificationMaxLatencySeconds` + finalise their runs |
| `src/AiTestCrew.WebApi/Services/RunDispatchHelper.cs` | Decides whether a run must be enqueued for a local agent (Web/Desktop targets) or run in-process |
| `src/AiTestCrew.Runner/AgentMode/AgentRunner.cs` | Runner `--agent` mode — register, heartbeat, poll, execute |
| `src/AiTestCrew.Runner/AgentMode/AgentClient.cs` | HTTP wrapper around `/api/agents/*` + `/api/queue/*` |
| `src/AiTestCrew.Runner/AgentMode/JobExecutor.cs` | Bridges a dequeued job to `TestOrchestrator.RunAsync` — detects `"kind": "DeferredVerification"` payloads and routes to VerifyOnly; treats `AwaitingVerification` results as success (not a failure to report) |
| `src/AiTestCrew.Runner/RemoteRepositories/ApiClientRunQueueRepository.cs` | HTTP-backed `IRunQueueRepository` — agent enqueues deferred / retry rows via `POST /api/queue`. Non-hot-path methods throw `NotSupportedException` |
| `src/AiTestCrew.Runner/RemoteRepositories/ApiClientPendingVerificationRepository.cs` | HTTP-backed `IPendingVerificationRepository` over `/api/pending-verifications/*` |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs` | Delivery agent with deferred path — `TryEnqueueDeferredVerifications` + `DeferredVerifyAsync` (retry-via-reenqueue) + `TryFinaliseParentRunAsync` (transactional merge + summary regeneration) |
| `ui/src/components/AgentsPanel.tsx` | Dashboard panel — agents with status dot, capabilities, owner, current job |
| `ui/src/components/QueueBanner.tsx` | Dashboard banner — active queued/claimed/running jobs + cancel button; deferred entries (`notBeforeAt` in future) show "Deferred verification — next attempt in ~N min" |
| `ui/src/components/StepList.tsx` | Step rendering; `AwaitingVerification` steps get a cyan ⏳ pill and live countdown parsed from `firstDueAtUtc` in step detail |
| `ui/src/components/TriggerRunButton.tsx` + `TriggerObjectiveRunButton.tsx` | Trigger buttons — swap the spinner for a quiet cyan ⏳ chip during `AwaitingVerification` so the UI doesn't falsely imply active execution |

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

## Run modes

```bash
# ── Normal / rebaseline / reuse ─────────────────────────────────────────────
dotnet run --project src/AiTestCrew.Runner -- "objective"                                    # Normal (legacy flat)
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset ctrl-loads "objective"   # Normal (module-scoped, merges)
dotnet run --project src/AiTestCrew.Runner -- --module sdr --testset ctrl-loads --obj-name "Short Name" "objective"  # With short display name
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "obj"                             # Regenerate & save (AI-generated objectives only)
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId>                            # Reuse (legacy flat)
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId>         # Reuse module-scoped — runs ALL test cases
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId> --objective <idOrName>  # Reuse a single test case (--objective matches Id slug OR display name)

# ── Multi-stack API targeting ──────────────────────────────────────────────
dotnet run --project src/AiTestCrew.Runner -- --stack bravecloud --api-module sdr --module sdr --testset nmi "objective"
dotnet run --project src/AiTestCrew.Runner -- --stack legacy --api-module sdr --module sdr --testset nmi "objective"

# ── Multi-environment (customer-based) targeting ───────────────────────────
dotnet run --project src/AiTestCrew.Runner -- --list-environments                             # List configured customer envs (default marked)
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId> --environment sumo-retail    # Run against Sumo (default fallback)
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId> --environment ams-metering   # Run same set against AMS (uses per-env URLs, creds, DB, app path)

# ── Module + test set management ───────────────────────────────────────────
dotnet run --project src/AiTestCrew.Runner -- --list                                         # List saved test sets
dotnet run --project src/AiTestCrew.Runner -- --list-modules                                 # List modules
dotnet run --project src/AiTestCrew.Runner -- --create-module "Name"                         # Create a module
dotnet run --project src/AiTestCrew.Runner -- --create-testset <moduleId> "Name"             # Create empty test set

# ── Agent mode (Phase 4 — distributed execution) ───────────────────────────
dotnet run --project src/AiTestCrew.Runner -- --agent --name "Alice-PC"                       # Register this machine as a long-running agent (polls server, executes queued UI/desktop jobs)
dotnet run --project src/AiTestCrew.Runner -- --agent --capabilities UI_Web_Blazor,UI_Web_MVC  # Limit which target types this agent will accept

# ── Recording (standalone test cases) ──────────────────────────────────────
# All recording + auth commands accept --environment <key>; omit for DefaultEnvironment.
dotnet run --project src/AiTestCrew.Runner -- --record-setup --module sdr --testset nmi      # Record reusable setup steps (e.g. login)
dotnet run --project src/AiTestCrew.Runner -- --auth-setup                                   # Save Blazor SSO + 2FA auth state (default env)
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor --environment sumo-retail  # Save auth for a specific customer env (writes env-scoped storage-state file)
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC --environment ams-metering    # Save Legacy MVC forms auth for AMS
dotnet run --project src/AiTestCrew.Runner -- --record --module sec --testset users --case-name "Search" --target UI_Web_Blazor --environment sumo-retail
dotnet run --project src/AiTestCrew.Runner -- --record --module desktop --testset calc --case-name "Basic Add" --target UI_Desktop_WinForms --environment sumo-retail

# ── aseXML (Phase 1: generate; Phase 2: deliver; Phase 3: verify) ─────────
dotnet run --project src/AiTestCrew.Runner -- --module aemo-b2b --testset mfn-tests "Generate an MFN for NMI 4103035611 ..."  # Render XML to output/asexml/
dotnet run --project src/AiTestCrew.Runner -- --list-endpoints                               # List endpoints from mil.V2_MIL_EndPoint (needs AseXml.BravoDb.ConnectionString)
dotnet run --project src/AiTestCrew.Runner -- --module aemo-b2b --testset mfn-delivery --endpoint GatewaySPARQ --obj-name "Deliver MFN" "Deliver MFN for NMI 4103035611 to GatewaySPARQ ..."
dotnet run --project src/AiTestCrew.Runner -- --record-verification --module aemo-b2b --testset mfn-delivery --objective <idOrName> --target UI_Web_Blazor --environment sumo-retail --verification-name "MFN Process Overview shows 'One In All In'" --wait 30
dotnet run --project src/AiTestCrew.Runner -- --record-verification --module aemo-b2b --testset mfn-delivery --objective <idOrName> --target UI_Web_MVC --environment sumo-retail --verification-name "Legacy MFN Search grid row exists"
# Tip: run --auth-setup --target UI_Web_MVC --environment <envKey> first so MVC recording starts authenticated against the right customer (skips capturing the login flow).

# ── Verify-only (re-run post-delivery verifications without re-delivering) ──
dotnet run --project src/AiTestCrew.Runner -- --verify-only --reuse mfn-delivery --module aemo-b2b --objective "Deliver MFN One In All In to GatewaySPARQ"
dotnet run --project src/AiTestCrew.Runner -- --verify-only --reuse mfn-delivery --module aemo-b2b --objective "Deliver MFN One In All In to GatewaySPARQ" --wait 0  # Skip wait (file already processed)
```

### Flag reference (alphabetical)

| Flag | Modes | Value | Purpose |
|---|---|---|---|
| `--agent` | Agent | flag | Start the Runner as a long-running agent that polls the server for queued UI/desktop jobs. Requires `ServerUrl` + `ApiKey` in config. |
| `--api-module <key>` | Normal / Reuse | e.g. `sdr` | Pick a module within a multi-stack API (`ApiStacks.<stack>.Modules`). Persists on the test set. |
| `--auth-setup` | Auth | (none) | Launch a browser and save auth state. `--target UI_Web_Blazor` (default) = Azure SSO + TOTP; `--target UI_Web_MVC` = forms auth. Combine with `--environment` to pick the right customer's URL / credentials / storage-state file. |
| `--environment <key>` | All run modes + Auth + Recording | e.g. `sumo-retail`, `ams-metering` | Customer environment key. Picks per-env URLs, credentials, WinForms app path, Bravo DB connection, per-stack BaseUrls, and cached auth-state filenames. Persists on the test set. Omit for `TestEnvironment.DefaultEnvironment`. |
| `--capabilities <list>` | Agent | e.g. `UI_Web_Blazor,UI_Web_MVC` | Comma-separated target types this agent accepts. Default: all three UI targets. |
| `--case-name "<name>"` | Recording | string | Display name for a recorded test case or setup recording. |
| `--create-module "<name>"` | Management | string | Create a module; slugifies the name. |
| `--create-testset <moduleId> "<name>"` | Management | slug + string | Create an empty test set in a module. |
| `--delivery-step-index <n>` | Record-verification | int (default 0) | Which delivery case in the objective to attach to. |
| `--endpoint <EndPointCode>` | Delivery | e.g. `GatewaySPARQ` | Bravo `mil.V2_MIL_EndPoint.EndPointCode`. Overrides LLM extraction. Persisted on the test set. |
| `--list` | List | (none) | List legacy flat test sets. |
| `--list-endpoints` | List | (none) | Query Bravo DB and print available endpoint codes. |
| `--list-environments` | List | (none) | List configured customer environments (default marked). |
| `--list-modules` | List | (none) | List modules. |
| `--module <moduleId>` | All | slug | Module scope for the command. |
| `--name "<name>"` | Agent | string | Agent display name shown on the dashboard. Defaults to `$env:COMPUTERNAME`. |
| `--obj-name "<name>"` | Normal / Rebaseline | string | Short display name for the generated objective. |
| `--objective <idOrName>` | Reuse / VerifyOnly / Record-verification | slug OR display name | Reuse: scope to a single test case. VerifyOnly: required, identifies the delivery objective. Record-verification: target delivery objective. Case-insensitive; matches `TestObjective.Id` first, then `Name`. |
| `--rebaseline` | Rebaseline | flag | Regenerate test cases via LLM and overwrite. Only valid for AI-generated objectives. |
| `--record` | Recording | flag | Record a standalone test case (combine with `--target`). |
| `--record-setup` | Recording | flag | Record setup steps (e.g. login) at the test-set level. |
| `--record-verification` | Recording | flag | Record a post-delivery UI verification attached to a delivery objective. |
| `--reuse <testSetId>` | Reuse / VerifyOnly | slug | Replay a saved test set. Module-scoped: auto-derives `--testset` if omitted. Required for VerifyOnly. |
| `--stack <key>` | Normal / Reuse | e.g. `bravecloud` | API stack key (`ApiStacks.<key>`). Persists on the test set. |
| `--target <type>` | Recording | `UI_Web_MVC`, `UI_Web_Blazor`, `UI_Desktop_WinForms` | UI surface for recording. |
| `--testset <testSetId>` | All | slug | Test set scope (auto-derived from `--reuse` if module-scoped). |
| `--verification-name "<name>"` | Record-verification | string | Display label for the recorded verification. |
| `--verify-only` | VerifyOnly | flag | Skip delivery (render/upload), reconstruct context from execution history, re-run only the post-delivery UI verifications. Requires `--reuse` + `--objective`. |
| `--wait <seconds>` | Record-verification / VerifyOnly | int (default = `AseXml.DefaultVerificationWaitSeconds`, 30) | Record-verification: delay between delivery and this verification at playback. VerifyOnly: overrides wait time on all verification steps (use `--wait 0` to skip delays). |

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
| A new CLI flag | Manual — `src/AiTestCrew.Runner/Program.cs` | `ParseArgs` + `CliArgs` + handler. Thread through `orchestrator.RunAsync` if it affects execution. |
| A new WebApi endpoint | Manual — `src/AiTestCrew.WebApi/Endpoints/*Endpoints.cs` | Map into `app.MapGroup` in `Program.cs`. Match naming style of sibling routes. |
| A new UI edit dialog | Manual — parallel to `EditWebUiTestCaseDialog.tsx` | Or reuse the existing one via its generic `definition` / `onSave` / `onDelete` props |
| A new wait strategy for verifications (SFTP pickup poll, DB status poll) | Extend `VerificationStep` with an optional richer strategy object; `AseXmlDeliveryAgent.RunVerificationAsync` dispatch | Currently fixed-delay only (`WaitBeforeSeconds`) |
| A new persistence field on any existing model | Extend the class; update `FromTestCase`/`ToTestCase` if applicable; update TS type | Re-reads old JSON via lenient deserialisation; no migration needed for additive changes |
| A new customer environment (e.g. TASN Networks) | Manual — `appsettings.json` only | Add an entry under `TestEnvironment.Environments.<key>` with the customer's URLs/creds/DB/per-stack BaseUrls. Run `--auth-setup --environment <key>` per target to cache auth state. Zero code changes. |
| A new per-environment setting (e.g. customer reporting URL) | Manual — three files | Add field to `EnvironmentConfig`; add `ResolveXxx(envKey)` on `IEnvironmentResolver` + `EnvironmentResolver` (with top-level fallback); inject the resolver wherever the setting is consumed. |
| A new teardown protocol (e.g. API-based purge, blob/file cleanup) | Manual — implement `ITeardownExecutor` | New class in `src/AiTestCrew.Agents/Teardown/`; register in DI alongside `BravoTeardownExecutor` (Runner + WebApi `Program.cs`). The orchestrator already invokes `ITeardownExecutor.ExecuteAsync` — no orchestrator change needed if a single implementation handles all teardown. For protocol routing, introduce a factory keyed off step metadata. |
| Tuning deferred-verification retry / deadline behaviour | `/tune-deferred-verification` | Config-only (`appsettings.json → TestEnvironment.AseXml`). Key knobs: `DeferVerifications`, `VerificationEarlyStartFraction`, `VerificationRetryIntervalSeconds`, `VerificationGraceSeconds` (set to 0 for a hard wait ceiling). |
| Debugging a stuck "Awaiting Verification" run | `/tune-deferred-verification` | Query `run_pending_verifications` + `run_queue` on the WebApi's DB; check `agents` table for matching capability; janitor sweeps on 30s tick. |
| A new deferred job kind (beyond verifications — e.g. deferred API polling) | Manual — new `"kind": "..."` discriminator + branch in `JobExecutor.TryParseDeferredRequest` | Reuse `run_queue` with `not_before_at` + `run_pending_verifications` OR model a new state table. Agent enqueues via `IRunQueueRepository.EnqueueAsync` (HTTP in remote mode, direct SQLite server-side). |

## Documentation

- `docs/functional.md` — user-facing feature reference and CLI runbook
- `docs/architecture.md` — component structure, data flow, design decisions, extension patterns
- Phase 3 decision: the aseXML feature is feature-complete through **Generate → Deliver → Wait → Verify**. Future work is extension (new transaction types, new protocols, richer wait strategies, desktop edit dialog, Phase 1.5 UI edit for non-verification aseXML steps) rather than new phases.

Keep docs updated when behaviour or structure changes. The `/add-*` skills codify the "right way" to extend — reach for them first before hand-editing.
