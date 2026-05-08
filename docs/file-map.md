# AITestCrew — File map

Detailed reference for every notable file in the codebase, grouped by subsystem.

The top-level `CLAUDE.md` keeps only the entry-point files needed to navigate the codebase. Reach for this map when you need precise pointers into a specific subsystem (aseXML delivery, deferred verification, auth recovery, data packs, etc.). For most tasks you can also find files via Glob/Grep — this map is a shortcut, not a gate.

## Runner / WebApi entry points

| File | What it does |
|---|---|
| `src/AiTestCrew.Runner/Program.cs` | CLI arg parsing, DI wiring, console output |
| `src/AiTestCrew.WebApi/Program.cs` | WebApi DI wiring, CORS, migration, minimal API endpoints |
| `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` | RunAsync with Normal/Reuse/Rebaseline/List modes, module-aware |

## Multi-stack API + multi-env resolution

| File | What it does |
|---|---|
| `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | REST API test generation and execution (multi-stack + multi-env aware via `IApiTargetResolver` + `IEnvironmentResolver`) |
| `src/AiTestCrew.Agents/Auth/ApiTargetResolver.cs` | Resolves API base URLs (env-overridable) and per-(env,stack) token providers |
| `src/AiTestCrew.Agents/Environment/EnvironmentResolver.cs` | `IEnvironmentResolver` impl — per-customer overrides for UI URLs, creds, WinForms path, Bravo DB, per-stack BaseUrls; falls back to top-level config fields |
| `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` | Clones step definitions / test cases and substitutes `{{Tokens}}` using `TokenSubstituter` (lenient). Handles API, WebUi, DesktopUi, aseXml, AseXmlDelivery, VerificationStep shapes plus their runtime test-case counterparts |
| `src/AiTestCrew.Core/Configuration/ApiStackConfig.cs` | Config models for API stacks and modules (`ApiStackConfig`, `ApiModuleConfig`) |
| `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` | Per-customer env config block (URLs, creds, WinForms path, Bravo DB connection string, per-stack BaseUrl overrides) |
| `src/AiTestCrew.Core/Interfaces/IApiTargetResolver.cs` | Interface for multi-stack URL and auth resolution (env-aware overloads) |
| `src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs` | Interface for customer-environment resolution (ResolveKey / ListKeys / per-setting resolvers) |

## Agent base + persistence

| File | What it does |
|---|---|
| `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` | `AskLlmAsync`, `AskLlmForJsonAsync`, `SummariseResultsAsync` |
| `src/AiTestCrew.Agents/Persistence/PersistedModule.cs` | Module model (id, name, description, timestamps) |
| `src/AiTestCrew.Agents/Persistence/ModuleRepository.cs` | CRUD for modules in `modules/{id}/module.json` |
| `src/AiTestCrew.Agents/Persistence/TestSetRepository.cs` | Save/load/move/delete test sets (legacy flat + module-scoped) |
| `src/AiTestCrew.Agents/Persistence/ExecutionHistoryRepository.cs` | Save/load/delete/prune execution runs in `executions/`, auto-retention via `MaxExecutionRunsPerTestSet` |
| `src/AiTestCrew.Agents/Persistence/TestObjective.cs` | Test objective model (wraps ApiTestDefinition or WebUiTestDefinition) |
| `src/AiTestCrew.Agents/ApiAgent/ApiTestDefinition.cs` | API test definition (HTTP request + expected response) |
| `src/AiTestCrew.Agents/Shared/WebUiTestDefinition.cs` | Web UI test definition (start URL + Playwright steps) |
| `src/AiTestCrew.Agents/Persistence/MigrationHelper.cs` | Auto-migrates legacy layouts: `testsets/` → `modules/default/`, v1 → v2 schema |

## WebApi endpoints + run tracking

| File | What it does |
|---|---|
| `src/AiTestCrew.WebApi/Endpoints/ModuleEndpoints.cs` | Module CRUD + nested test set/run/move-objective endpoints |
| `src/AiTestCrew.WebApi/Endpoints/RunEndpoints.cs` | Trigger runs with optional moduleId/testSetId, active run recovery |
| `src/AiTestCrew.WebApi/Services/RunTracker.cs` | In-memory tracking of individual active runs |
| `src/AiTestCrew.WebApi/Services/ModuleRunTracker.cs` | In-memory tracking of module-level composite runs |
| `ui/src/contexts/ActiveRunContext.tsx` | Global run state: module + individual run tracking, polling, page-refresh recovery |
| `src/AiTestCrew.Core/Models/` | `TestTask`, `TestStep`, `TestResult`, `TestSuiteResult`, `RunMode` |

## Web UI + Desktop UI agents

| File | What it does |
|---|---|
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

## aseXML — Generate / Deliver / Verify

| File | What it does |
|---|---|
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
| `templates/asexml/` | Checked-in aseXML templates + manifests grouped by transaction type (copied into each project's `bin/templates/asexml/` at build) |
| `ui/src/components/AseXmlTestCaseTable.tsx` | Read-only viewer for `AseXmlSteps` (no edit dialog in Phase 1) |
| `ui/src/components/AseXmlDeliveryTestCaseTable.tsx` | Read-only viewer for `AseXmlDeliverySteps` (adds Endpoint column) |

## Teardown + data packs

| File | What it does |
|---|---|
| `src/AiTestCrew.Agents/Teardown/BravoTeardownExecutor.cs` | `ITeardownExecutor` impl — runs per-test-set SQL `DELETE` statements once per objective in `Reuse` mode. Per-env opt-in (`DataTeardownEnabled`), guardrail-checked, strict `{{Token}}` substitution from history + env params + delivery FieldValues |
| `src/AiTestCrew.Agents/Teardown/SqlGuardrails.cs` | Static `Validate(sql, allowedExecPrefixes)` — strips `--` and `/* */` comments. Two accepted shapes: (a) constrained DML — requires `WHERE`, rejects all destructive keywords; (b) `EXEC[UTE] [schema.]<proc>` where `<proc>` matches one of `TestEnvironmentConfig.TeardownExecAllowedPrefixes` (default `["usp_"]`). EXEC branch skips WHERE but still rejects further destructive keywords + chained EXEC. |
| `src/AiTestCrew.Agents/DataPack/DataPackRunner.cs` | `IDataPackRunner` impl — runs version-controlled `.sql` files at WebApi startup against per-env Bravo DBs. Bypasses `SqlGuardrails` (dev-authored). Per-env opt-in (`RunDataPacksOnStartup`); per-batch autocommit; abort-env-on-failure. `LatestReport` exposed via `/api/data-packs/startup-report` for the dashboard panel |
| `src/AiTestCrew.Agents/DataPack/DataPackRegistry.cs` | Pure file-walk discovering `bin/datapacks/<phase>/<envKey>/<NN.subfolder>/<NN.script>.sql`; phase order fixed `["datateardown", "datapreparation"]` |
| `src/AiTestCrew.Agents/DataPack/SqlBatchSplitter.cs` | Splits T-SQL on standalone `GO` lines; comment- and string-aware (state machine carries across line boundaries) |
| `src/AiTestCrew.WebApi/Endpoints/DataPackEndpoints.cs` | `GET /api/data-packs/startup-report` — JSON for the dashboard panel |
| `ui/src/components/DataPacksPanel.tsx` | Dashboard panel above the modules grid (auto-refresh 30 s, expandable per-script rows with verbatim SQL errors) |
| `data/datapacks/{datateardown\|datapreparation}/<envKey>/<NN.subfolder>/<NN.script>.sql` | Authored content (numeric prefixes drive order). Packaged into `bin/datapacks/` by the WebApi `.csproj` Content Include |
| `src/AiTestCrew.Core/Interfaces/ITeardownExecutor.cs` | Teardown executor contract + `SqlTeardownStepDto` |
| `src/AiTestCrew.Storage/Persistence/SqlTeardownStep.cs` | Persisted teardown step `{ Name, Sql }` on `PersistedTestSet.TeardownSteps` |
| `ui/src/components/TeardownStepsPanel.tsx` | Editor for `TeardownSteps` on the test set detail page (mirrors `SetupStepsPanel`); shows env-opt-in warning |

## Distributed execution (agents, queue, deferred verification)

| File | What it does |
|---|---|
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
| `src/AiTestCrew.Runner/AgentMode/JobExecutor.cs` | Bridges a dequeued job to `TestOrchestrator.RunAsync` — detects `"kind": "DeferredVerification"` payloads and routes to VerifyOnly; treats `AwaitingVerification` results as success (not a failure to report); catches `AuthRequiredException` on regular AND deferred branches via `TryParkOnAuthRefreshAsync`; settles the auth-refresh row after `ExecuteAuthSetupAsync` |
| `src/AiTestCrew.Runner/RemoteRepositories/ApiClientRunQueueRepository.cs` | HTTP-backed `IRunQueueRepository` — agent enqueues deferred / retry rows via `POST /api/queue`. Non-hot-path methods throw `NotSupportedException` |
| `src/AiTestCrew.Runner/RemoteRepositories/ApiClientPendingVerificationRepository.cs` | HTTP-backed `IPendingVerificationRepository` over `/api/pending-verifications/*` |
| `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs` | Delivery agent with deferred path — `TryEnqueueDeferredVerifications` + `DeferredVerifyAsync` (retry-via-reenqueue) + `TryFinaliseParentRunAsync` (transactional merge + summary regeneration) |
| `ui/src/components/AgentsPanel.tsx` | Dashboard panel — agents with status dot, capabilities, owner, current job |
| `ui/src/components/QueueBanner.tsx` | Dashboard banner — active queued/claimed/running jobs + cancel button; deferred entries (`notBeforeAt` in future) show "Deferred verification — next attempt in ~N min" |
| `ui/src/components/StepList.tsx` | Step rendering; `AwaitingVerification` steps get a cyan ⏳ pill and live countdown parsed from `firstDueAtUtc` in step detail |
| `ui/src/components/TriggerRunButton.tsx` + `TriggerObjectiveRunButton.tsx` | Trigger buttons — swap the spinner for a quiet cyan ⏳ chip during `AwaitingVerification` so the UI doesn't falsely imply active execution |

## Seamless authentication recovery

| File | What it does |
|---|---|
| `src/AiTestCrew.Core/Exceptions/AuthRequiredException.cs` | Thrown by API + UI agents when auth recovery isn't possible. Carries `(env, surface, stack?)` so the dispatcher knows what to refresh |
| `src/AiTestCrew.Core/Models/AgentAuthState.cs` | Pre-flight auth-health row — one per (agent, env, surface), reported on heartbeat |
| `src/AiTestCrew.Agents/Auth/AuthStateScanner.cs` | Read-only file-mtime scanner the agent runs every heartbeat; respects `EnvironmentConfig.AuthHealthEnabled` |
| `src/AiTestCrew.Agents/Recording/RecordingService.cs` | `AuthSetupAsync` — Blazor branch requires `sawSsoRedirect` (positive proof the URL visited login.microsoftonline.com) AND non-empty `CookiesAsync()` before saving state, so an empty context can't be saved silently |
| `src/AiTestCrew.Storage/Sqlite/SqliteAuthRefreshRepository.cs` | `run_auth_refreshes` CRUD with dedup-by-scope via the unique partial index |
| `src/AiTestCrew.Storage/Sqlite/SqliteAgentAuthStateRepository.cs` | `agent_auth_state` upsert/list — `ReplaceForAgentAsync` is wholesale-replace per heartbeat, `ListForOnlineAgentsAsync` joins on `agents` to filter Offline |
| `src/AiTestCrew.WebApi/Endpoints/AuthRefreshEndpoints.cs` | `/api/auth-refreshes/*` — insert/list/start/complete/fail/cancel. `/start` enqueues an `AuthSetup` queue entry with `target` (TestTargetType) + `authRefreshId` in the payload |
| `src/AiTestCrew.WebApi/Endpoints/AuthHealthEndpoints.cs` | `GET /api/auth-health` — per-env tile aggregation, drops Fresh + active-refresh surfaces, drops envs with `AuthHealthEnabled = false` |
| `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` (`SweepAuthRefreshesAsync`) | 30 s sweeps: timeout stale `InProgress` past `Auth.AuthRefreshMaxLatencySeconds`; release dependent queue entries on Completed; cancel them on Failed |
| `ui/src/components/AuthRefreshBanner.tsx` | Reactive banner above the modules grid — appears when at least one `run_auth_refreshes` row is `Pending`/`InProgress` |
| `ui/src/components/AuthHealthPanel.tsx` | Pre-flight panel — env-grouped tiles with per-surface Refresh button. Polls `/api/auth-health` every 30 s |

## DB Assert step

| File | What it does |
|---|---|
| `src/AiTestCrew.Storage/DbAgent/DbCheckStepDefinition.cs` | Persistence model — `Sql`, `ConnectionKey`, `ExpectedRowCount`, `ColumnAssertions`, `Captures`, `TimeoutSeconds`; legacy `expectedColumnValues` JSON shim promotes old dict into `ColumnAssertions` on deserialise |
| `src/AiTestCrew.Storage/DbAgent/ColumnAssertion.cs` | Per-column assertion — `Column`, `JsonPath`, `Operator`, `Expected`, `Expected2`, `IgnoreCase`, `ToleranceSeconds`, `ToleranceDelta` |
| `src/AiTestCrew.Storage/DbAgent/ColumnCapture.cs` | Post-assertion token capture — `Column`, `JsonPath`, `As` (token name, not substituted), `Required` |
| `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs` | 14-value enum: `Equals` / `NotEquals` / `Contains` / `NotContains` / `StartsWith` / `EndsWith` / `Regex` / `GreaterThan` / `LessThan` / `Between` / `IsNull` / `IsNotNull` / `EqualsNumeric` / `EqualsDate` |
| `src/AiTestCrew.Agents/DbAgent/DbCheckAgent.cs` | Post-step-only agent — resolves per-check connection via `IEnvironmentResolver.ResolveDbConnectionString`, runs row-count or column-assertion mode, evaluates captures, attaches `dbCheckRow`/`capturedTokens` to step metadata |
| `src/AiTestCrew.Agents/DbAgent/ColumnAssertionEvaluator.cs` | Pure static evaluator — NULL fidelity, JSONPath extraction, numeric/date tolerance, regex; returns typed `EvaluateResult(Passed, Reason)`, never throws |
| `src/AiTestCrew.Agents/DbAgent/JsonValueExtractor.cs` | Thin wrapper over `JsonPath.Net` (`json-everything`, MIT); returns typed `ExtractionStatus` (Found / FoundNull / NotJson / InvalidPath / NotFound) |
| `src/AiTestCrew.Agents/DbAgent/DbCheckSqlGuardrails.cs` | Read-only enforcement — rejects non-SELECT, semicolons, and a denied-keyword list; allows CTEs |
| `src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs` | Added `ResolveDbConnectionString`, `ResolveAllowDbDryRun`, `ListDbConnectionKeys` |
| `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` | Added `DbConnections: Dictionary<string,string>` and `AllowDbDryRun: bool` (default true) per env |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Added top-level `DbConnections: Dictionary<string,string>` fallback |
| `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` | Added `CapturedTokens: Dictionary<string,string>` — carries inline-sibling captures into deferred post-steps |
| `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs` | Merges `capturedTokens` from each completed child step into the working context (captured > existing); threads `CapturedTokens` through the deferred path; forwards `dbCheckRow` metadata through to parent run detail |
| `src/AiTestCrew.WebApi/Endpoints/DbCheckEndpoints.cs` | `POST /api/db-check/dry-run` (rate-limited "Try query" preview, per-env opt-out) and `GET /api/db-check/connections` (connection-key list for the editor dropdown) |
| `src/AiTestCrew.WebApi/Services/DbDryRunRateLimiter.cs` | In-memory fixed-window token bucket — 10 requests/minute/user; `Sweep(nowUtc)` called by `AgentHeartbeatMonitor` on a 5-minute cadence |
| `ui/src/components/EditDbCheckStepDialog.tsx` | Full editor dialog — connection dropdown, SQL textarea, mode radio, assertions table, captures table, timeout, "Try query" preview with per-cell `+` to add assertions |
| `ui/src/api/dbCheck.ts` | `dryRunDbCheck(envKey, connectionKey, sql, parameters)` + `getDbConnections(envKey)` API clients |
| `ui/src/components/PostStepsPanel.tsx` | Wires `EditDbCheckStepDialog` into the edit-pencil branch; updated `DbCheckBlock` renders assertions + captures pills; updated `PayloadSummary` shows assertion/capture counts |
| `ui/src/components/StepList.tsx` | `extractDbDiagnostics` reads `dbCheckRow`/`dbCheckRows` from step metadata; `DbDiagnosticsTable` renders the failing row as a column→value table under the failure reason |
| `.claude/commands/add-db-assert.md` | `/add-db-assert` skill — validates parent step, calls dry-run, PUTs the post-step; mirrors `/add-asexml-verification` for DB assertions |
