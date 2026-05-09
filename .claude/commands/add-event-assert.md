Scaffold an Azure Service Bus event-assertion post-step attached to an
existing parent test step.

An event assertion (`Event_AzureServiceBus` post-step) opens a receiver
against a queue or topic+subscription, evaluates per-message criteria
(over system properties, application properties, JSON / XML / text body
fields), folds the per-message verdict via a configurable match-mode
(`AnyMessage` / `AllMessages` / `ExactlyOne` / `ExactCount` / `MinCount` /
`MaxCount` / `CountRange`), and optionally captures values from the first
passing message into `{{Token}}` for sibling post-steps. This is the
parallel of `/add-db-assert` for event-source verifications.

Arguments: $ARGUMENTS
Expected format: `<moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`

Examples:
- `aemo-b2b mfn-delivery-tests deliver-mfn AseXmlDeliver 0 "after the delivery, confirm a MeterReadingCreated event was raised onto the meter-events topic, test-runner sub, with MeterId={{NMI}}, capture EventId"`
- `bravo-smoke jobs-checks search-job WebUi 0 "verify NO rejection event arrived on the order-events queue for {{OrderId}}"`
- `bravo-smoke order-flow create-order Api 0 "drain the queue first, then confirm exactly one shipment-confirmed event arrives"`

`parentKind` must be one of: `Api`, `WebUi`, `DesktopUi`, `AseXml`, `AseXmlDeliver`.
`parentStepIndex` is 0-based into the parent objective's matching step list.

## What you must do

Event assertions are authored without a UI recorder — they're declarative
JSON. The skill's job is to draft, validate the connection works via the
peek endpoint, then PUT the post-step.

### Step 1 — Confirm the prerequisites

1. Module + test set + objective exist. Read
   `modules/<moduleId>/<testSetId>.json` and verify the test objective
   with id `<objectiveId>` is present. If the user gave the objective name
   instead, resolve to the slug.

2. Parent step exists at `<parentKind>` index `<parentStepIndex>`. The
   carrier field per kind:
   - `Api` → `apiSteps[i]`
   - `WebUi` → `webUiSteps[i]`
   - `DesktopUi` → `desktopUiSteps[i]`
   - `AseXml` → `aseXmlSteps[i]`
   - `AseXmlDeliver` → `aseXmlDeliverySteps[i]`

3. The Service Bus connection key the user mentions (or the only
   configured key by default) is set up for the test set's environment.
   Hit `GET /api/event-assert/connections?envKey=<key>` and check the
   returned `keys`.

4. The active env's `AllowEventAssertPeek` is true. The peek will return
   403 if it's disabled — surface that to the user and stop.

### Step 2 — Read the engine's contracts

Skim these before drafting so you understand what's allowed:

- `src/AiTestCrew.Storage/EventAssertAgent/EventAssertStepDefinition.cs`
  — persistence shape (Name, ConnectionKey, Entity, BodyFormat,
  ReceiveMode, MatchMode, ExpectedCount, MaxCount, TimeoutSeconds,
  MaxMessages, DrainBeforeParent, CompleteOnPass, CorrelationFilter,
  SessionId, Criteria, Captures).
- `src/AiTestCrew.Storage/EventAssertAgent/MatchMode.cs` — every match
  mode + when each fits a user phrasing.
- `src/AiTestCrew.Storage/EventAssertAgent/BodyFormat.cs` — Auto / Json /
  Xml / Text / Binary; sniff rules in
  `src/AiTestCrew.Agents/EventAssertAgent/Body/BodyFormatDetector.cs`.
- `src/AiTestCrew.Agents/EventAssertAgent/MessageFieldResolver.cs` — the
  full field-path table (system props,
  `ApplicationProperties.<name>`, `Body.<jsonpath>`, `BodyXml.<xpath>`,
  `BodyText`, `BodyLength`).

### Step 3 — Draft the entity, criteria, and captures

Start from the user's natural-language description. Apply these rules:

**Entity**:
- `Queue` for "the X queue" / "the X events queue".
- `Topic` for "the X topic"; require the user to name a subscription
  (`SubscriptionName` is mandatory). If they only mention a topic, ask.
- Use `{{Token}}` placeholders for any value coming from the parent
  step's render context. Common tokens for aseXML delivery parents:
  `{{NMI}}`, `{{MessageID}}`, `{{TransactionID}}`, `{{Filename}}`. For
  UI / API parents the available tokens are env params + any siblings'
  captures.

**MatchMode**:
- `AnyMessage` (default) — "an event was raised", "at least one matched".
- `AllMessages` — "every message must match" (rare; only when the user
  literally said all/every).
- `ExactlyOne` — "exactly one", "a single".
- `ExactCount(N)` / `MinCount(N)` / `MaxCount(N)` / `CountRange(lo, hi)`
  — when the user gave a specific count.
- `MaxCount(0)` — for negative-assertion phrasing ("verify NO X event was
  raised", "ensure no rejection arrived"). The receive loop runs the
  FULL timeout to actually verify zero arrived.

**Criteria**:
- Use `ApplicationProperties.<name>` for AEMO-style metadata fields
  (EventType, Source, MessageType, etc.).
- For JSON bodies, prefer `Body.<jsonpath>` over `BodyText` substring
  matches. JSONPath is the same syntax JsonPath.Net accepts (e.g.
  `Body.Order.Id`, `Body.Items[0].Sku`).
- For XML bodies, use `BodyXml.<xpath>`. For default-namespace
  documents, wrap in `local-name()` filters
  (`//*[local-name()='Order']/@Id`).
- Pick the right operator: `Equals` for exact match (default
  `IgnoreCase=true`); `Contains` / `StartsWith` / `EndsWith` for
  substrings; `Regex` for patterns; `GreaterThan` / `LessThan` /
  `Between` for thresholds (numbers OR dates work — the evaluator falls
  back automatically); `EqualsNumeric` with `ToleranceDelta` for decimal
  fields; `EqualsDate` with `ToleranceSeconds` for timestamps; `IsNull`
  / `IsNotNull` for null-aware checks.

**Captures**:
- If the user wants to use a field value in a later post-step, emit a
  `captures` entry: `{ field: "MessageId", as: "MessageId", required: true }`.
- For body-extracted values:
  `{ field: "Body.OrderId", as: "OrderId", required: false }`.
- `as` is the bare token name — never wrap in braces. Siblings reference
  it as `{{OrderId}}`.

**DrainBeforeParent**:
- Only set true when the user explicitly mentions stale-message risk
  ("drain the queue first", "make sure no leftovers from a prior failed
  run"). Otherwise leave false. The orchestrator drains in
  `ReceiveAndDelete` mode BEFORE the parent step runs.

### Step 4 — Validate via peek

Hit the WebApi:

```bash
curl -X POST http://localhost:5050/api/event-assert/peek \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <your-key>" \
  -d '{
    "envKey": "<envKey or null>",
    "connectionKey": "<key>",
    "entity": { "type": "Queue", "name": "<entity>" },
    "max": 5
  }'
```

The response carries up to N current messages — system + application
properties + body preview. Use this to:
1. Confirm the connection works + the entity exists (404 / 500 means
   stop and surface the issue).
2. Confirm the field paths the user mentioned actually exist on a real
   message (`ApplicationProperties.EventType` is present, the JSON body
   has `OrderId`, etc.).

**On peek failure** (auth / unknown entity / unreachable namespace): print
a typed remediation snippet showing how to add the connection to
`appsettings.json`. **Never write `appsettings.json` from the skill** —
that's an operator handoff (security + per-env review).

### Step 5 — PUT the post-step

Build a `VerificationStep` payload (the post-step shape) and PUT it via
the generic post-step CRUD endpoint:

```bash
curl -X PUT "http://localhost:5050/api/modules/<m>/testsets/<ts>/objectives/<o>/post-steps/<parentKind>/<parentStepIndex>" \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <your-key>" \
  -d '{ ... post-step JSON ... }'
```

The post-step JSON shape:

```json
{
  "description": "<short human label>",
  "target": "Event_AzureServiceBus",
  "waitBeforeSeconds": 0,
  "role": "Verification",
  "eventAssert": {
    "name": "<human label>",
    "connectionKey": "DefaultBus",
    "entity": { "type": "Queue", "name": "<entity>" },
    "matchMode": "AnyMessage",
    "criteria": [
      { "field": "ApplicationProperties.EventType", "operator": "Equals", "expected": "<EventType>" }
    ],
    "captures": [
      { "field": "MessageId", "as": "MessageId", "required": true }
    ],
    "timeoutSeconds": 30
  }
}
```

Pick `waitBeforeSeconds` per the deferred-vs-inline rules in the
chat-prompt docs:
- 0 / a few seconds for "right after".
- > 30s when the bus needs time to process the parent. The exact
  deferral threshold is in
  `appsettings.json → TestEnvironment.AseXml.VerificationDeferThresholdSeconds`.

### Step 6 — Confirm the change

After PUT, re-read the objective and verify the new post-step appears
under the right parent step's `postSteps[]`. Tell the user the peek
results + final post-step shape, plus a suggested next step (run the
test set in Reuse mode against the env, or run only the new post-step
via the `/api/runs` `verifyStepFilter`).

## What can go wrong

- **Connection key not configured.** 404 from peek. Print the snippet
  the operator needs to add under
  `Environments.<env>.ServiceBusConnections.<key>` (or the top-level
  fallback). Don't write the config yourself.
- **Topic without subscription.** 400 from peek. The user must name a
  subscription — ask.
- **Body path against wrong format.** A `Body.X` criterion against an
  XML body, or a `BodyXml.X` criterion against JSON, fails with a typed
  reason. The peek output includes the body's resolved `format` —
  use it.
- **`ApplicationProperties` key not present.** The criterion fails;
  peek the entity, look at the actual property names, and adjust.
- **Token not in context.** A `{{MeterId}}` reference fails silently
  with the literal `{{MeterId}}` showing up in the criterion. Lenient
  mode logs WARN; on the next deferred sibling the same token will
  surface unsubstituted. Confirm the parent step actually emits the
  token (delivery → yes; an arbitrary API or UI step → only if it
  shipped env params or earlier captures bound it).
- **Captures didn't fire.** Captures only run when the verdict is green
  AND every required capture resolves. A failed required capture
  produces a Failed step with `Capture failed: …` in the summary.

## Don't

- Don't hand-edit the persisted JSON file — go through the API.
- Don't substitute `Captures.As` from any context — the agent
  intentionally doesn't substitute it.
- Don't switch to a different connection key without confirming with
  the user; rerouting connections silently is a foot-gun.
- Don't enable `DrainBeforeParent` unless the user asked. Drain is
  destructive (`ReceiveAndDelete` mode); accidentally draining a
  production-traffic queue is bad.
- Don't peek a queue with a `correlationFilter` set against a value
  the user didn't supply. The filter is a debugging tool, not a
  silent narrowing of scope.
