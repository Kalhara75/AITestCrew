---
id: REQ-002
title: DB Assert post-step — JSON column assertions, value capture, in-UI editor, and richer comparators
status: Proposed
created: 2026-05-08
author: Kalhara Samarasinghe
area: agents + ui
---

# REQ-002 — DB Assert post-step (JSON, capture, editor, comparators)

## Goal

Make the DB Assert step a first-class verification surface that any test type can pin onto. Today the carrier exists, the agent runs, and the chat assistant can author one — but the surface stops at "case-insensitive equality on `.ToString()` of a column". Lift it to:

1. **JSON column assertions** — `JsonPath` inside the column value (Bravo's `Payload`, `RawPayload`, and most newer columns are JSON blobs that today can only be string-matched).
2. **Capture as `{{Token}}`** — bind a returned column or JSON-extracted value into the run context so a later post-step can read it.
3. **Richer comparators** — equals / not-equals / contains / starts-with / ends-with / regex / >, <, between / IS NULL / IS NOT NULL / numeric-equals / date-equals-with-tolerance.
4. **Multi-DB connections** — a registry beyond hard-coded `"BravoDb"` so SDR / Reporting / future DBs can be reached without code changes.
5. **In-UI editor with dry-run** — currently the dialog branch in `PostStepsPanel.tsx` for `dbCheck` is a no-op (only `webUi` and `desktopUi` are wired). Add an editor with `{{Token}}` autocomplete, a "Try query" preview, and an assertion table.
6. **`/add-db-assert` skill + chat-assistant prompt update** — make NL authoring as smooth as `/add-asexml-verification` is for UI verifications.

The DB Assert step **stays post-step-only** — DbCheckAgent itself documents that "a DB assertion without a preceding write has no signal" (`src/AiTestCrew.Agents/DbAgent/DbCheckAgent.cs:14-17`). That constraint is intentional and not in scope to lift.

## Why now

- Slice 1 + 2 wired the carrier (`VerificationStep.DbCheck`), the agent (`DbCheckAgent`), the SQL guardrails, the chat-assistant intent (`confirmCreatePostStep` for `Db_SqlServer`), and the read-only display block (`PostStepsPanel.tsx:312-350`). The plumbing is solid.
- The two highest-value gaps for actual testing flows are **JSON column assertions** (Bravo persists most state as JSON inside `nvarchar(max)` columns; today these are unassertable except by literal substring match) and **capture-as-token** (without it, a DB-side ID can't flow into a subsequent API/UI step, so end-to-end tests have to bounce through aseXML or UI side-effects to bridge data).
- Once those land, the rest of this requirement is the surface area — UI editor, comparators, multi-DB — that turns the step from "a thing the chat assistant produces" into a thing a test author can comfortably build with from any direction.

## Current-state findings (from audit — do NOT redo these)

✅ **Already in place — extend, don't rebuild:**

| Component | Path | Status |
|---|---|---|
| `DbCheckStepDefinition` | `src/AiTestCrew.Storage/DbAgent/DbCheckStepDefinition.cs` | Carrier with `Sql`, `ExpectedRowCount`, `ExpectedColumnValues`, `ConnectionKey="BravoDb"`, `TimeoutSeconds=15`. |
| `DbCheckAgent` | `src/AiTestCrew.Agents/DbAgent/DbCheckAgent.cs` | Post-step-only agent; `Db_SqlServer` capability; loops over `PreloadedTestCases: List<DbCheckStepDefinition>`; first-row column compare. |
| `DbCheckSqlGuardrails` | `src/AiTestCrew.Agents/DbAgent/DbCheckSqlGuardrails.cs` | Rejects non-SELECT, semicolons, denied keywords (`INSERT/UPDATE/DELETE/MERGE/TRUNCATE/DROP/ALTER/CREATE/EXEC/EXECUTE/SHUTDOWN/GRANT/REVOKE/INTO`). Allows CTEs (`WITH`). |
| `TestTargetType.Db_SqlServer` | `src/AiTestCrew.Core/Models/Enums.cs` | Enum value present. |
| DI registration | `src/AiTestCrew.Runner/Program.cs:526-534`, `src/AiTestCrew.WebApi/Program.cs:143-151` | `DbCheckAgent` registered as `ITestAgent` in both Program.cs files. |
| `IEnvironmentResolver.ResolveBravoDbConnectionString` | `src/AiTestCrew.Agents/Environment/EnvironmentResolver.cs` | Per-env override → falls back to top-level `BravoDbConnectionString`. |
| `BravoDbConnectionString` config | `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs:37` + `TestEnvironmentConfig.cs` | Per-env + global. |
| `StepParameterSubstituter.Apply(DbCheckStepDefinition)` | `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs:338-352` | Token-substitutes `Sql` and `ExpectedColumnValues` values. |
| `VerificationStep.DbCheck` carrier | `src/AiTestCrew.Storage/AseXmlAgent/VerificationStep.cs:67` | Optional payload field; `Target="Db_SqlServer"` selects it. |
| `TestObjective.EnumerateAllPostSteps()` | `src/AiTestCrew.Storage/Persistence/TestObjective.cs:137-171` | Yields DB post-steps under any of the five parent kinds. |
| Chat NL authoring | `src/AiTestCrew.WebApi/Services/ChatIntentService.cs:457-481, 537-542` | `confirmCreatePostStep` action with `target="Db_SqlServer"` + `dbCheck` payload; system prompt teaches the LLM to write SELECTs with `{{Token}}` placeholders. |
| `ConfirmCreatePostStepCard.tsx` | `ui/src/components/chat/actions/ConfirmCreatePostStepCard.tsx` | Confirmation card for the chat action. |
| UI types | `ui/src/types/index.ts:103-129` | `DbCheckStepDefinition` + `PostStep.dbCheck` declared. |
| Read-only display | `ui/src/components/PostStepsPanel.tsx:312-350` | `DbCheckBlock` renders SQL + expected values. |
| `update/deletePostStep` API | `ui/src/api/modules.ts` + `src/AiTestCrew.WebApi/Endpoints/TestSetEndpoints.cs` | Generic post-step CRUD already round-trips any `VerificationStep` shape. |

❌ **Gaps this requirement addresses:**

1. **No JSON column support.** `ExpectedColumnValues: Dictionary<string,string>` does case-insensitive `string.Equals` on `reader.GetValue(ord)?.ToString() ?? ""` (`DbCheckAgent.cs:178-189`). Bravo's JSON columns can only be matched by full literal — you can't assert "Payload.OrderId equals X".
2. **No capture-as-token.** A DB query that returns a freshly-created Job ID can't feed it forward as `{{JobId}}` to a sibling post-step. End-to-end flows have to round-trip through aseXML/UI side-effects today.
3. **Equality only.** No contains / regex / numeric / date-with-tolerance / IS NULL / range. Date and decimal columns currently fail comparison through `.ToString()` because of trailing zeros / fractional seconds / culture differences.
4. **NULL collapsed into empty string.** `reader.IsDBNull(ord) ? "" : reader.GetValue(ord)?.ToString() ?? ""` (`DbCheckAgent.cs:187`) makes "column was NULL" and "column was empty string" indistinguishable.
5. **Single hardcoded connection.** `DbCheckAgent.ResolveConnectionString` (`DbCheckAgent.cs:113-121`) only routes `"BravoDb"`. SDR / Reporting / customer-secondary DBs are unreachable. The XML doc on `DbCheckStepDefinition.cs:24` flags this as Slice 1 scope.
6. **No editor dialog.** `PostStepsPanel.tsx:240-285` only wires `EditWebUiTestCaseDialog` and `EditDesktopUiTestCaseDialog`. Clicking the edit pencil on a `dbCheck` post-step does nothing — they're effectively read-only after chat creation, and only deletable.
7. **Failure diagnostics are a single concatenated line.** `"Column value mismatch: col1: expected '...', got '...'; col2: ..."` — when a query returns 12 columns and one fails, the user can't see what the other 11 contained.
8. **No `/add-db-assert` skill.** `/add-asexml-verification` exists; the parallel for DB assertions doesn't.
9. **Chat prompt teaches only the legacy shape.** `ChatIntentService.cs:468-475` hands the LLM `expectedRowCount` + `expectedColumnValues` (flat dict). Once the new shape exists, the prompt must teach it — otherwise the assistant keeps generating the legacy shape forever.

## Scope — what's in

### 1. Per-column rich assertions (data-model change)

Replace `ExpectedColumnValues: Dictionary<string,string>` on `DbCheckStepDefinition` with a structured list:

```csharp
public List<ColumnAssertion> ColumnAssertions { get; set; } = [];
```

Each `ColumnAssertion`:
- `Column` (required) — name from the SELECT result set
- `JsonPath` (optional) — JSONPath inside the column's value, e.g. `$.OrderId`, `$.Items[0].Code`
- `Operator` — enum (string-serialised): `Equals` (default) / `NotEquals` / `Contains` / `NotContains` / `StartsWith` / `EndsWith` / `Regex` / `GreaterThan` / `LessThan` / `Between` / `IsNull` / `IsNotNull` / `EqualsNumeric` / `EqualsDate`
- `Expected` (string, lenient — `{{Token}}` substituted)
- `Expected2` (string, used by `Between`)
- `IgnoreCase` — defaults true for string ops, false for numeric/date
- `ToleranceSeconds` (used by `EqualsDate`) / `ToleranceDelta` (used by `EqualsNumeric`)

**Back-compat (mandatory):** when JSON contains the legacy `expectedColumnValues` dict, it must deserialise into `ColumnAssertions` as one `Equals` assertion per entry, mirroring the `ApiDefinitionCompat`/`WebUiDefinitionCompat` pattern in `TestObjective.cs:98-122`. The legacy property setter promotes into the new list and never serialises back. **Do not write a one-time migration** — the JSON shim is the contract.

`ExpectedRowCount` stays as-is (mutually exclusive with `ColumnAssertions` — an assertion list with 0 entries plus a non-null `ExpectedRowCount` runs row-count mode).

### 2. JSON column support

When `ColumnAssertion.JsonPath` is non-empty:
- Read the column's value as a string. If it doesn't parse as JSON (`JsonDocument.TryParseValue`), the assertion **fails** (not errors) with `"column 'X' is not JSON"`.
- Evaluate the path. Path-not-found → fails with `"JSON path '$.X.Y' not found in column 'X'"`. Path resolves to a JSON value → run the operator against the extracted scalar (objects/arrays as the JSON-text form).
- `IsNull` / `IsNotNull` distinguish "JSON null" from "missing path" — both fail `IsNotNull` but only "JSON null" passes `IsNull`. Document this clearly in the agent code.

**Library choice — planner picks one and justifies in the plan:**
- `JsonPath.Net` (NuGet, System.Text.Json native, ~50 KB) — recommended; spec-aligned, predictable.
- A hand-rolled subset supporting `$`, dotted props, `[N]` indexes — minimal footprint, controllable, but you'll be playing whack-a-mole when users want filters.

Recommendation: `JsonPath.Net`. Confirm license is acceptable (MIT) before committing.

### 3. Capture as `{{Token}}`

New optional list on `DbCheckStepDefinition`:

```csharp
public List<ColumnCapture> Captures { get; set; } = [];
```

Each `ColumnCapture`:
- `Column` (required) — column name
- `JsonPath` (optional) — extract a sub-value from a JSON column
- `As` (required) — token name to bind, e.g. `"JobId"` (no braces)
- `Required` — defaults `true`; if true and the column/path is null/missing, the step fails. If false, the token is left undefined (subsequent `{{JobId}}` stays as a literal — lenient mode, logged as WARN).

**Runtime contract** — captures are merged into the per-objective post-step run context that `PostStepOrchestrator` already threads to siblings. Concretely:
- After the DB step succeeds (or partially succeeds — captures still apply if the assertion list passed), the captured `Dictionary<string,string>` is merged into the running context dict that subsequent post-steps receive at `StepParameterSubstituter.Apply` time.
- Precedence: **captured > parent context > env params**. If a capture overwrites a parent key, log INFO. (Planner: confirm by reading `PostStepOrchestrator.RunInlineAsync` — adjust if the precedence is already coded differently.)
- The captured-tokens dict **must round-trip through `DeferredVerificationRequest`** so deferred siblings get the captured value when they fire later. Touch `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` to add `CapturedTokens: Dictionary<string,string>` and serialise it through.

### 4. Multi-DB connection registry

Lift connection routing out of `DbCheckAgent.ResolveConnectionString`. Add to `IEnvironmentResolver`:

```csharp
string? ResolveDbConnectionString(string connectionKey, string? envKey);
```

Backed by:
- `EnvironmentConfig.DbConnections: Dictionary<string,string>` (per-customer-env)
- `TestEnvironmentConfig.DbConnections: Dictionary<string,string>` (top-level fallback)
- `BravoDbConnectionString` stays as a back-compat fallback for the `"BravoDb"` key only — when neither `DbConnections.BravoDb` is set anywhere, the resolver returns `BravoDbConnectionString`. New keys (e.g. `"SdrReportingDb"`) must use the new dict.

Unknown keys → resolver returns `null` → agent surfaces as `TestStatus.Error` (config issue, not data issue), with a message naming the key and the env. Match the existing pattern in `BravoEndpointResolver` for "key not in config".

`appsettings.example.json` gets a worked example showing one global + one per-env override.

### 5. NULL / type fidelity

Rewrite the assertion loop to stop forcing `.ToString()` upfront:

- Read raw `object?` via `reader.GetValue(ord)`.
- For `IsNull` / `IsNotNull` — check `reader.IsDBNull(ord)` directly (or the JSON value's `ValueKind == Null` after path extraction).
- For `EqualsNumeric` — parse both sides as `decimal` with `CultureInfo.InvariantCulture`, compare with `ToleranceDelta`. Failure to parse expected/actual → fail with a typed message.
- For `EqualsDate` — parse both sides as `DateTimeOffset` with `InvariantCulture` + `DateTimeStyles.AssumeUniversal`, compare with `ToleranceSeconds`. Fall back to `DateTime.Parse` if `DateTimeOffset` parse fails. Failure → fail with typed message.
- For string ops — only then call `Convert.ToString(value, CultureInfo.InvariantCulture)`.

### 6. Pretty diagnostics on failure

When an assertion or row-count check fails:
- Capture the **full first row** (or first 3 rows for row-count failures) as a `Dictionary<string, string?>` with cell values truncated to 200 chars.
- Attach to `TestStep.Metadata["dbCheckRow"]` (or extend `TestStep` with a structured `Diagnostics` field if the planner finds the metadata bag is the wrong shape — match the existing convention in `ApiTestAgent.ValidateResponseAsync`).
- The single-line `Reason` stays human-readable; the structured row is for the run-detail UI to render.

UI side — `ExecutionDetailPage` / step-result rendering must surface the structured diagnostics for `db-check` steps as a small column→value table under the failure reason. (REQ-001 is concurrently standardising the execution UI — coordinate with whatever it lands on for step-detail rendering.)

### 7. In-UI editor — `EditDbCheckStepDialog.tsx`

Wire it into `PostStepsPanel.tsx` next to the existing webUi/desktopUi branches (after `PostStepsPanel.tsx:285`). Fields:

- **Name** — single-line input.
- **Connection key** — dropdown sourced from a new `GET /api/db-check/connections` endpoint that returns the keys configured for the active env. Free-text fallback for power users (planner: pick one and document — recommend dropdown only, with a "request a new key" hint that points at the appsettings doc).
- **SQL** — multi-line textarea with monospaced font. `{{Token}}` autocomplete pulled from the parent objective's `parentSteps[*].outputTokens` (a small extension of the existing chat-context shape) plus the active env's `EnvironmentParameters` keys. Highlight tokens visually using the existing `highlightTokensStr` helper already imported in `PostStepsPanel.tsx`.
- **Expected row count** — optional number input. Mutually exclusive with the assertion list (gate via radio: "Assert row count" / "Assert column values").
- **Column assertions table** — one row per assertion: `Column` text, `JsonPath` text (placeholder `$.OrderId`), `Operator` dropdown, `Expected` text, `Expected2` (only for `Between`), `IgnoreCase` checkbox, `Tolerance` (only for date/numeric ops), `×` delete button. `+ Add assertion` button at the foot.
- **Captures table** — one row per capture: `Column`, `JsonPath`, `As` (token name), `Required` checkbox, `×`. `+ Add capture`.
- **Timeout (seconds)** — number input, default 15.
- **Try query** button — calls `POST /api/db-check/dry-run` (see §8) and renders columns + first 5 rows in a small table. Each cell has a `+` button: clicking it inserts an `Equals` assertion for that column with the cell value pre-filled (or, if the column is JSON, prompts for a `JsonPath`).

Save → PUTs through the existing `updatePostStep` (no new endpoint needed for save — the new shape lives inside the post-step JSON the endpoint already round-trips). Delete → existing `deletePostStep`.

### 8. `POST /api/db-check/dry-run`

New endpoint in a new `src/AiTestCrew.WebApi/Endpoints/DbCheckEndpoints.cs`:

- Request: `{ envKey, connectionKey, sql, parameters: Dictionary<string,string> }` — the `parameters` dict is substituted into `sql` server-side using `TokenSubstituter` so the user can preview with realistic values.
- Validates SQL via `DbCheckSqlGuardrails`.
- Resolves connection via `IEnvironmentResolver.ResolveDbConnectionString(connectionKey, envKey)`.
- Runs the SELECT with `CommandTimeout=10` (lower than the runtime default so a slow exploratory query doesn't hold the UI), returns column names + up to 5 rows + total row count.
- Each cell is truncated to 500 chars in the response and tagged with the SQL Server type name so the UI can render hints for JSON columns.
- Auth: requires the existing logged-in user (existing JWT/cookie check on the WebApi). Rate-limit per user — 10 requests / minute, return 429 above that. Match the limit pattern of any existing analogous endpoint; if none, add a small in-memory token bucket.
- Per-env opt-in: `Environments.<key>.AllowDbDryRun: true` (planner: confirm naming with the existing `RunDataPacksOnStartup` pattern). Default `true` for envs without an explicit flag — but the planner should propose a default for production-style envs and confirm with stakeholder before merging.

### 9. NL authoring — chat assistant + `/add-db-assert` skill

**Chat prompt update** (`src/AiTestCrew.WebApi/Services/ChatIntentService.cs:457-481`):
- Replace the `"expectedColumnValues": { <col>: "<expected value>", ... }` example with the new `columnAssertions` array shape.
- Add the `captures` field to the documented payload.
- Add 2-3 worked examples in the prompt — at least one with a JsonPath assertion and one with a capture.
- Update the post-step authoring rules block (`ChatIntentService.cs:537-542`) so the LLM knows: "for a JSON column, prefer a `jsonPath` assertion over a substring match on the whole column"; "if the user wants the row's ID for later, emit a `captures` entry".

**`ConfirmCreatePostStepCard.tsx`** — render the new shape so the user sees the assertion list + captures cleanly before confirming.

**`/add-db-assert` skill** — new file `.claude/skills/add-db-assert/SKILL.md` plus any asset folder needed. Mirrors `/add-asexml-verification`:
- Args: `<moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`.
- Resolves the parent step from the test set (read-only, via existing repo).
- Drafts SQL + assertions + captures from the description (reuses the same prompt skeleton as the chat assistant — share the prompt fragment via a small included file rather than duplicating).
- Calls `POST /api/db-check/dry-run` to validate the SQL compiles + runs against the active env.
- PUTs the post-step via `updatePostStep` (or appends to the parent's `PostSteps`).
- On dry-run failure, prints the column list it _did_ see and a suggested SQL correction — does not silently try other shapes.

### 10. Documentation + extension map

- `docs/architecture.md` — new "DB Assert Step" section: data model (assertions + captures), runtime path, JSONPath evaluator choice (and why), capture semantics + precedence rules, multi-DB resolution, security envelope (read-only guardrails, dry-run gating).
- `docs/functional.md` — user-facing "Asserting database state" section under Run Modes / Post-Steps. Include the chat NL example, the `/add-db-assert` invocation, and a screenshot-worthy walkthrough of the editor.
- `docs/file-map.md` — append the new files under a new `## DB Assert step` heading.
- `CLAUDE.md` "Where to extend — quick map" table — add rows:
  - "A new DB connection (e.g. SDR Reporting DB) → config-only — `Environments.<key>.DbConnections.<connectionKey>` (or top-level `TestEnvironment.DbConnections.<connectionKey>` fallback). Zero code."
  - "A new column-assertion operator → enum + evaluator branch. Files: `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs`, `src/AiTestCrew.Agents/DbAgent/ColumnAssertionEvaluator.cs`. Update editor dropdown + chat prompt examples."
- `CLAUDE.md` "Available slash commands" table — add `/add-db-assert <moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`.

### 11. Tests

- **Unit (new file `tests/AiTestCrew.Agents.Tests/DbAgent/ColumnAssertionEvaluatorTests.cs`)**: every operator across (string col, numeric col, date col, JSON col with `JsonPath`, NULL col, missing JSON path). Each operator gets a happy-path + at least one failure case + at least one edge case (empty string vs NULL, `0` vs `0.00`, `2026-01-01` vs `2026-01-01T00:00:00Z`).
- **Unit `DbCheckSqlGuardrailsTests.cs`** (extend or add): covers all currently-implemented denials + a new test for `WITH x AS (SELECT 1) INSERT INTO y SELECT * FROM x` (must reject — currently `INSERT` keyword catches it; assert).
- **Unit `StepParameterSubstituterTests.cs`** (extend): the new `DbCheckStepDefinition` substitution touches `Sql`, `ColumnAssertions[*].Expected`, `Expected2`, `JsonPath` (yes — see Open Questions); `Captures.Column` (yes — column names can be tokens for advanced flows); `Captures.As` is **NOT** substituted (it's a token target, substituting it would let parent context redirect captures unexpectedly).
- **Unit `EnvironmentResolverTests.cs`** (extend): `ResolveDbConnectionString("BravoDb", env)` falls back to `BravoDbConnectionString` when no `DbConnections.BravoDb` is set; `ResolveDbConnectionString("SdrReportingDb", env)` returns null gracefully when nothing is configured.
- **Integration**: full post-step dispatch through `PostStepOrchestrator` with (a) row-count assertion, (b) column-value assertion, (c) JsonPath assertion, (d) capture-and-reuse-in-next-post-step. Use the existing test-DB infra if present; otherwise the planner proposes (LocalDB / Testcontainers SQL Server / sqlite + a thin abstraction). Recommendation: **Testcontainers SQL Server** — same engine, deterministic.
- **WebApi**: at least one happy-path test for `POST /api/db-check/dry-run` (guardrail rejection, env-not-found, row truncation).

## Scope — explicitly out

- **Multi-row assertions** (assert across all returned rows, ALL/ANY semantics). The user's brief explicitly says "assumption is the query always returns one record; if not, assert applies on the first row" — keep that. A future REQ can add a `RowSelector { First, Last, All, Where(...) }`.
- **Aggregate operators** (`MIN/MAX/SUM/AVG` on a column) — out. Wrap in the user's SELECT (`SELECT MIN(CreatedAt) AS oldest FROM ...`) is enough.
- **Write-mode SQL** (UPDATE/INSERT/DELETE/MERGE for fixture setup) — explicitly **forbidden**, not deferred. Use the existing `BravoTeardownExecutor` or startup data packs.
- **Recording flow integration** — DB checks are dialog/chat/skill-authored only; no DB recorder. Keeps the model simple. (`/add-asexml-verification` is for recorded UI verifications; `/add-db-assert` is the parallel for DB but bypasses recording entirely.)
- **Schema introspection / "auto-discover columns"** — out; the dry-run returns columns, that's enough.
- **Standalone "DB Assert" objective** (top-level test type, not a post-step) — out. DbCheckAgent is post-step-only by design.
- **Distributed-execution / agent-capability changes** — `Db_SqlServer` already exists in the capability registry. Verify that an Online agent advertising it can pick up a deferred DB post-step (planner: confirm in `RunDispatchHelper.cs` and `JobExecutor.cs`); no change expected.
- **Top-level "DB Health Panel" on the dashboard** — nice-to-have; out for this slice. The dry-run endpoint covers manual probing.
- **REQ-001 visual concerns** — the failure-row table in step detail must use the standardised execution components REQ-001 lands on. If REQ-001 hasn't merged when this requirement implements, prefer raw HTML and leave a TODO referencing REQ-001's component names.

## Acceptance criteria

A reviewer should be able to verify each of these without ambiguity:

1. **Data model.** `DbCheckStepDefinition.ColumnAssertions: List<ColumnAssertion>` and `Captures: List<ColumnCapture>` exist and serialise. A test set saved with the legacy `expectedColumnValues` dict deserialises into a `ColumnAssertions` list of `Equals` entries; round-tripping the file (load → save) emits the new shape, and an existing run's history is unaffected.
2. **JSON column.** A DB check with `JsonPath="$.OrderId"` on a column whose value is `'{"OrderId":"123","Other":1}'` and `Expected="123"` passes. The same check with `JsonPath="$.Missing"` fails with `"JSON path '$.Missing' not found in column 'Payload'"`. A non-JSON column fails with `"column 'Payload' is not JSON"`.
3. **Capture round-trip.** A DB post-step with `Captures: [{Column:"JobId", As:"JobId"}]` followed by a sibling API post-step using `{{JobId}}` in the URL substitutes correctly at runtime — verified by an integration test that dispatches both inline and via deferred mode (`waitBeforeSeconds > defer threshold`).
4. **Operators.** Unit tests pass for every operator (Equals / NotEquals / Contains / NotContains / StartsWith / EndsWith / Regex / GreaterThan / LessThan / Between / IsNull / IsNotNull / EqualsNumeric / EqualsDate) across at least string, numeric, date, NULL, and JSON-path-extracted scalars.
5. **NULL fidelity.** A column whose value is SQL NULL passes `IsNull`, fails `IsNotNull`, and fails `Equals ""` (i.e. NULL ≠ empty string).
6. **Multi-DB.** `appsettings.json → TestEnvironment.Environments.<env>.DbConnections.SdrReportingDb` is honoured by `IEnvironmentResolver.ResolveDbConnectionString("SdrReportingDb", env)`. `BravoDb` falls back to the legacy `BravoDbConnectionString` when the new dict has no `BravoDb` entry. An unknown key returns `null` and the agent surfaces it as `TestStatus.Error` with a message naming the key and env.
7. **Editor dialog.** Clicking "Edit" on a `dbCheck` post-step in `PostStepsPanel.tsx` opens `EditDbCheckStepDialog` with all current values populated. "Try query" returns columns + first 5 rows. Saving PUTs through `updatePostStep` and the panel refreshes. Delete removes it via `deletePostStep`. (Today the edit pencil on a `dbCheck` post-step does nothing.)
8. **Chat assistant.** Sending the chat message "after the WinForms search step, confirm the Jobs table has a row where MessageID matches the just-delivered message and the Payload JSON's OrderId equals 12345, and capture JobId for later use" produces a `confirmCreatePostStep` action with: target=`Db_SqlServer`, a SELECT over Jobs filtered by `{{MessageID}}`, a `columnAssertions` entry with `Column="Payload"`, `JsonPath="$.OrderId"`, `Operator="Equals"`, `Expected="12345"`, and a `captures` entry `[{Column:"JobId", As:"JobId"}]`.
9. **Slash command.** `/add-db-assert <m> <ts> <o> AseXmlDeliver 0 "confirm Jobs has a row for the just-delivered MessageID with Status='Processed' and Payload.OrderId={{OrderId}}, capture JobId"` produces a working post-step end-to-end on a clean test set, including a green dry-run.
10. **Diagnostics.** A failing column-value assertion's run-detail UI shows the full first row as a column→value table under the failure reason (cells truncated to 200 chars).
11. **SQL guardrails.** Unit tests reject: `SELECT * FROM x; DROP TABLE y` (semicolon), `WITH x AS (SELECT 1) INSERT INTO y SELECT * FROM x` (write keyword in CTE), `EXEC sp_who` (denied keyword). Allow: `SELECT * FROM Jobs WHERE MessageID = '{{MessageID}}'`, `WITH cte AS (SELECT 1 AS x) SELECT * FROM cte`.
12. **Documentation.** `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, and `CLAUDE.md` (extension map + slash commands) are updated.

## Files most likely touched

**Backend (C#) — new:**

- `src/AiTestCrew.Storage/DbAgent/ColumnAssertion.cs`
- `src/AiTestCrew.Storage/DbAgent/ColumnCapture.cs`
- `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs` (enum)
- `src/AiTestCrew.Agents/DbAgent/ColumnAssertionEvaluator.cs`
- `src/AiTestCrew.Agents/DbAgent/JsonValueExtractor.cs` (thin JSONPath wrapper)
- `src/AiTestCrew.WebApi/Endpoints/DbCheckEndpoints.cs`

**Backend — modified:**

- `src/AiTestCrew.Storage/DbAgent/DbCheckStepDefinition.cs` — add `ColumnAssertions`, `Captures`, JSON-shim setter for legacy `expectedColumnValues`.
- `src/AiTestCrew.Agents/DbAgent/DbCheckAgent.cs` — assertion loop rewrite; capture path; switch to `IEnvironmentResolver.ResolveDbConnectionString`; structured failure metadata.
- `src/AiTestCrew.Agents/DbAgent/DbCheckSqlGuardrails.cs` — confirm WITH-CTE behaviour; add the new tests' coverage.
- `src/AiTestCrew.Agents/Environment/EnvironmentResolver.cs` — add `ResolveDbConnectionString(connectionKey, envKey)`.
- `src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs` — add the interface method.
- `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` — add `DbConnections: Dictionary<string,string>`.
- `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` — add top-level `DbConnections` fallback.
- `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` — substitute `Sql`, `ColumnAssertions[*].{Expected,Expected2,JsonPath,Column}`, `Captures[*].{Column,JsonPath}`; do NOT substitute `Captures[*].As`.
- `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs` — merge captured tokens into the run context for siblings; verify deferred path threads them through `DeferredVerificationRequest`.
- `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` — add `CapturedTokens: Dictionary<string,string>`.
- `src/AiTestCrew.Runner/Program.cs`, `src/AiTestCrew.WebApi/Program.cs` — DI for any new services (likely none — evaluator is static).
- `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` — system prompt updates for the new shape (lines 457-481, 537-542).
- `src/AiTestCrew.Runner/appsettings.example.json` — add `DbConnections` block + dry-run toggle example.

**Frontend (React/TS) — new:**

- `ui/src/components/EditDbCheckStepDialog.tsx`
- `ui/src/api/dbCheck.ts` — `dryRunDbCheck(envKey, connectionKey, sql, parameters)` + `getDbConnections(envKey)` clients.

**Frontend — modified:**

- `ui/src/types/index.ts` — extend `DbCheckStepDefinition` with `columnAssertions`, `captures`; add `ColumnAssertion`, `ColumnCapture`, `AssertionOperator` types.
- `ui/src/components/PostStepsPanel.tsx` — wire the new dialog into the editingIdx branch (after line 285); update `DbCheckBlock` (line 312) to render the new shape including assertion list + captures.
- `ui/src/components/chat/actions/ConfirmCreatePostStepCard.tsx` — render new shape.
- (REQ-001 may also touch this file — coordinate.)

**Skill / docs:**

- `.claude/skills/add-db-assert/SKILL.md` (new) + any asset.
- `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, `CLAUDE.md` — updates per §10.

**Tests:**

- `tests/AiTestCrew.Agents.Tests/DbAgent/ColumnAssertionEvaluatorTests.cs` (new)
- `tests/AiTestCrew.Agents.Tests/DbAgent/DbCheckSqlGuardrailsTests.cs` (extend or new)
- `tests/AiTestCrew.Agents.Tests/Environment/StepParameterSubstituterTests.cs` (extend)
- `tests/AiTestCrew.Agents.Tests/Environment/EnvironmentResolverTests.cs` (extend)
- `tests/AiTestCrew.WebApi.Tests/DbCheckEndpointsTests.cs` (new — happy path + guardrail + rate limit)
- Integration test for capture round-trip (location TBD by planner — match the existing convention).

## Open questions for the planner

1. **JSONPath library** — `JsonPath.Net` (recommended; check MIT license + footprint) vs hand-rolled subset. Decide and justify in the plan.
2. **Should `JsonPath` itself be `{{Token}}`-substituted?** Useful for dynamic indexes (`$.Items[{{Index}}].Code`). Recommend YES — JsonPath is read-only by definition, and consistency wins. Confirm there's no surprise where a token containing `'` could break the path syntax (unit-test it).
3. **`Captures` precedence vs env params** — when `{{JobId}}` is captured AND set in `EnvironmentParameters`, who wins? Recommend captured (most-recent) wins, log INFO so users notice. Confirm the precedence already enforced by `PostStepOrchestrator.RunInlineAsync` matches; document the answer either way.
4. **`Required: false` on a capture — what's in the run context when missing?** Recommend leave the token undefined (literal `{{JobId}}` survives lenient-mode substitution and is logged as WARN by the existing `unknownTokens` collector) rather than binding empty string. Loud > silent.
5. **Dry-run gating per env** — name the flag `Environments.<key>.AllowDbDryRun` (default true) or invert it as `BlockDbDryRun` (default false)? Recommend `AllowDbDryRun: true` — matches the positive-default of `RunDataPacksOnStartup`. Confirm with the operator (Kalhara) before merge for production envs.
6. **Failure metadata location** — `TestStep.Metadata["dbCheckRow"]` (loose dict) vs a typed `TestStep.Diagnostics` field. Recommend the metadata bag (zero-cost extension, matches existing patterns); if the planner finds the bag is being phased out, use the typed field.
7. **Editor connection dropdown vs free text** — recommend dropdown only with a help link to the appsettings doc; free-text is a foot-gun.
8. **`POST /api/db-check/dry-run` — auth + rate limit pattern** — match an existing analogous endpoint (`POST /api/chat/message`?). If no rate-limit infra exists, use a simple in-memory token bucket — keep this slice small.
9. **Test infrastructure for the integration test** — Testcontainers SQL Server (recommended) vs LocalDB (CI-friendly on Windows only) vs SQLite-with-thin-abstraction (fastest but lies about JSON behaviour). Pick one and justify.
10. **REQ-001 coordination** — the failure-row table needs the standardised execution-detail rendering REQ-001 introduces. If REQ-001 hasn't merged when this is implemented, leave the rendering vanilla and TODO-tag it; do NOT block on REQ-001.
11. **Should `ColumnAssertion.Column` itself be `{{Token}}`-substituted?** Edge case (templated column name). Recommend YES for consistency; trivial to implement and unit-test. The planner can flip the call if it surfaces a concrete risk.
12. **Migration of in-flight chat conversations** — once the chat prompt is updated to teach the new shape, an in-flight conversation with the old shape on the wire should still produce a valid action. The deserialiser shim (§1) handles this on the way in; verify the chat card renders a mixed-shape gracefully or normalises before display.
