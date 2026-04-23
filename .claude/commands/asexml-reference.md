Reference guide for the aseXML subsystem (AEMO B2B transaction test cases). Consult this before extending or debugging the Generate / Deliver / Verify pipeline, or when authoring new transaction types, endpoints, or verifications.

## What the aseXML subsystem is

Three stacked agents that together exercise an inbound AEMO B2B transaction end-to-end:

```
AseXml_Generate → AseXml_Deliver → (wait) → UI verification (sibling agent)
```

The whole pipeline is a **single `TestObjective`** under a module / test set. A delivery case owns its post-delivery verifications; verifications run in-process via sibling-agent dispatch, not as separate tasks.

## Three phases, three agents

### Phase 1 — `AseXml_Generate` (`AseXmlGenerationAgent`)

Renders an aseXML payload from a template + user field values. Output = XML file on disk under `output/asexml/{timestamp}_{taskId}/`.

- **Templates** are a `.xml` + `.manifest.json` pair under `templates/asexml/{TransactionType}/`. Copied to each project's `bin/templates/asexml/` at build via `<Content>` glob.
- **Manifest fields** have one of three `source` values: `auto` (generators), `user` (runtime values), `const` (hardwired).
- **Auto generators**: `messageId`, `transactionId`, `nowOffset`, `today`. Patterns may contain `{rand8}` for 8-char uppercase alphanumeric.
- **Determinism**: `AseXmlRenderer.Render(manifest, body, userValues)` is a pure function — returns `(xmlString, resolvedFields)`. No LLM involvement in rendering; the LLM only picks a template and extracts values from the objective.
- **Token grammar** (`{{FieldName}}`) is defined in `src/AiTestCrew.Core/Utilities/TokenSubstituter.cs` and shared across the subsystem.

Extension → `/add-asexml-template`.

### Phase 2 — `AseXml_Deliver` (`AseXmlDeliveryAgent`)

Self-contained — renders the XML via the shared renderer, then uploads to a Bravo inbound drop location. Does not require a preceding Generate task.

- **Endpoint resolution**: `IEndpointResolver` → `BravoEndpointResolver` queries `mil.V2_MIL_EndPoint` by `EndPointCode`. Result cached for process lifetime. Connection string lives in `AseXml.BravoDb.ConnectionString` (in `appsettings.json` only — never `.example.json`).
- **BravoEndpoint record**: `EndPointCode, FtpServer, UserName, Password, OutBoxUrl, IsOutboundFilesZipped`.
- **Protocol selection**: `DropTargetFactory.Create(endpoint)` inspects the `OutBoxUrl` scheme — `sftp://` → `SftpDropTarget` (SSH.NET), `ftp://` → `FtpDropTarget` (FluentFTP), default SFTP.
- **Zip wrapping**: when `IsOutboundFilesZipped = true`, `XmlZipPackager.Package(xml, "{MessageID}.xml")` builds a single-entry zip and the upload filename becomes `{MessageID}.zip`. Local debug copy is written as both `.xml` and `.zip`.
- **Remote filename convention**: `{MessageID}.xml` (or `.zip`). Matches the AEMO sample filename pattern.
- **Security**: `BravoEndpoint.Password` is never logged. Every log touches code + user + host + OutBoxUrl + zip flag only.

Extension → `/add-delivery-protocol` (new `IXmlDropTarget`).

### Phase 3 — Post-delivery UI verifications

Same delivery agent chains in UI steps after upload. Values from the render context (`MessageID`, `TransactionID`, `Filename`, `EndpointCode`, `UploadedAs`, plus every resolved manifest field) substitute into every string field of the UI steps via `{{Token}}`.

- **Attachment**: `AseXmlDeliveryTestDefinition.PostDeliveryVerifications: List<VerificationStep>`.
- **Verification shape**: `{ Description, Target, WaitBeforeSeconds, WebUi?, DesktopUi? }`. Target string matches `TestTargetType` enum: `UI_Web_MVC`, `UI_Web_Blazor`, `UI_Desktop_WinForms`.
- **Wait**: `WaitBeforeSeconds` is the **deadline for a green result**, not the time to wait before a single attempt. With `AseXml.DeferVerifications=true` (default) and `WaitBeforeSeconds > VerificationDeferThresholdSeconds` (default 30), the delivery agent queues the verification instead of blocking on `Task.Delay` and re-enqueues failed attempts at `VerificationRetryIntervalSeconds` up to `wait + VerificationGraceSeconds`. See `docs/architecture.md → Deferred Post-Delivery Verification` and `/tune-deferred-verification`.
- **Execution**: `AseXmlDeliveryAgent.RunVerificationAsync` deep-clones the definition, substitutes tokens on every string field, builds a synthetic `TestTask` with the UI target + `Parameters["PreloadedTestCases"]`, and dispatches to the matching sibling agent via `CanHandleAsync`. When deferred, the same method is invoked later via `DeferredVerifyAsync` using a self-contained `DeferredVerificationRequest` snapshot (so context doesn't race with a concurrent re-delivery).
- **Sibling resolution**: `AseXmlDeliveryAgent` takes `IServiceProvider` (not `IEnumerable<ITestAgent>`) and lazily materialises siblings — avoids DI recursion because the agent itself is an `ITestAgent`.
- **Recording**: `--record-verification` loads the delivery's `FieldValues` plus the latest successful run's MessageID / TransactionID / Filename from execution history, launches the matching recorder, and auto-parameterises captured literals (min length 4, longest-match-first, exact substring, first-key-wins).
- **Recording auth**: `UI_Web_Blazor` uses `BraveCloudUiStorageStatePath`, `UI_Web_MVC` uses `LegacyWebUiStorageStatePath`. Prints a hint and runs unauthenticated if the path isn't cached. Run `--auth-setup --target <UI_*>` first.
- **Web UI editing**: the generic `EditWebUiTestCaseDialog.tsx` is reused for verification step editing (pencil icon in the verifications panel). Desktop UI edits are delete-and-re-record for now.

Extension → `/add-asexml-verification` (new verification on existing delivery), or `/add-agent` if you need a new UI surface entirely.

## Key files (by phase)

### Core contracts
- `src/AiTestCrew.Core/Models/Enums.cs` — `TestTargetType.AseXml_Generate`, `AseXml_Deliver`.
- `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` — `AseXmlConfig`, `BravoDbConfig`.
- `src/AiTestCrew.Core/Interfaces/IEndpointResolver.cs` — `BravoEndpoint` record.
- `src/AiTestCrew.Core/Utilities/TokenSubstituter.cs` — `{{Token}}` regex + substitute helpers.

### Generate
- `src/AiTestCrew.Agents/AseXmlAgent/AseXmlGenerationAgent.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/AseXmlTestDefinition.cs` (step persistence model)
- `src/AiTestCrew.Agents/AseXmlAgent/Templates/TemplateManifest.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Templates/TemplateRegistry.cs` (startup scan)
- `src/AiTestCrew.Agents/AseXmlAgent/Templates/AseXmlRenderer.cs` (pure render)
- `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs`
- `templates/asexml/MeterFaultAndIssueNotification/MFN-OneInAllIn.*` (seed template)

### Deliver
- `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryTestDefinition.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/IXmlDropTarget.cs` (+ `DeliveryReceipt`)
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/SftpDropTarget.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/FtpDropTarget.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/DropTargetFactory.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/BravoEndpointResolver.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Delivery/XmlZipPackager.cs`

### Verify (Phase 3)
- `src/AiTestCrew.Agents/AseXmlAgent/VerificationStep.cs`
- `src/AiTestCrew.Agents/AseXmlAgent/Recording/VerificationRecorderHelper.cs` (auto-parameterisation)
- `src/AiTestCrew.Agents/Persistence/ExecutionHistoryRepository.cs` — `GetLatestDeliveryContextAsync`
- `src/AiTestCrew.Agents/Persistence/PersistedExecutionRun.cs` — `PersistedDelivery` typed record for history

### Deferred verification (v6 coordination)
- `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` — self-contained snapshot carried in the queue entry
- `src/AiTestCrew.Core/Models/PendingVerification.cs` + `src/AiTestCrew.Core/Interfaces/IPendingVerificationRepository.cs`
- `src/AiTestCrew.Storage/Sqlite/SqlitePendingVerificationRepository.cs` — server-side impl
- `src/AiTestCrew.Runner/RemoteRepositories/ApiClientPendingVerificationRepository.cs` + `ApiClientRunQueueRepository.cs` — REST clients for distributed (Docker WebApi + PC agent) deployments
- `src/AiTestCrew.WebApi/Endpoints/PendingVerificationEndpoints.cs` + `QueueEndpoints.cs` — REST surface
- `src/AiTestCrew.Agents/AseXmlAgent/AseXmlDeliveryAgent.cs` — `TryEnqueueDeferredVerifications` / `DeferredVerifyAsync` / `TryFinaliseParentRunAsync`
- `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` — janitor sweeps (stale-claim reclaim + deadline expiry)
- Full write-up: `docs/architecture.md → Deferred Post-Delivery Verification`; tuning: `/tune-deferred-verification`.

### UI
- `ui/src/components/AseXmlTestCaseTable.tsx` (generation — read-only)
- `ui/src/components/AseXmlDeliveryTestCaseTable.tsx` (delivery — read-only; nested verifications panel with view/edit/delete)
- `ui/src/components/EditWebUiTestDialog.tsx` (generic — reused for verification Web UI editing)

### Runner + CLI
- `src/AiTestCrew.Runner/Program.cs` — `--record-verification` handler, `--list-endpoints`, `--endpoint`, `--objective` with Id-or-Name matching.

### WebApi
- `src/AiTestCrew.WebApi/Endpoints/ModuleEndpoints.cs` — verification `PUT` + `DELETE` endpoints.

## Data model summary

```
PersistedTestSet
 ├─ Id, Name, ModuleId, ApiStackKey?, ApiModule?, EndpointCode?
 ├─ Objectives[]                    ← user-objective texts
 ├─ ObjectiveNames{}                ← display-name map
 └─ TestObjectives[]
      ├─ Id (slug), Name, ParentObjective, AgentName, TargetType, Source
      ├─ ApiSteps[]                 ← ApiTestDefinition
      ├─ WebUiSteps[]               ← WebUiTestDefinition
      ├─ DesktopUiSteps[]           ← DesktopUiTestDefinition
      ├─ AseXmlSteps[]              ← AseXmlTestDefinition (Generate)
      └─ AseXmlDeliverySteps[]      ← AseXmlDeliveryTestDefinition (Deliver)
           ├─ Description, TemplateId, TransactionType, FieldValues{}
           ├─ EndpointCode, ValidateAgainstSchema
           └─ PostDeliveryVerifications[]
                ├─ Description, Target, WaitBeforeSeconds
                ├─ WebUi?: WebUiTestDefinition
                └─ DesktopUi?: DesktopUiTestDefinition
```

## CLI cheat-sheet (aseXML-specific)

```bash
# Generate only (LLM chooses template + fills values)
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-tests \
  --obj-name "MFN generate smoke" \
  "Generate an MFN for NMI 4103035611 ..."

# Discover available Bravo endpoints
dotnet run --project src/AiTestCrew.Runner -- --list-endpoints

# Deliver (renders + uploads)
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-delivery \
  --endpoint GatewaySPARQ \
  --obj-name "MFN deliver" \
  "Deliver an MFN for NMI 4103035611 to GatewaySPARQ ..."

# Record a post-delivery UI verification (pre-req: --auth-setup --target <UI_*>)
dotnet run --project src/AiTestCrew.Runner -- --record-verification \
  --module aemo-b2b --testset mfn-delivery \
  --objective "MFN deliver" \
  --target UI_Web_Blazor \
  --verification-name "MFN Process Overview shows 'One In All In'" \
  --wait 30

# Re-run the whole thing (delivery + verifications run together)
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-delivery \
  --reuse mfn-delivery

# Re-run one specific objective in the set (accepts Id slug OR display Name)
dotnet run --project src/AiTestCrew.Runner -- \
  --module aemo-b2b --testset mfn-delivery \
  --reuse mfn-delivery \
  --objective "MFN deliver"
```

## Invariants — do NOT violate

- **Templates are pure content.** The renderer logic lives in `AseXmlRenderer` and handles all substitution, escaping, and validation. No per-transaction-type C# code.
- **The LLM writes field values, never XML.** If a prompt asks the LLM to emit XML bodies, the determinism of the renderer is broken.
- **Auto fields go through generators.** Never hardcode MessageID / TransactionID / timestamps.
- **Passwords never hit logs.** `BravoEndpoint.Password` is fetched from DB, passed to the `IXmlDropTarget`, and never surfaces in step details, summaries, or error messages.
- **Verification targets match the sibling-agent system.** `VerificationStep.Target` is a `TestTargetType` string — any new UI surface must have an `ITestAgent` whose `CanHandleAsync` returns true for that value.
- **Endpoint selection is test-case-level**, not template-level. Templates never name an endpoint; `AseXmlDeliveryTestDefinition.EndpointCode` does.
- **`IServiceProvider` for sibling resolution, not `IEnumerable<ITestAgent>`.** The delivery agent is itself registered as `ITestAgent`; eager enumerable injection recurses into its own factory.

## When to reach for which skill

| Task | Skill |
|---|---|
| Add a new transaction type (MFN, CDN, MDM, etc.) | `/add-asexml-template` |
| Add a new delivery protocol (AS2, HTTP POST, SMB) | `/add-delivery-protocol` |
| Record a verification on an existing delivery case | `/add-asexml-verification` |
| Add a new test target type entirely (not aseXML-related) | `/add-agent` |
| Add a response validation rule | `/add-validation` |
| Review an agent implementation | `/review-agent` |

## Related reference skills

- `/blazor-cloud-reference` — Blazor/MudBlazor recording + replay patterns for Brave Cloud UI verifications.
- `/bravo-web-reference` — Legacy MVC + Kendo UI patterns for Legacy Web verifications.
- `/desktop-winui-reference` — FlaUI patterns for WinForms verifications.
