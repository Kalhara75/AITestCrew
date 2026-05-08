---
id: REQ-004
title: Azure Service Bus event assertion post-step — declarative event matching, value capture, multi-namespace, in-UI editor with peek preview
status: Proposed
created: 2026-05-09
author: Kalhara Samarasinghe
area: agents + ui
---

# REQ-004 — Azure Service Bus event assertion post-step

## Goal

Make "events the system under test should have raised onto Azure Service Bus" a first-class verification surface that any parent test step (API, Web UI, Desktop UI, aseXML deliver, aseXML generate) can pin onto. Test authors declare what messages they expect; the framework connects, receives, matches, captures values into `{{Token}}`, and emits a structured pass/fail with diagnostics that name the closest non-matching message.

The new step is a **post-step**, not a top-level objective — its signal is meaningless without a preceding action that should have caused the event. Same shape rule that REQ-002 locked in for `DbCheckAgent` and that the existing `VerificationStep` carrier already enforces.

Concretely the slice ships:

1. **Declarative event expectation DSL** — a connection key + entity (queue, or topic+subscription), a match mode (`AnyMessage` / `AllMessages` / `ExactlyOne` / `MinCount`/`MaxCount`), a list of field criteria (system properties + application properties + JSON body via JSONPath, XML body via XPath), a receive timeout, and an optional drain-before-parent toggle.
2. **Value capture** — bind a system property, application property, or JSON-extracted scalar from the FIRST matching message into the per-objective post-step run context as `{{Token}}` for sibling post-steps. Reuses REQ-002's capture plumbing end-to-end (`PostStepOrchestrator` merge + `DeferredVerificationRequest.CapturedTokens`).
3. **Multi-namespace registry** — `ServiceBusConnections.<connectionKey>` parallel to REQ-002's `DbConnections`, supporting connection-string and Azure AD (`DefaultAzureCredential`) auth, configurable per-customer-env with top-level fallback. Zero hardcoded namespaces.
4. **Reuse of REQ-002's operator surface** — `AssertionOperator` (Equals / NotEquals / Contains / NotContains / StartsWith / EndsWith / Regex / GreaterThan / LessThan / Between / IsNull / IsNotNull / EqualsNumeric / EqualsDate) lifts verbatim from `DbCheckStepDefinition`. The body-JSON path evaluator reuses the same JsonPath.Net wrapper REQ-002 introduces.
5. **In-UI editor with peek preview** — `EditEventAssertStepDialog.tsx` parallel to `EditDbCheckStepDialog.tsx`, with a "Peek messages" button that calls a new `POST /api/event-assert/peek` endpoint to render the most recent N messages in the queue/subscription without consuming them. Each row has a `+` button to seed a criterion or capture from the message's actual fields.
6. **`/add-event-assert` slash command + chat NL authoring** — parallel to `/add-db-assert`. Same `confirmCreatePostStep` chat action, new `eventAssert` payload shape.
7. **New distributed-execution capability `Event_AzureServiceBus`** — the agent advertises it; a remote agent with outbound access to the customer's Service Bus namespace can pick the post-step up via the existing run-queue path.

## Why now

- REQ-002 has just landed the post-step infrastructure that makes this feasible at low cost: the `VerificationStep` carrier accepts heterogeneous payloads (`DbCheck`, `WebUi`, `DesktopUi`, `AseXmlDeliver`, etc.); `PostStepOrchestrator` merges captured tokens into siblings; `DeferredVerificationRequest.CapturedTokens` round-trips them to deferred siblings; `IEnvironmentResolver.ResolveDbConnectionString` set the multi-key registry pattern. This requirement is mostly **a new payload field, a new agent, a new resolver method, a new editor dialog** — none of those slices are speculative.
- The Bravo platform increasingly publishes domain events to Service Bus (job-status changes, downstream sends, B2B inbound notifications). End-to-end tests today either sample the DB (REQ-002) or wait on UI side-effects (REQ-003); when the only observable signal is an enqueued event, there is no way to assert it without a hand-rolled receiver script.
- Adding it now (while the post-step capture-token plumbing is hot in everyone's head) is cheaper than retrofitting later when the merge precedence rules and deferred round-trip have faded.

## Current-state findings (from audit — do NOT redo these)

✅ **Already in place — extend, don't rebuild:**

| Component | Path | Status |
|---|---|---|
| `VerificationStep` carrier (heterogeneous post-step payload) | `src/AiTestCrew.Storage/AseXmlAgent/VerificationStep.cs` | Add a new optional `EventAssert: EventAssertStepDefinition` field next to `DbCheck`. `Target` selects which agent picks the step up. |
| `AssertionOperator` enum + operator semantics | `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs` | Reuse 1:1. Lift the operator-evaluation function out of `ColumnAssertionEvaluator` into a `ScalarOperatorEvaluator` shared helper (REQ-002's evaluator is column-row-oriented; the operator dispatch is the reusable part). |
| `ColumnCapture` shape (token binding) | `src/AiTestCrew.Storage/DbAgent/ColumnCapture.cs` | Mirror its shape for `EventCapture` — `Field`, `JsonPath`, `As`, `Required`. Don't share the type itself (DB and Service Bus have different "which field" semantics). |
| Capture-token runtime path | `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs`, `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` | Wired by REQ-002. Service Bus captures emit into `Metadata["capturedTokens"]` the same way DB captures do. Zero orchestrator change. |
| Multi-key registry pattern | `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` (`DbConnections`) + `IEnvironmentResolver.ResolveDbConnectionString` | Mirror as `ServiceBusConnections` + `ResolveServiceBusConnection(connectionKey, envKey)`. |
| Post-step distributed dispatch | `src/AiTestCrew.Runner/AgentMode/JobExecutor.cs`, `src/AiTestCrew.Agents/RunQueue/*` | A new capability string (`"Event_AzureServiceBus"`) plus the agent's `CanHandleAsync` returning true for `TestTargetType.Event_AzureServiceBus` is the only wiring. |
| Token substitution | `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` | Add an overload for `EventAssertStepDefinition`. Substitute every string field inside `Criteria[*].Expected` / `Expected2` / `JsonPath` / `XPath` / `Field`, plus connection-level overrides if any (`SubscriptionName`, `CorrelationFilter`). |
| Chat post-step authoring | `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` (`confirmCreatePostStep`) | Extend the LLM-facing payload schema with `eventAssert`. |
| Dry-run / preview endpoint pattern | `src/AiTestCrew.WebApi/Endpoints/DbCheckEndpoints.cs` | Mirror as `EventAssertEndpoints.cs` with `POST /api/event-assert/peek`. Same auth + per-env opt-in shape. |
| Read-only display block + edit dialog conventions | `ui/src/components/PostStepsPanel.tsx`, `EditDbCheckStepDialog.tsx` | New `EventAssertBlock` + `EditEventAssertStepDialog.tsx` slot in next to the `dbCheck` branch. |

❌ **Gaps this requirement addresses:**

1. **No event-assertion primitive at all.** `MessageBus` exists in `TestTargetType` but no agent handles it; nothing ever emits a payload for that target.
2. **No Service Bus client wiring.** `Azure.Messaging.ServiceBus` is not referenced in any project. The `DropTargetFactory` (aseXML delivery) handles SFTP / S3 / Azure Blob today; Service Bus isn't there.
3. **No event-side `ServiceBusConnections` registry.** All Azure surface is currently storage account-only, configured ad-hoc per `IXmlDropTarget`.
4. **No body-parsing dispatch.** REQ-002's JSONPath usage assumes the column value is a JSON string; for messages we need to dispatch on `ContentType` / declared `BodyFormat` (JSON vs XML vs Text).
5. **No ordering semantics.** REQ-002 only asserts on the FIRST row. For event matching, "all messages match" / "any message matches" / "exactly one matches" / "at least N match" are real test patterns.
6. **No timeout / wait model.** REQ-002 reads settled DB state with a single `CommandTimeout`. Service Bus is async-arrival; we need a "receive until first match OR until timeout, then evaluate" loop.
7. **No drain-before-parent hook.** Stale messages from prior failed runs would otherwise contaminate match results. The orchestrator currently has no pre-parent hook; this requirement adds one.
8. **No editor dialog.** Today there is no surface to author a Service Bus assertion; the chat-only path won't be usable until the editor exists.

## Scope — what's in

### 1. New step model — `EventAssertStepDefinition`

New file `src/AiTestCrew.Storage/EventAssertAgent/EventAssertStepDefinition.cs`. Shape:

```csharp
public class EventAssertStepDefinition
{
    public string Name { get; set; } = "";

    public string ConnectionKey { get; set; } = "";        // resolved via IEnvironmentResolver
    public ServiceBusEntity Entity { get; set; } = new();   // Queue OR Topic+Subscription

    public BodyFormat BodyFormat { get; set; } = BodyFormat.Auto;  // Auto = sniff from ContentType
    public ReceiveMode ReceiveMode { get; set; } = ReceiveMode.PeekLock;

    public MatchMode MatchMode { get; set; } = MatchMode.AnyMessage;
    public int? ExpectedCount { get; set; }                // for ExactCount / MinCount / MaxCount
    public int? MaxCount { get; set; }                     // for MinCount/MaxCount range; ExpectedCount=min, MaxCount=max
    public int TimeoutSeconds { get; set; } = 30;          // total receive window after parent
    public int MaxMessages { get; set; } = 50;             // hard cap on messages drained for evaluation
    public bool DrainBeforeParent { get; set; } = false;   // empty queue/sub before the parent runs
    public bool CompleteOnPass { get; set; } = true;       // on PeekLock: settle messages on green; abandon on red
    public string? CorrelationFilter { get; set; }         // optional pre-filter on CorrelationId (token-substituted)
    public string? SessionId { get; set; }                 // optional session receiver target

    public List<EventCriterion> Criteria { get; set; } = [];
    public List<EventCapture> Captures { get; set; } = [];
}

public class ServiceBusEntity
{
    public ServiceBusEntityType Type { get; set; } = ServiceBusEntityType.Queue;
    public string Name { get; set; } = "";                 // queue name OR topic name
    public string? SubscriptionName { get; set; }          // required when Type == Topic
}

public enum ServiceBusEntityType { Queue, Topic }
public enum BodyFormat { Auto, Json, Xml, Text, Binary }   // Binary disables Body.* paths; matchable via system props only
public enum ReceiveMode { PeekLock, ReceiveAndDelete }
public enum MatchMode { AnyMessage, AllMessages, ExactlyOne, ExactCount, MinCount, MaxCount, CountRange }
```

```csharp
public class EventCriterion
{
    public string Field { get; set; } = "";    // see Field path syntax below
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;
    public string Expected { get; set; } = "";
    public string? Expected2 { get; set; }      // for Between
    public bool IgnoreCase { get; set; } = true;
    public double? ToleranceDelta { get; set; }
    public int? ToleranceSeconds { get; set; }
}

public class EventCapture
{
    public string Field { get; set; } = "";    // same path syntax as EventCriterion.Field
    public string As { get; set; } = "";
    public bool Required { get; set; } = true;
}
```

**Field path syntax** (identical for `EventCriterion.Field` and `EventCapture.Field`):

| Prefix | Meaning | Example |
|---|---|---|
| `MessageId`, `CorrelationId`, `Subject`, `ContentType`, `ReplyTo`, `To`, `SessionId`, `EnqueuedTimeUtc`, `DeliveryCount`, `PartitionKey` | System property | `CorrelationId` |
| `ApplicationProperties.<name>` | Custom property dictionary | `ApplicationProperties.EventType` |
| `Body.<jsonpath>` | JSONPath into the parsed body (when `BodyFormat=Json` or `Auto` resolves to JSON) | `Body.Order.Id`, `Body.Items[0].Sku` |
| `BodyXml.<xpath>` | XPath into the parsed body (when `BodyFormat=Xml` or `Auto` resolves to XML — e.g. `Content-Type: application/xml`) | `BodyXml.//Order/@Id` |
| `BodyText` | Raw body as a UTF-8 string | `BodyText` |
| `BodyLength` | Byte length of the raw body | `BodyLength` |

Path evaluation is uniform: extract a scalar (or JSON-text projection of an object/array), then dispatch to `ScalarOperatorEvaluator.Evaluate(operator, actual, criterion)` (the lifted-out helper from REQ-002).

### 2. New target type + agent

Add `Event_AzureServiceBus` to `TestTargetType` (`src/AiTestCrew.Core/Models/Enums.cs`). Update `VerificationStep.Target` XML doc to list it.

New agent `src/AiTestCrew.Agents/EventAssertAgent/AzureServiceBusEventAgent.cs`:

- Extends `BaseTestAgent`; capability `"Event_AzureServiceBus"`; `CanHandleAsync` returns true for `TestTargetType.Event_AzureServiceBus`.
- Post-step-only: documented + enforced the same way `DbCheckAgent` documents its constraint (an event assertion without a preceding action has no signal).
- Loops over `task.Parameters["PreloadedTestCases"]: List<EventAssertStepDefinition>`, applies env-token substitution, executes each.

**Per-step execution:**

1. Resolve connection via `IEnvironmentResolver.ResolveServiceBusConnection(connectionKey, envKey)` → `ServiceBusConnectionInfo { Namespace, AuthMode, ConnectionString? }`.
2. If `DrainBeforeParent` was honored upstream (orchestrator hook, see §4), receive starts with a clean entity.
3. Open a `ServiceBusReceiver` (via cached `ServiceBusClient`) for the queue or topic+subscription.
4. Loop until `MaxMessages` reached OR `TimeoutSeconds` elapsed OR (for `AnyMessage` / `ExactlyOne` / count-bound modes) the early-stop condition is met:
   - `ReceiveMessagesAsync` with a small per-call timeout (default 1s; configurable via `ReceiveBatchTimeoutSeconds` open question).
   - For each received `ServiceBusReceivedMessage`, parse body per `BodyFormat` (Auto: sniff `ContentType`; default JSON if `application/json` or starts-with `{` / `[`, XML if `application/xml` / starts-with `<`, otherwise Text).
   - If `CorrelationFilter` is set, skip messages whose `CorrelationId` doesn't match (substring + token-substituted equality — recommend exact-match; planner: confirm).
   - Evaluate every criterion against the message; record per-message pass/fail.
5. After the loop, evaluate `MatchMode`:
   - `AnyMessage` — pass if at least one message passed all criteria.
   - `AllMessages` — pass if every received message passed all criteria AND at least one message was received.
   - `ExactlyOne` — pass if exactly one message passed.
   - `ExactCount(N)` — pass if exactly `ExpectedCount` messages passed.
   - `MinCount(N)` — pass if `ExpectedCount` or more passed.
   - `MaxCount(N)` — pass if `ExpectedCount` or fewer passed (and ≥ 1; zero is failure unless explicitly allowed via open question).
   - `CountRange` — pass if pass count ∈ `[ExpectedCount, MaxCount]`.
6. **Captures** apply to the FIRST passing message (consistent with REQ-002's first-row rule). Bind via `Metadata["capturedTokens"]` exactly as `DbCheckAgent` does.
7. **Settlement** (PeekLock mode):
   - On pass + `CompleteOnPass=true`: complete every message that passed; abandon every message that didn't pass (so non-matching production traffic flows back).
   - On pass + `CompleteOnPass=false`: abandon all (debug mode — leave messages in place).
   - On fail: abandon all (so the next debug attempt sees the same population).
   - On `ReceiveAndDelete`: nothing to settle.
8. **Diagnostics on failure** — populate `TestStep.Metadata["serviceBusReceived"]` with up to 10 received-message summaries (truncated bodies, system properties, application properties, per-criterion pass/fail). The single-line `Reason` is human-readable; the structured dump is for the run-detail UI.

### 3. Connection registry — `ServiceBusConnections`

Mirror REQ-002's pattern exactly:

```csharp
// EnvironmentConfig.cs
public Dictionary<string, ServiceBusConnectionConfig> ServiceBusConnections { get; set; } = new();

// TestEnvironmentConfig.cs (top-level fallback)
public Dictionary<string, ServiceBusConnectionConfig> ServiceBusConnections { get; set; } = new();
```

Where `ServiceBusConnectionConfig` is:

```csharp
public class ServiceBusConnectionConfig
{
    public ServiceBusAuthMode AuthMode { get; set; } = ServiceBusAuthMode.ConnectionString;
    public string? ConnectionString { get; set; }   // when AuthMode=ConnectionString
    public string? FullyQualifiedNamespace { get; set; }  // when AuthMode=AzureAd, e.g. "myns.servicebus.windows.net"
    public string? ManagedIdentityClientId { get; set; }  // optional, if a UAMI is required
}

public enum ServiceBusAuthMode { ConnectionString, AzureAd }
```

`AzureAd` mode uses `DefaultAzureCredential` so dev (Azure CLI) and prod (managed identity) both work with no code changes. If `ManagedIdentityClientId` is set, pass it to the credential.

`IEnvironmentResolver.ResolveServiceBusConnection(connectionKey, envKey)` returns the resolved `ServiceBusConnectionConfig?`:

- Per-env `Environments.<envKey>.ServiceBusConnections.<connectionKey>` first.
- Top-level `ServiceBusConnections.<connectionKey>` fallback.
- Unknown key → returns null → agent surfaces `TestStatus.Error` (config issue, not data issue) with a message naming the key and env.

`appsettings.example.json` gets a worked example: one connection-string-auth namespace at top level, one Azure AD namespace overridden per env.

### 4. Drain-before-parent — orchestrator hook

The current `PostStepOrchestrator` runs post-steps strictly AFTER the parent. To honor `DrainBeforeParent: true`, the orchestrator must call into the event-assert agent BEFORE the parent runs.

Minimum-cost design (recommended; planner confirm):

- `PostStepOrchestrator.RunInlineAsync` (and the deferred path) inspects each post-step's payload at the start of the parent step's execution. If any payload is `EventAssertStepDefinition` with `DrainBeforeParent=true`, dispatch a small synchronous "drain" call to `AzureServiceBusEventAgent.DrainAsync(connectionKey, entity, envKey, ct)` BEFORE the parent step runs.
- `DrainAsync` opens a receiver in `ReceiveAndDelete` mode and pulls until the queue/sub returns no messages within a 2s idle window or a hard 10s ceiling, whichever comes first. Logs the count drained.
- This is the only orchestrator change in this slice. No new "pre-step" abstraction.

If the planner finds the orchestrator hook is too intrusive (the dispatch is tighter than expected), fall back to a naked `dotnet`-side helper that the test author calls explicitly in a sibling pre-step — but flag it as a downgrade in the plan.

### 5. JsonPath / XPath body evaluation

- **JSON body** — reuse REQ-002's `JsonValueExtractor` (JsonPath.Net wrapper). Same path-not-found semantics: missing path fails the criterion with `"JSON path '$.X' not found in body"`. JSON-null vs missing path follow REQ-002's rules.
- **XML body** — new `XmlValueExtractor` using `System.Xml.XPath`. Returns the inner text of the first matched node, or the attribute value when the XPath ends in `/@attr`. Path-not-found fails with a typed message naming the XPath. Default namespace handling: if the document declares a default namespace, accept paths with `*[local-name()='Tag']` form OR `xmlns:ns="..."` declared via a future extension (open question — keep it simple in v1; users wrap their XPaths in `local-name()` filters).
- **Text body** — `BodyText` returns the raw UTF-8 string; operators apply directly.
- **Binary** — `BodyLength` is the only path. `Body.*` and `BodyXml.*` paths fail the criterion immediately with `"binary body — only system / application properties and BodyLength are matchable"`.
- **Auto sniff** — use `ContentType` if present (`application/json` → JSON, `application/xml` / `text/xml` → XML, otherwise Text). When `ContentType` is empty, sniff the first non-whitespace byte (`{` / `[` → JSON, `<` → XML, else Text).

### 6. Capture into `{{Token}}`

`EventCapture` runtime contract — same as REQ-002's DB capture:

- Captures apply to the FIRST passing message after every criterion passes.
- For `EventCapture.Field`, extract using the same path resolver as criteria.
- Bind into `Metadata["capturedTokens"]` as `Dictionary<string, string>`. `PostStepOrchestrator` already merges that into siblings (REQ-002 wired it).
- Precedence: **captured > parent context > env params** (matches REQ-002's rule).
- Round-trip through `DeferredVerificationRequest.CapturedTokens` for deferred siblings (REQ-002 already added this field).
- `Required: false` + missing field → token left undefined; downstream `{{X}}` survives as a literal; logged as WARN via the existing `unknownTokens` collector.

### 7. `POST /api/event-assert/peek`

New endpoint in a new `src/AiTestCrew.WebApi/Endpoints/EventAssertEndpoints.cs`, mirroring `DbCheckEndpoints.cs`:

- Request: `{ envKey, connectionKey, entity: { type, name, subscriptionName? }, max: 10, correlationFilter?: "..." }`.
- Resolves the connection via `IEnvironmentResolver.ResolveServiceBusConnection`.
- Opens a `ServiceBusReceiver` in **PeekMode** (`receiver.PeekMessagesAsync(max)`) — does NOT consume messages, regardless of `ReceiveMode`. This is critical: a UI button must never accidentally drain a real test.
- Returns `{ messages: [{ messageId, correlationId, contentType, enqueuedTimeUtc, applicationProperties: {...}, body: { format, preview } }], totalPeeked }`.
- `body.format` is `"Json" | "Xml" | "Text" | "Binary"`; `body.preview` is the truncated body (max 2 KB) — JSON pretty-printed.
- Auth: existing JWT/cookie; rate-limit per user (10 req/min, 429 above) — match the pattern REQ-002 introduces for `/api/db-check/dry-run`.
- Per-env opt-in: `Environments.<envKey>.AllowEventAssertPeek` (default `true` — match REQ-002's `AllowDbDryRun` precedent). Operator can flip it false on production-style envs.

### 8. In-UI editor — `EditEventAssertStepDialog.tsx`

Wire into `PostStepsPanel.tsx` next to the `dbCheck` branch. Fields:

- **Name** — single-line input.
- **Connection key** — dropdown sourced from a new `GET /api/event-assert/connections?envKey=...` endpoint (mirrors REQ-002's `GET /api/db-check/connections`).
- **Entity type** — radio: Queue / Topic.
- **Entity name** — text. If Topic, an additional **Subscription name** text input appears.
- **Body format** — dropdown (Auto / JSON / XML / Text / Binary) — defaults Auto.
- **Receive mode** — dropdown (PeekLock / ReceiveAndDelete) — defaults PeekLock with a help tooltip ("PeekLock + Complete on pass is safe for shared subscriptions").
- **Match mode** — dropdown (AnyMessage / AllMessages / ExactlyOne / ExactCount / MinCount / MaxCount / CountRange). Selecting count-based modes reveals `ExpectedCount` (and `MaxCount` for `CountRange`).
- **Timeout (seconds)** — number input, default 30.
- **Max messages** — number input, default 50.
- **Drain before parent** — checkbox, default off. Help tooltip explains the orchestrator hook.
- **Correlation filter** — optional text with `{{Token}}` autocomplete.
- **Session ID** — optional text.
- **Criteria table** — one row per criterion: `Field` (text with help-link to the path syntax doc), `Operator` (dropdown — same enum as DB), `Expected`, `Expected2` (only for Between), `IgnoreCase`, `Tolerance` fields (only for date/numeric ops), `×`. `+ Add criterion`.
- **Captures table** — one row per capture: `Field`, `As`, `Required`, `×`. `+ Add capture`.
- **Peek messages** button — calls `POST /api/event-assert/peek`. Renders the most recent N messages in a small expandable table. Each message row has a `+ Add criterion` and `+ Add capture` button that pre-fills `Field` from the actual message — selecting `ApplicationProperties.EventType` from a dropdown sourced from THIS message's properties, or pasting a body-JSON path with the value pre-filled.

Save → PUTs through the existing generic `updatePostStep` endpoint (no new save endpoint needed — the new shape lives inside the post-step JSON the endpoint already round-trips). Delete → existing `deletePostStep`.

### 9. NL authoring — chat assistant + `/add-event-assert` skill

**Chat prompt update** (`src/AiTestCrew.WebApi/Services/ChatIntentService.cs`):

- Extend the `confirmCreatePostStep` action's payload schema docs with the `eventAssert` shape.
- Add 2-3 worked examples showing different match modes — at least one with a JSONPath body criterion and one with a capture.
- Update the post-step authoring rules block: "for events on Service Bus, prefer `Body.X` JSONPath criteria over `BodyText` substring matches"; "if the user wants the message ID for later, emit a `captures` entry with `Field: MessageId`".

**`ConfirmCreatePostStepCard.tsx`** — render the new shape so the user sees the expectation list + match mode + captures cleanly before confirming.

**`/add-event-assert` slash command** — new file `.claude/skills/add-event-assert/SKILL.md`. Mirrors `/add-db-assert`:

- Args: `<moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`.
- Resolves the parent step (read-only, via existing repo).
- Drafts the connection key, entity, criteria, and captures from the description (reuses the chat assistant's prompt skeleton — share the prompt fragment via a small included file).
- Calls `POST /api/event-assert/peek` to validate the connection works AND optionally seed criteria from a sample message.
- PUTs the post-step via `updatePostStep` (or appends to the parent's `PostSteps`).
- On peek failure (auth / unknown queue / unreachable namespace), prints a typed remediation (config snippet showing how to add the connection to `appsettings.json`).

### 9a. NL-driven peek-then-author (NEW chat action `peekServiceBusMessages`)

Expanded NL coverage beyond §9 — the chat assistant becomes a first-class authoring surface for event assertions, not just a `confirmCreatePostStep` emitter. New action kind:

```json
{ "kind": "peekServiceBusMessages",
  "summary": "Peek meter-events topic on sumo-retail",
  "data": { "envKey": "sumo-retail", "connectionKey": "DefaultBus",
            "entity": { "type": "Topic", "name": "meter-events", "subscriptionName": "test-runner" },
            "max": 10, "correlationFilter": null } }
```

**System-prompt trigger phrasing:** "show me messages on …", "what's on the meter-events topic right now", "peek the queue", "are there any pending events for {{X}}".

**Card behaviour** (`ui/src/components/chat/actions/PeekServiceBusMessagesCard.tsx`, NEW):

- Calls `POST /api/event-assert/peek` and renders a compact message table (messageId, contentType, enqueuedTimeUtc, applicationProperties, body preview) — same shape the editor's "Peek messages" panel uses.
- Each peeked message row exposes:
  - **`+ Add as criterion`** — opens a field-path picker (system props / app props / Body.jsonpath); on selection emits a follow-up `confirmCreatePostStep` (or `confirmEditPostStep` if there's already an event-assert post-step on the contextual parent — derived from `catalog.currentTestSet.objectives[*].parentSteps[*].postSteps[*]`) with the criterion pre-seeded from the message's actual value.
  - **`+ Add as capture`** — same picker; emits a follow-up action with `captures: [{ field, as: <field-tail>, required: true }]`.
- The card never PUTs anything itself; all writes flow through follow-up `confirm*PostStep` cards for transparency and an explicit accept gate.

This is what makes the slice an actual **NL step-injection expansion** — the user can converse with the assistant about messages on the bus, then turn observations directly into authored post-steps without leaving chat.

### 9b. NL-driven edit of existing post-steps (NEW chat action `confirmEditPostStep`)

`confirmCreatePostStep` only handles ADDS. To enable NL-driven mutations of existing post-steps (event-assert AND, as a back-port benefit, every other post-step type including REQ-002's DB asserts), add a second action kind:

```json
{ "kind": "confirmEditPostStep",
  "summary": "Update event-assert post-step #2",
  "data": { "moduleId": "...", "testSetId": "...", "objectiveId": "...",
            "parentKind": "Api", "parentStepIndex": 0, "postStepIndex": 2,
            "postStep": { /* full updated VerificationStep shape — full replacement, not a patch */ } } }
```

**System-prompt trigger phrasing:** "change the criterion on post-step N to use Contains", "add a capture for X on the event-assert step after the API call", "make the timeout on post-step 2 longer", "tighten the match-mode to ExactlyOne".

**Authoring rule** (added to the system prompt's "Post-step authoring rules" block):

> "For NEW post-steps emit `confirmCreatePostStep`. For MUTATIONS to existing post-steps emit `confirmEditPostStep`. Resolve `postStepIndex` from `catalog.currentTestSet.objectives[*].parentSteps[*].postSteps[*]` — exact match by description; ask if ambiguous. Always emit the FULL updated `postStep` payload (replacement, not patch) — keeps the runtime simple and makes the diff visible to the user."

**Card behaviour** (`ui/src/components/chat/actions/ConfirmEditPostStepCard.tsx`, NEW):

- Renders a before/after `KeyValueGrid` diff (the card has access to the prior shape via `catalog.currentTestSet`).
- On accept, PUTs through the existing `PUT /api/modules/{m}/testsets/{ts}/objectives/{o}/post-steps/{parentKind}/{parentStepIndex}/{postIndex}` endpoint — generic, no new server route.
- Generic across all post-step types — `dbCheck`, `webUi`, `desktopUi`, `aseXml`, `aseXmlDeliver`, `eventAssert`. **Back-port benefit:** REQ-002's DB asserts retroactively gain NL-edit support.

### 10. Distributed-execution capability

- Add `"Event_AzureServiceBus"` to the agent capability registry (`src/AiTestCrew.Core/Models/AgentCapability.cs` if one exists; otherwise the agent capability is a free-form string and the agent advertises it via `AgentCapabilities` config — planner: confirm by reading `JobExecutor.ExecuteAsync` and the `--capabilities` CLI flag handler).
- A remote agent advertising `Event_AzureServiceBus` can pick up a deferred event-assert post-step. This matters for two scenarios:
  - The local test runner has no outbound access to the customer's Service Bus namespace (firewall) but a centralised agent does.
  - A test runs against an env whose Service Bus uses Azure AD auth backed by a managed identity that lives only on the centralised agent host.

### 11. Documentation

- `docs/architecture.md` — new "Event Assertion Step (Azure Service Bus)" section: data model (criteria + captures + match modes), runtime path (receive loop, settlement, diagnostics), body-format dispatch (JSON / XML / Text / Binary), capture semantics (referencing REQ-002's section to avoid duplication), connection resolution + auth modes, security envelope (peek-only preview, no production-side drain by default).
- `docs/functional.md` — user-facing "Asserting Service Bus events" section under Run Modes / Post-Steps. Include the chat NL example, `/add-event-assert` invocation, screenshot-worthy walkthrough of the editor including the peek panel.
- `docs/file-map.md` — append the new files under a new `## Event Assertion step` heading.
- `CLAUDE.md` "Where to extend — quick map" — add rows:
  - "A new Service Bus namespace → config-only — `Environments.<key>.ServiceBusConnections.<connectionKey>` (or top-level fallback). Zero code."
  - "A new event match mode → enum + agent dispatch branch. Files: `src/AiTestCrew.Storage/EventAssertAgent/MatchMode.cs`, `src/AiTestCrew.Agents/EventAssertAgent/AzureServiceBusEventAgent.cs`. Update editor dropdown + chat prompt examples."
  - "A new event-body format (e.g. Avro, Protobuf) → enum + body extractor. Files: `BodyFormat.cs`, new `<Format>BodyExtractor.cs`. Update sniff logic in `BodyFormatDetector` and editor dropdown."
- `CLAUDE.md` "Available slash commands" — add `/add-event-assert <moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`.

### 12. Tests

- **Unit (new file `tests/AiTestCrew.Agents.Tests/EventAssertAgent/ScalarOperatorEvaluatorTests.cs`)** — every operator across (string, numeric, date, NULL, JSON-extracted scalar, XML-extracted scalar). Lift from REQ-002's `ColumnAssertionEvaluatorTests.cs` and parameterize over the value-source.
- **Unit `MatchModeEvaluatorTests.cs` (new)** — every match mode against synthetic message-pass-fail vectors:
  - `AnyMessage` with [pass, fail, fail] → pass.
  - `AllMessages` with [pass, pass] → pass; `[pass, fail]` → fail; `[]` → fail.
  - `ExactlyOne` with [pass, fail], [pass, pass], [fail, fail] → pass / fail / fail.
  - `ExactCount(2)`, `MinCount(2)`, `MaxCount(2)`, `CountRange(1, 3)` — boundary cases.
- **Unit `BodyFormatDetectorTests.cs` (new)** — `Auto` sniff for application/json, application/xml, text/plain, no-content-type-but-`{`-prefix, no-content-type-but-`<`-prefix, binary content type.
- **Unit `EnvironmentResolverTests.cs` (extend)** — `ResolveServiceBusConnection("Default", env)` resolves the per-env override; falls back to top-level; returns null on unknown key.
- **Unit `StepParameterSubstituterTests.cs` (extend)** — substitute `Sql`-equivalents on `EventAssertStepDefinition`: `Criteria[*].{Field,Expected,Expected2}`, `Captures[*].Field`, `CorrelationFilter`, `SessionId`, `Entity.Name`, `Entity.SubscriptionName`. Do NOT substitute `Captures[*].As` (REQ-002 rule).
- **Integration (mocked)** — open question: thin abstraction `IServiceBusReceiverFactory` over the Azure SDK so unit tests can supply a fake receiver returning a synthetic message stream. Recommendation: yes — the SDK types are notoriously hard to fake otherwise. The Storage project owns the interface; the Agents project owns the Azure-backed implementation.
- **Integration (real Azure, CI-gated)** — at least one happy-path test against a dedicated test namespace. Skipped when `AZ_SERVICEBUS_TEST_CONNECTION_STRING` env var is missing, so local CI without Azure access still passes. Test creates a queue (or uses a pre-provisioned one), sends a message, runs the agent, asserts pass + captured token. Tear-down deletes the messages.
- **WebApi**: at least one happy-path test for `POST /api/event-assert/peek` (unknown connection → 404; rate-limit → 429; happy path → 200 with truncated body).
- **Capture round-trip** — extend REQ-002's `DbCaptureRoundTripTests` (or add a new file) with an event-source → DB-sink case: a parent API step → event-assert post-step captures `OrderId` → sibling DB-check post-step uses `{{OrderId}}`. Confirms the bidirectional plumbing.

## Scope — explicitly out

- **Multi-message captures** (capture an array of values across all matching messages). Future REQ. v1 captures from the FIRST matching message only — same constraint REQ-002 locked in for DB rows.
- **Send-and-assert** (publish a fixture message in the same step, then assert side-effects). This is fixture setup; use a separate parent test step (an API call to whatever publishes events) and the assertion as a post-step.
- **Service Bus topics with auto-created subscriptions** (the agent creates an isolated subscription per test run, with a correlation filter, then deletes it after). Operationally appealing but management-plane access (RBAC `Azure Service Bus Data Owner`) is a higher trust bar than data-plane access. v1 requires a pre-existing subscription. Future REQ.
- **Dead-letter queue assertions** ("the message ended up in the DLQ with reason X"). Useful for negative tests; out for v1. Workaround: configure the entity name as `<queue>/$DeadLetterQueue` directly — Azure SDK supports this — and document it.
- **Scheduled / deferred / sessioned message assertions beyond a single session ID** — a `SessionId` field is in scope (single-session receivers); cross-session ordering assertions are not. Future REQ.
- **Replay from a specific sequence number / enqueue time** — useful for retrospective debugging; out. v1 always reads forward from "now" (or after the drain).
- **Event Hubs** — a separate Azure messaging service, different SDK (`Azure.Messaging.EventHubs`), different consumption model (offsets + checkpoints). If we ever need to assert on Event Hubs, it's a parallel agent (`EventHubsEventAgent`) with the same operator surface but different connection / entity / receive semantics. NOT a v1 extension of this requirement. Document this clearly so a future REQ doesn't conflate them.
- **Other broker types** (RabbitMQ, Kafka, AWS SNS/SQS, Google Pub/Sub) — out. The factory pattern (parallel to `IXmlDropTarget` for delivery) is a future-proofing direction; v1 ships a single concrete agent.
- **Recording-flow integration** — event assertions are dialog/chat/skill-authored only; no recorder.
- **Standalone "EventAssert" objective** (top-level, not a post-step) — out, by the same reasoning as `DbCheckAgent`.
- **Top-level "Service Bus health" panel on the dashboard** — nice-to-have; out. The peek endpoint covers manual probing.

## Acceptance criteria

A reviewer should be able to verify each of these without ambiguity:

1. **Data model.** `EventAssertStepDefinition` with `Criteria`, `Captures`, `Entity`, `MatchMode`, `BodyFormat`, `ReceiveMode`, etc. exists and serialises/deserialises through the existing post-step JSON path. Loading a test set with one event-assert post-step, saving it back, and diffing the file shows no semantic change.
2. **`VerificationStep` carrier.** `VerificationStep.EventAssert: EventAssertStepDefinition?` is added; setting `Target = "Event_AzureServiceBus"` routes the post-step to the new agent at execution time.
3. **Connection registry.** `appsettings.json → TestEnvironment.Environments.<env>.ServiceBusConnections.MyNs` is honored by `IEnvironmentResolver.ResolveServiceBusConnection("MyNs", env)`. Top-level fallback works. Unknown key → resolver returns null → agent surfaces `TestStatus.Error` with a message naming the key and env.
4. **Body format dispatch.** A criterion `Body.OrderId Equals "12345"` against a message with `ContentType: application/json` and body `{"OrderId":"12345"}` passes. The same criterion against a message with body `<Order Id="12345"/>` fails (because JSON path on XML body fails with a typed message). The criterion `BodyXml.//Order/@Id Equals "12345"` against the XML body passes.
5. **Match modes.** Unit tests pass for every match mode listed in §1 across the boundary cases enumerated in §12.
6. **Capture round-trip (inline).** A parent API step → event-assert post-step capturing `MessageId` → sibling DB-check post-step using `{{MessageId}}` in its `WHERE` clause finds the row. Verified by integration test.
7. **Capture round-trip (deferred).** Same as #6 but with `WaitBeforeSeconds > defer threshold` — the captured value round-trips through `DeferredVerificationRequest.CapturedTokens` and arrives at the deferred sibling.
8. **Drain before parent.** A queue with 5 stale messages from a prior failed run + a fresh parent step that publishes 1 new event + an event-assert with `DrainBeforeParent=true` and `ExactlyOne` mode → passes (the stale messages are drained before the parent runs; only the fresh event remains).
9. **Settlement on PeekLock.** With `ReceiveMode=PeekLock` and `CompleteOnPass=true`: a green test completes matching messages (verified by re-running the agent and seeing zero messages in the queue) and abandons non-matching messages (verified by a second receiver seeing the non-matching messages still present). On red, all messages are abandoned (the next debug attempt sees the same population).
10. **Diagnostics.** A failing assertion's run-detail UI surfaces up to 10 received-message summaries (truncated bodies + system properties + application properties + per-criterion pass/fail) under the failure reason. Verified by inspecting the JSON file written under `executions/`.
11. **Editor dialog.** Clicking "Edit" on an `eventAssert` post-step in `PostStepsPanel.tsx` opens `EditEventAssertStepDialog` with all current values populated. "Peek messages" returns up to N messages without consuming them. The `+ Add criterion` button on a peeked message seeds a criterion with the actual field value. Saving PUTs through `updatePostStep` and the panel refreshes. Delete removes it via `deletePostStep`.
12. **Chat assistant.** Sending the chat message "after the API publish step, confirm a `MeterReadingCreated` event was raised onto the `meter-events` topic on the `test-runner` subscription with a `MeterId` matching `{{MeterId}}` and capture the `EventId` for later" produces a `confirmCreatePostStep` action with: `target=Event_AzureServiceBus`, `entity={type:Topic, name:meter-events, subscriptionName:test-runner}`, criteria including `ApplicationProperties.EventType Equals "MeterReadingCreated"` and `Body.MeterId Equals "{{MeterId}}"`, and a capture `[{Field:Body.EventId, As:EventId}]`.
12a. **NL peek-then-author (§9a).** Sending the chat message "show me the last 5 messages on the `meter-events` topic, `test-runner` sub, on `sumo-retail`" produces a `peekServiceBusMessages` action; the card calls `POST /api/event-assert/peek` and renders the message list. Clicking "Use field `ApplicationProperties.EventType` as criterion" on a peeked message emits a follow-up `confirmCreatePostStep` action whose payload pre-seeds that criterion with the actual value from the peeked message.
12b. **NL-driven edit (§9b).** Against a test set that already has an event-assert post-step on `Api[0]`, sending the chat message "change the criterion on the event-assert post-step after the API call to use Contains instead of Equals" produces a `confirmEditPostStep` action with the operator updated to `Contains`, the rest of the payload identical, and `postStepIndex` resolved from `catalog.currentTestSet`. Accepting the card PUTs through the existing post-step CRUD endpoint and `PostStepsPanel.tsx` reflects the change on the next render.
13. **Slash command.** `/add-event-assert <m> <ts> <o> Api 0 "after the publish, confirm a MeterReadingCreated event arrived on the meter-events topic, test-runner sub, MeterId={{MeterId}}, capture EventId"` produces a working post-step end-to-end on a clean test set, including a green peek probe.
13a. **`confirmEditPostStep` is generic.** The same chat action also works against an existing `dbCheck` or `webUi` post-step (e.g. "change the SQL on the DB-check after the publish to filter by `Status='Processed'`" → `confirmEditPostStep` with the updated `dbCheck.sql`). Back-port benefit: REQ-002's DB asserts and earlier UI post-steps retroactively support NL-driven edits.
14. **Distributed-execution.** A remote agent started with `--capabilities Event_AzureServiceBus` picks up a deferred event-assert post-step from the run queue, executes it, returns the result, and sibling captures arrive on the originating run.
15. **SQL-style guardrails — N/A here.** Service Bus doesn't have an injection class equivalent to SQL. The peek endpoint only supports peek-mode (cannot drain), and the agent's settlement is gated behind `CompleteOnPass`. Document explicitly that there are no read-vs-write guardrails to apply.
16. **Documentation.** `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, and `CLAUDE.md` (extension map + slash commands) are updated.

## Files most likely touched

**Backend (C#) — new:**

- `src/AiTestCrew.Storage/EventAssertAgent/EventAssertStepDefinition.cs`
- `src/AiTestCrew.Storage/EventAssertAgent/EventCriterion.cs`
- `src/AiTestCrew.Storage/EventAssertAgent/EventCapture.cs`
- `src/AiTestCrew.Storage/EventAssertAgent/MatchMode.cs` (enum)
- `src/AiTestCrew.Storage/EventAssertAgent/BodyFormat.cs` (enum)
- `src/AiTestCrew.Storage/EventAssertAgent/ServiceBusEntity.cs` (+ enum `ServiceBusEntityType`)
- `src/AiTestCrew.Core/Configuration/ServiceBusConnectionConfig.cs` (+ enum `ServiceBusAuthMode`)
- `src/AiTestCrew.Agents/EventAssertAgent/AzureServiceBusEventAgent.cs`
- `src/AiTestCrew.Agents/EventAssertAgent/IServiceBusReceiverFactory.cs` + `ServiceBusReceiverFactory.cs` (concrete; cached `ServiceBusClient` per connection key)
- `src/AiTestCrew.Agents/EventAssertAgent/MessageBodyExtractor.cs` (JSON / XML / Text / Binary dispatch)
- `src/AiTestCrew.Agents/EventAssertAgent/MatchModeEvaluator.cs`
- `src/AiTestCrew.Agents/EventAssertAgent/ScalarOperatorEvaluator.cs` (lifted from REQ-002's `ColumnAssertionEvaluator`)
- `src/AiTestCrew.WebApi/Endpoints/EventAssertEndpoints.cs`

**Backend — modified:**

- `src/AiTestCrew.Storage/AseXmlAgent/VerificationStep.cs` — new optional `EventAssert: EventAssertStepDefinition?` field; updated XML doc on `Target`.
- `src/AiTestCrew.Core/Models/Enums.cs` — new `TestTargetType.Event_AzureServiceBus`.
- `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` — new `ServiceBusConnections: Dictionary<string,ServiceBusConnectionConfig>` + `AllowEventAssertPeek: bool` (default true).
- `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` — top-level `ServiceBusConnections` fallback dict.
- `src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs` — new `ResolveServiceBusConnection(connectionKey, envKey)` method.
- `src/AiTestCrew.Agents/Environment/EnvironmentResolver.cs` — implement the new resolver method.
- `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` — new `Apply(EventAssertStepDefinition, ...)` overload.
- `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs` — pre-parent drain hook (§4). Verify the existing capture-token merge handles event-assert captures unchanged (it should — same `Metadata["capturedTokens"]` shape).
- `src/AiTestCrew.Storage/DbAgent/ColumnAssertionEvaluator.cs` — extract operator-eval logic into `Agents/EventAssertAgent/ScalarOperatorEvaluator.cs`; have `ColumnAssertionEvaluator` delegate. (Refactor — must not regress REQ-002 tests.)
- `src/AiTestCrew.Runner/Program.cs`, `src/AiTestCrew.WebApi/Program.cs` — DI for `AzureServiceBusEventAgent`, `IServiceBusReceiverFactory`, `EventAssertEndpoints`.
- `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` — system prompt updates for the new `eventAssert` payload shape + 2-3 worked examples + post-step authoring rules.
- `src/AiTestCrew.Runner/appsettings.example.json` — add a `ServiceBusConnections` block (one connection-string-auth namespace at top level, one Azure AD namespace overridden per env).

**Frontend (React/TS) — new:**

- `ui/src/components/EditEventAssertStepDialog.tsx`
- `ui/src/api/eventAssert.ts` — `peekMessages(envKey, connectionKey, entity, max, correlationFilter?)` + `getServiceBusConnections(envKey)` clients.

**Frontend — modified:**

- `ui/src/types/index.ts` — extend with `EventAssertStepDefinition`, `EventCriterion`, `EventCapture`, `ServiceBusEntity`, `MatchMode`, `BodyFormat`, `ReceiveMode` types.
- `ui/src/components/PostStepsPanel.tsx` — wire the new dialog into the editingIdx branch; new `EventAssertBlock` to render the new shape (criteria list + captures + match mode + entity).
- `ui/src/components/chat/actions/ConfirmCreatePostStepCard.tsx` — render new shape.

**Skill / docs:**

- `.claude/skills/add-event-assert/SKILL.md` (new) + any asset.
- `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, `CLAUDE.md` — updates per §11.

**Tests:**

- `tests/AiTestCrew.Agents.Tests/EventAssertAgent/ScalarOperatorEvaluatorTests.cs` (new — also covers what REQ-002's `ColumnAssertionEvaluatorTests` covered; the existing tests delegate via the refactor)
- `tests/AiTestCrew.Agents.Tests/EventAssertAgent/MatchModeEvaluatorTests.cs` (new)
- `tests/AiTestCrew.Agents.Tests/EventAssertAgent/BodyFormatDetectorTests.cs` (new)
- `tests/AiTestCrew.Agents.Tests/EventAssertAgent/AzureServiceBusEventAgentTests.cs` (new — uses fake `IServiceBusReceiverFactory`)
- `tests/AiTestCrew.Agents.Tests/Environment/EnvironmentResolverTests.cs` (extend)
- `tests/AiTestCrew.Agents.Tests/Environment/StepParameterSubstituterTests.cs` (extend)
- `tests/AiTestCrew.WebApi.Tests/EventAssertEndpointsTests.cs` (new)
- Integration test for capture round-trip (event-source → DB-sink) — extend REQ-002's `DbCaptureRoundTripTests` (or add a new file).
- Optional Azure-gated integration test — skipped when `AZ_SERVICEBUS_TEST_CONNECTION_STRING` is unset.

## Open questions for the planner

1. **`IServiceBusReceiverFactory` vs direct `ServiceBusClient` use** — recommend the factory abstraction even though it adds a small interface, because the Azure SDK's receiver/sender types are sealed and notoriously hard to fake. Without it, the only test path is integration-against-real-Azure, which is fragile in CI. Confirm.
2. **`AssertionOperator` sharing — refactor or duplicate?** — recommend extract `ScalarOperatorEvaluator` from REQ-002's `ColumnAssertionEvaluator` so both DB and event-assert share the dispatch. Risk: the refactor must not regress REQ-002's tests. Mitigation: the test suite is the contract; if the lifted helper breaks them, the refactor is wrong. Alternative: duplicate the operator-dispatch into a sibling module — keeps each agent's call site simpler but doubles the maintenance surface for new operators. Pick the refactor.
3. **JSONPath library** — REQ-002 will have committed to `JsonPath.Net` by the time this lands. Reuse the same wrapper. If REQ-002 chose differently, match its choice.
4. **XML body XPath — namespace handling** — recommend in v1 the user wraps prefixed paths in `local-name()` filters (e.g. `//*[local-name()='Order']/@Id`). A first-class namespace registry (`Namespaces: Dictionary<string,string>` on the step) is a valid future extension; flag in the plan but don't build.
5. **Azure AD auth via `DefaultAzureCredential`** — works locally via Azure CLI; works in production via managed identity. The `ManagedIdentityClientId` field is for user-assigned MIs; confirm by reading the team's Azure conventions before merge.
6. **Drain semantics — settle vs receive-and-delete** — recommend `ReceiveAndDelete` for drain (simpler; the messages are stale by definition). Risk: if drain accidentally targets a production-traffic queue, real messages are lost. Mitigation: drain is opt-in per step, and the editor's tooltip warns about it.
7. **`MaxCount(0)` semantics** — should "expect zero matching messages" pass? Useful for negative assertions (e.g. "no rejection event was raised"). Recommend YES — wire it explicitly. With `MaxCount(0)` and any pass count > 0, fail; with pass count == 0, pass. The receive loop still runs for the full timeout to actually verify zero arrived (don't early-exit).
8. **Settle behaviour when `CompleteOnPass=true` but the agent crashes mid-loop** — `using` blocks on the receiver should abandon any locked messages on dispose. Confirm by reading the SDK's dispose semantics; document.
9. **Sessioned receivers — single-session only in v1?** — recommend YES. Multi-session "receive from any session that has a message matching X" requires the SDK's `ServiceBusSessionProcessor`, which is a different lifecycle. Future REQ.
10. **Capture-token precedence vs env params** — recommend captured-most-recent-wins, log INFO on overwrite. Match REQ-002's rule exactly.
11. **`POST /api/event-assert/peek` — auth + rate limit** — match REQ-002's pattern (10 req/min in-memory token bucket, 429 above). If REQ-002 lifts the bucket into a shared utility, reuse it; otherwise duplicate it for now and note the consolidation as a follow-up.
12. **Per-env opt-in for peek — name** — `AllowEventAssertPeek` (recommended; positive default to match `AllowDbDryRun` and `RunDataPacksOnStartup`). Confirm with operator (Kalhara) before merge for production envs.
13. **Editor "Peek messages" — risk of locking real messages** — peek mode in the SDK is read-only; messages are NOT locked. Verify in the SDK docs (`receiver.PeekMessagesAsync` vs `receiver.ReceiveMessagesAsync`). If for any reason peek is unavailable on a given entity (rare), fall back to a documented "messages not previewable here, author criteria by hand" UI state.
14. **Content-type sniffing with mixed-case `application/Json`** — be lenient (case-insensitive contains check on common substrings: `json`, `xml`). Document.
15. **`Body.*` and `BodyXml.*` on a binary body** — should the criterion immediately fail with a typed message, or should it skip silently? Recommend FAIL (loud > silent), so the user knows their criteria are unevaluatable against the actual content type.
16. **Should peek-endpoint and agent share a single `BodyFormatDetector`?** — recommend YES; one definition, one set of tests. Place it in the Storage project so both Agents and WebApi can reference it.
17. **`/add-event-assert` skill — should it create the connection in `appsettings.json` if missing?** — recommend NO. Print the snippet the user needs to paste, but never write `appsettings.json` from a skill (security + operator-handoff concern).
18. **Distributed-execution — capability registration** — confirm whether capabilities are an enum or free-form strings. If enum, add `Event_AzureServiceBus`; if free-form, the agent advertises the string and the dispatch matches on equality.
19. **Telemetry / observability** — should the agent emit a structured log entry per received message (count + per-criterion result)? Recommend YES at debug level, gated by an existing logger; off by default.
20. **REQ-002 / REQ-003 coordination** — both add capture-token surfaces; this REQ piggybacks on REQ-002's plumbing exclusively. If REQ-002 isn't merged when this REQ is implemented, gate this slice on REQ-002 first. The plan must explicitly check the merge order.
