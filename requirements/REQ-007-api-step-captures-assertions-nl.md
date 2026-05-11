---
id: REQ-007
title: REST API steps — response captures, structured assertions, NL injection skill
status: Proposed
created: 2026-05-09
author: Kalhara Samarasinghe
area: agents + ui
---

# REQ-007 — API step parity (captures, assertions, NL injection)

## Goal

Bring the REST API step to feature parity with **DB Assert** (REQ-002) and **Event Assert** (REQ-004), so a test author can chain API calls inside a single objective with captured values flowing forward and structured assertions on response bodies. Today the API agent is the *oldest* step type in the system and is the only one still relying on substring-match + LLM judgement for response validation; the newer step types have moved past that. Lift the API step to:

1. **Response captures (`Captures`)** — extract a value from the JSON response body via JSONPath (or a header / status code) and bind it to a `{{Token}}` so a sibling step (API, UI, DB, Event) can use it.
2. **Structured response assertions (`ApiAssertions`)** — operator-driven assertions on status code, header values, and JSON response body fields (JSONPath), parallel to `ColumnAssertion` (REQ-002 §1) and `EventCriterion` (REQ-004).
3. **`/add-api-step` skill** — NL authoring of an API step (HTTP method, path, body, captures, assertions) attached as a post-step under an existing parent, mirroring `/add-db-assert` and `/add-event-assert`.
4. **Chat-assistant prompt update** — teach the LLM the new `apiAssertions` + `captures` shape and add 2–3 worked examples covering capture-then-reuse and JSONPath assertion.
5. **In-UI editor surface** — extend the existing `EditTestCaseDialog.tsx` (or split out an `EditApiTestCaseDialog.tsx` if cleaner) with assertion + capture tables.

The API step **stays usable as both a top-level objective step AND a post-step** — both surfaces benefit from this change, and the data-model upgrade is identical for both. (`ApiTestDefinition` is already the carrier in both contexts; `ChatIntentService.cs:581` already wires `api` into `confirmCreatePostStep`.)

## Why now

- The user's stated need: "specific steps to call a REST API on a particular module with parameters saved during a test case and assert for the REST output values. NL capability must be there where user can describe what he needs the API details to inject a step to a test case." Today none of *captures*, *structured assertions*, or *NL injection of a single API step* are supported.
- DB Assert (REQ-002) and Event Assert (REQ-004) have already established the pattern (`Captures` + `Assertions` + chat intent + `/add-*` skill). This REQ is mostly *follow-the-pattern* work — low architectural risk, high test-author leverage.
- Without this, end-to-end API flows (POST → capture id → GET by id → assert content) have to be authored manually through the editor with brittle substring matches, and the LLM cannot reliably synthesise them from a description.

## Current-state findings (from audit — do NOT redo these)

✅ **Already in place — extend, don't rebuild:**

| Component | Path | Status |
|---|---|---|
| `ApiTestDefinition` | `src/AiTestCrew.Storage/ApiAgent/ApiTestDefinition.cs` | Carrier with Method / Endpoint / Headers / QueryParams / Body / ExpectedStatus / ExpectedBodyContains / ExpectedBodyNotContains / PostSteps. **No captures, no structured assertions.** |
| `ApiTestAgent` | `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | Resolves stack/module/env via `IApiTargetResolver`; executes via `HttpClient`; loops over `PreloadedTestCases` for reuse mode; `ValidateResponseAsync` runs the hybrid rule + LLM check. |
| Token substitution into API steps | `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs:35-55` | `Apply(ApiTestDefinition)` substitutes `Endpoint`, `Headers` values, `QueryParams` values, and `Body` (recursive). Fully working — captured tokens from siblings already flow IN to API steps. |
| Token substitution OUT of API steps | `ApiTestAgent.BuildPostStepContext` (`src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs:741-752`) | Publishes `Method`, `Endpoint`, `ParentCaseName` to post-steps. **Does not publish status code or response body fields.** |
| API step as a post-step | `src/AiTestCrew.Storage/AseXmlAgent/VerificationStep.cs` (`Api` carrier field) | Field exists; `Target="API_REST"` selects it. |
| Chat intent for API post-step | `src/AiTestCrew.WebApi/Services/ChatIntentService.cs:581` | `confirmCreatePostStep` already has `api: <ApiTestDefinition shape>` in its system prompt. Knows the legacy shape only. |
| `EditTestCaseDialog.tsx` | `ui/src/components/EditTestCaseDialog.tsx` | Manual editor for `ApiTestDefinition`. No assertions list, no captures list, no `{{Token}}` autocomplete. |
| `ConfirmCreatePostStepCard.tsx` | `ui/src/components/chat/actions/ConfirmCreatePostStepCard.tsx` | Renders API-shaped post-step from chat — knows the legacy shape only. |
| `TestObjective.EnumerateAllPostSteps()` | `src/AiTestCrew.Agents/Persistence/TestObjective.cs` | Already yields API post-steps under any of the five parent kinds. |
| `DeferredVerificationRequest.CapturedTokens` | `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` | If REQ-002 has merged, this field already exists. If not, REQ-002 adds it; this REQ depends on it. (Planner: check current state.) |

❌ **Gaps this requirement addresses:**

1. **No response captures.** `ApiTestDefinition` has no `Captures` field. `ApiTestCase` likewise. Compare `DbCheckStepDefinition.Captures: List<ColumnCapture>` (REQ-002 §3) and `EventAssertStepDefinition.Captures: List<EventCapture>` (REQ-004). A POST that returns `{"id": "abc"}` cannot bind `{{Id}}` for the next step.
2. **No structured assertions.** Validation surface is limited to `ExpectedStatus` (single int), `ExpectedBodyContains: List<string>` (substring), `ExpectedBodyNotContains: List<string>` (negative substring). No JSONPath, no operators (`Equals` / `Regex` / `GreaterThan` / `Between` / `IsNull` / etc.), no header assertions, no numeric/date tolerance.
3. **LLM-driven validation is opaque.** `ApiTestAgent.ValidateResponseAsync` (`ApiTestAgent.cs:487-579`) sends the response to Claude and stores a free-text reason. Useful for fuzz/discovery, **not** useful for deterministic regression assertions. There's no way for the user to say "fail if `$.data.status != 'active'`" and get a typed pass/fail.
4. **Status / response body not in post-step context.** `BuildPostStepContext` (lines 741-752) publishes the URL bits and the parent case name — not the response status, not response body fields. A post-step DB check that wants to confirm "what the API returned matches what's persisted" can't see the API response.
5. **No `/add-api-step` skill.** `/add-db-assert` and `/add-event-assert` exist; the parallel for API steps doesn't. The user can't run `/add-api-step <m> <ts> <o> <parentKind> <parentIdx> "POST /accounts with body {…}, capture id, assert status 201"`.
6. **Chat-prompt teaches only the legacy shape.** `ChatIntentService.cs:581` documents `api: { <ApiTestDefinition shape> }` with the existing fields only. The LLM will keep generating the legacy shape until the prompt is updated, even after the data model lands.
7. **Editor dialog has no assertion / capture surface.** `EditTestCaseDialog.tsx` only exposes the legacy fields. Even after the chat lands a structured payload, the user can't edit the assertions / captures from the UI.
8. **`Detail` blob is the only failure diagnostic.** When a body-substring assertion fails, the user sees the entire response body dumped into `TestStep.Detail`. There's no per-assertion pass/fail breakdown like DB and Event have.

## Scope — what's in

### 1. Per-assertion structured response checks (data-model change)

Add to `ApiTestDefinition` (and mirror on `ApiTestCase` if the runtime case still exists separately):

```csharp
public List<ApiAssertion> ApiAssertions { get; set; } = [];
```

Each `ApiAssertion`:

- `Source` — enum: `Status` (the HTTP status code) / `Header` (a named response header) / `Body` (a JSONPath against the parsed JSON response body) / `BodyText` (raw response body as one string).
- `HeaderName` (used when `Source = Header`).
- `JsonPath` (used when `Source = Body`) — e.g. `$.data.id`, `$.items[0].status`. `{{Token}}`-substituted (so dynamic indexes work, see §3 of REQ-002 open questions — same answer).
- `Operator` — enum, the same surface DB Assert uses (REQ-002 §1): `Equals` / `NotEquals` / `Contains` / `NotContains` / `StartsWith` / `EndsWith` / `Regex` / `GreaterThan` / `LessThan` / `Between` / `IsNull` / `IsNotNull` / `EqualsNumeric` / `EqualsDate`.
- `Expected` (string, lenient — `{{Token}}` substituted).
- `Expected2` (string, used by `Between`).
- `IgnoreCase` — defaults true for string ops, false for numeric/date.
- `ToleranceSeconds` (used by `EqualsDate`) / `ToleranceDelta` (used by `EqualsNumeric`).

**Reuse, don't fork:** the operator enum + evaluator from REQ-002 (`AssertionOperator` + `ColumnAssertionEvaluator`) must be reused — extract into a shared `src/AiTestCrew.Agents/PostSteps/ScalarAssertionEvaluator.cs` (or similar) if needed and have both `ColumnAssertionEvaluator` and a new `ApiAssertionEvaluator` call it. **Do not duplicate the operator switch.** If REQ-002 has merged before this lands, the planner extracts; if not, REQ-002 owns the extraction and this REQ blocks on it.

**Back-compat (mandatory):** when JSON contains the legacy `expectedStatus` (int), `expectedBodyContains` (List<string>), or `expectedBodyNotContains` (List<string>), they must continue to deserialise into the existing fields AND additionally promote into `ApiAssertions` at load time:

- `ExpectedStatus = 200` → `ApiAssertion { Source = Status, Operator = Equals, Expected = "200" }`.
- Each `ExpectedBodyContains[i]` → `ApiAssertion { Source = BodyText, Operator = Contains, Expected = bodyContainsValue }`.
- Each `ExpectedBodyNotContains[i]` → `ApiAssertion { Source = BodyText, Operator = NotContains, Expected = ... }`.

The promotion is one-way at load time (mirrors REQ-002 §1's `expectedColumnValues` shim). On save, only the new `ApiAssertions` list is written — the legacy fields are left empty `[]` / `0` and no longer drive evaluation. Existing test sets must continue to pass / fail identically after migration; covered by an integration test (§11).

### 2. Response captures

Add to `ApiTestDefinition`:

```csharp
public List<ApiCapture> Captures { get; set; } = [];
```

Each `ApiCapture`:

- `Source` — same enum as `ApiAssertion.Source`: `Status` / `Header` / `Body` / `BodyText`.
- `HeaderName` (used when `Source = Header`).
- `JsonPath` (used when `Source = Body`).
- `As` (required) — token name to bind, e.g. `"AccountId"` (no braces). **Not `{{Token}}`-substituted** — same rule as REQ-002 §3.
- `Required` — defaults `true`. If true and the source is missing (status code is always present, but a header may be absent or a JSONPath may not resolve), the step fails. If false, the token stays undefined (lenient mode — `{{AccountId}}` literal survives, logged WARN by the existing `unknownTokens` collector).

**Runtime contract** — captures merge into the per-objective post-step run context after the API step succeeds (or partially succeeds — captures still apply if assertions on different paths failed; same rule as DB). Reuse the merge path REQ-002 §3 introduces in `PostStepOrchestrator` — do not add a parallel one.

**Deferred path:** captured tokens must round-trip through `DeferredVerificationRequest.CapturedTokens` (added by REQ-002, see Current-state §9). Verify the existing serialiser handles a Dict added by an API step (it should — the field is generic).

### 3. Status + response body in post-step context

Extend `ApiTestAgent.BuildPostStepContext` (`ApiTestAgent.cs:741-752`) to publish:

- `{{ResponseStatus}}` — the integer status code as a string.
- `{{ResponseBody}}` — the raw response body, truncated to 16 KB (configurable via `TestEnvironmentConfig.Api.PostStepBodyTruncationBytes`, default 16384). Larger bodies still parse cleanly for `Captures` / `ApiAssertions` evaluated *inside* the API agent, but the post-step text token caps at 16 KB to avoid bloating the run context.
- `{{ResponseHeader.<Name>}}` — for each response header, lower-case-keyed. (Optional; recommend yes — costs nothing and is occasionally invaluable. Planner: confirm naming convention with `MessageFieldResolver` from REQ-004 if that introduced a similar pattern.)

These are *automatic* context tokens, separate from explicit `Captures`. Captures are for sibling steps; these are for *immediate* post-step body/header use without ceremony. Document in the chat prompt.

### 4. Pretty diagnostics on failure

When an `ApiAssertion` fails:

- Capture the response status, response headers (truncated 50 entries), and response body (truncated 4 KB) in `TestStep.Metadata["apiResponse"]` (or extend `TestStep.Diagnostics` if REQ-002 §6 chose the typed field). Match whatever REQ-002 settled on.
- Per-assertion pass/fail breakdown in `TestStep.Metadata["apiAssertions"]` as a `List<{ Source, Operator, Expected, Actual, Passed, Reason }>` so the run-detail UI can render an assertion table.
- The single-line `TestStep.Reason` stays human-readable: `"3 of 5 API assertions failed"` (or the single-assertion message for single-assertion cases).

UI side — `ExecutionDetailPage` / step-result rendering must surface the structured diagnostics for `api` steps as an assertion table + response panel under the failure reason. (Coordinate with REQ-001 standardised execution UI — same TODO-tag rule as REQ-002 §6.)

### 5. LLM validation — keep, but make it opt-in

The hybrid LLM validation in `ApiTestAgent.ValidateResponseAsync` (`ApiTestAgent.cs:487-579`) stays useful for:

- Fuzz tests (`IsFuzzTest = true`) where the user has no fixed expected output.
- Generated-objective Normal-mode runs where the LLM is grading its own work.

But for a step with `ApiAssertions.Count > 0`, **structured assertions are authoritative** — the LLM step must NOT override a structured pass/fail. Concretely:

- If `ApiAssertions.Count > 0` and all pass → `TestStatus.Passed`. LLM step is **skipped** (cost saving).
- If `ApiAssertions.Count > 0` and any fail → `TestStatus.Failed`. LLM step is **skipped**. The structured failure list is the truth.
- If `ApiAssertions.Count == 0` and `ExpectedBodyContains/NotContains/Status` are all defaults → fall back to the LLM hybrid validation as today (preserves Normal-mode behaviour).
- If `ApiAssertions.Count == 0` but legacy fields are non-default → the §1 shim has already promoted them, so this branch shouldn't fire after the shim runs. Add a defensive assertion + log to catch shim regressions.

Document this precedence in the agent code and in the architecture doc.

### 6. In-UI editor surface

Extend `EditTestCaseDialog.tsx` (the existing manual editor) with:

- **Assertions table** — one row per assertion: `Source` dropdown (Status / Header / Body / BodyText), `HeaderName` text (only when Source=Header), `JsonPath` text (only when Source=Body), `Operator` dropdown, `Expected` text, `Expected2` (only for Between), `IgnoreCase`, `Tolerance` (only for date/numeric ops), `×` delete. `+ Add assertion` at the foot.
- **Captures table** — one row per capture: `Source` dropdown, `HeaderName` (Header only), `JsonPath` (Body only), `As` (token name), `Required`, `×`. `+ Add capture`.
- **`{{Token}}` autocomplete** in `Endpoint`, `Body`, header values, and `Expected` fields — pulled from the existing chat-context shape (parent objective's published tokens + active env's `EnvironmentParameters` keys + this step's own captures, so authors see the tokens they're about to bind). Reuse the `highlightTokensStr` helper used by `PostStepsPanel.tsx`.
- **"Try call" button** — calls `POST /api/api-step/dry-run` (see §7) and renders status + headers + body. Each scalar field in the JSON body has a `+ Capture` and `+ Assert` button: clicking inserts a capture or `Equals` assertion with that field's path pre-filled.

Save → existing PUT path for the test set / post-step (no new endpoint needed for save — the new fields ride inside the existing API step JSON).

If `EditTestCaseDialog.tsx` has grown to the point where the diff would be unwieldy, the planner is free to split a focused `EditApiTestCaseDialog.tsx` and route to it from the test-set view + `PostStepsPanel.tsx`. Recommendation: extend in place unless the file is already > 600 lines.

### 7. `POST /api/api-step/dry-run`

New endpoint in a new `src/AiTestCrew.WebApi/Endpoints/ApiStepEndpoints.cs`:

- Request: `{ envKey, stackKey, moduleKey, method, endpoint, headers, queryParams, body, parameters: Dictionary<string,string> }` — the `parameters` dict is substituted into all string fields server-side using `TokenSubstituter` so the user can preview with realistic values.
- Resolves URL via `IApiTargetResolver.ResolveApiBaseUrl(stackKey, moduleKey, envKey)` and auth via the per-(env,stack) `ITokenProvider`.
- Executes ONE HTTP call with `HttpClient.Timeout = 10s` (lower than the runtime default so a slow exploratory call doesn't hold the UI), returns status + headers + body (body truncated to 32 KB in the response).
- Auth: requires the existing logged-in user (existing JWT/cookie check on the WebApi). Rate-limit per user — match the bucket REQ-002 §8 introduces (10 / minute, return 429 above). Reuse infrastructure.
- Per-env opt-in: `Environments.<key>.AllowApiDryRun: true` (default true). Match REQ-002's `AllowDbDryRun` naming + default. Confirm with the operator before merge for production envs.

**Critical safety note** — unlike the DB dry-run (read-only via SQL guardrails), the API dry-run can hit *any* endpoint including writes (POST/PUT/DELETE). The dry-run sends the request **as-is**. The UI must show a clear "this will call the real API" warning when `Method != GET`. Document this in `architecture.md` security envelope. No method-allowlist gate — that would defeat the feature; the warning + per-env opt-in is the safety surface.

### 8. NL authoring — chat assistant + `/add-api-step` skill

**Chat prompt update** (`src/AiTestCrew.WebApi/Services/ChatIntentService.cs:581` and the surrounding rules block):

- Replace the legacy `expectedStatus` / `expectedBodyContains` / `expectedBodyNotContains` example with the new `apiAssertions` array shape.
- Add the `captures` field to the documented payload.
- Add 2–3 worked examples in the prompt — at least one with a JSONPath assertion, one with a capture flowing into a sibling step, and one asserting a header.
- Update the post-step authoring rules so the LLM knows: "for a JSON response, prefer a `Body` + `JsonPath` assertion over a `BodyText` substring match"; "if the user wants to use a returned ID later, emit a `captures` entry"; "structured assertions take precedence over the legacy `expectedBodyContains` shape — always emit `apiAssertions`".

**`ConfirmCreatePostStepCard.tsx`** — render the new shape so the user sees the assertion list + captures cleanly before confirming.

**`/add-api-step` skill** — new file `.claude/commands/add-api-step.md`. Mirrors `/add-db-assert` and `/add-event-assert`:

- Args: `<moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`.
- Resolves the parent step from the test set (read-only, via existing repo).
- Drafts Method + Endpoint + Headers + QueryParams + Body + ApiAssertions + Captures from the description (reuses the same prompt skeleton as the chat assistant — share the prompt fragment via a small included file rather than duplicating).
- Calls `POST /api/api-step/dry-run` to validate the endpoint resolves + responds (and to surface the response shape so the LLM can refine its JSONPath choices on a second pass).
- PUTs the post-step via the existing `updatePostStep` (or appends to the parent's `PostSteps`).
- On dry-run failure, prints the resolved URL + status + body it received and does not silently retry with a different shape.

**Top-level "inject as parent step" path:** if `<parentKind>` is omitted, the skill appends a new top-level API step to the objective's `ApiSteps` list (creating the API stack/module slots from the test set's existing config — error if the test set has no API stack pinned). This covers the user's stated need to inject a *new step* into a test case as well as the post-step pattern.

### 9. Documentation + extension map

- `docs/architecture.md` — new "API Step (Captures & Assertions)" section: data model, runtime path, JSONPath evaluator (reused from REQ-002), `ApiAssertions` vs LLM-validation precedence, capture semantics, `{{ResponseStatus}}` / `{{ResponseBody}}` automatic tokens, dry-run endpoint security envelope.
- `docs/functional.md` — under "API testing", document the new assertion + capture surface, the `/add-api-step` skill, and a worked NL example.
- `docs/file-map.md` — append the new files under the existing API step entries.
- `CLAUDE.md` "Where to extend — quick map" — add rows:
  - "A new API assertion source (e.g. timing, response size) → enum + branch in `ApiAssertionEvaluator`." Files: the new evaluator + the editor dropdown + chat prompt examples.
  - "A new API response capture source → same as above; capture and assertion share the `Source` enum."
- `CLAUDE.md` "Available slash commands" — add `/add-api-step <moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`.

### 10. Tests

- **Unit `ApiAssertionEvaluatorTests.cs` (new)**: every operator across (status code, header value, JSONPath-extracted scalar — string / numeric / date / null / missing). Each operator gets a happy-path + at least one failure case + at least one edge case. Reuse `ColumnAssertionEvaluator`'s test scaffolding via the shared `ScalarAssertionEvaluator` if §1 extracted it.
- **Unit `ApiTestDefinitionShimTests.cs` (new)**: legacy `expectedStatus = 200` + `expectedBodyContains = ["foo"]` + `expectedBodyNotContains = ["bar"]` deserialises into 3 promoted `ApiAssertions`. Round-tripping (load → save → load) emits only the new shape and produces identical pass/fail results. An empty legacy + empty new = "use LLM validation" (covered separately in `ApiTestAgentTests`).
- **Unit `StepParameterSubstituterTests.cs` (extend)**: substitution covers `ApiAssertions[*].{JsonPath, HeaderName, Expected, Expected2}` and `Captures[*].{JsonPath, HeaderName}`; `Captures[*].As` is NOT substituted.
- **Unit `ApiTestAgentTests.cs` (extend)**: assertions-present path skips the LLM validator; assertions-absent + legacy-fields-default path falls back to LLM; assertions-absent + non-default legacy fields path = defensive assertion fires (regression catch).
- **Integration**: full run of a 3-step API objective: POST → capture id → GET by id → assert `$.id == {{capturedId}}` and `$.status == 'active'`. Run inline AND via deferred dispatch (capture must round-trip through `DeferredVerificationRequest`).
- **WebApi**: at least one happy-path test for `POST /api/api-step/dry-run` (env-not-found, stack-not-found, body truncation at 32 KB, 429 on rate limit).

## Scope — explicitly out

- **GraphQL / SOAP / gRPC** — out. REST-only. The module abstraction can be extended later for other protocols; a new step type is the right shape for those, not a flag on `ApiTestDefinition`.
- **Multi-call assertions** (assert across a *sequence* of API responses, e.g. "the count returned by GET /a then GET /b should sum to N") — out. Each step asserts on its own response. If you need cross-step arithmetic, capture both values and add a final post-step assertion (DB or future "expression assert" step).
- **Schema validation** (assert the response matches a JSON Schema or OpenAPI spec) — out for this slice. A future REQ can add `Operator = MatchesSchema` or a `SchemaUrl` field; the current operator surface covers most regression needs.
- **Polling assertions** (retry the API call until an assertion passes or a deadline) — out. The deferred-verification path (REQ-004) is the right home for "wait until X is true"; once REQ-004 lands, an API step can be dispatched via the same deferred mechanism with a `WaitBeforeSeconds` field. Re-evaluate after REQ-004 ships.
- **Recording flow integration** — API steps are dialog/chat/skill-authored only; no API recorder.
- **Distributed-execution / agent-capability changes** — `Api_Rest` already exists in the capability registry. Verify (planner: `RunDispatchHelper.cs` + `JobExecutor.cs`); no change expected.
- **Removing the LLM validator entirely** — out. It still earns its keep for fuzz / Normal-mode generation. The §5 precedence rule is enough.
- **A "top-level API step injection via chat" intent** (chat says "add a GET /accounts step to objective X" and the chat lands a new top-level step, not a post-step) — recommend in scope via the §8 skill (skill handles top-level injection when `<parentKind>` is omitted), but the chat-intent surface for top-level injection is **out** for this slice. The chat already handles top-level *test-case generation* via Normal-mode; adding "inject one step at a time via chat" would require a new intent (`confirmAddObjectiveStep` or similar) and isn't worth the surface area until users ask. The skill covers the workflow.
- **REQ-001 visual concerns** — same TODO-tag rule as REQ-002 §6.

## Acceptance criteria

A reviewer should be able to verify each of these without ambiguity:

1. **Data model.** `ApiTestDefinition.ApiAssertions: List<ApiAssertion>` and `Captures: List<ApiCapture>` exist and serialise. A test set saved with the legacy `expectedStatus` + `expectedBodyContains` + `expectedBodyNotContains` deserialises into a promoted `ApiAssertions` list; round-tripping (load → save) emits only the new shape; an existing run's pass/fail outcome is unchanged.
2. **Status / Header / Body / BodyText assertions.** Unit tests pass for every operator on every source: `Status Equals 201` passes for a 201 response and fails for 200; `Header Equals "application/json"` checks the named header; `Body $.data.id Equals "abc"` extracts via JSONPath; `BodyText Contains "ok"` does substring on the raw body.
3. **JSONPath edge cases.** `JsonPath="$.missing"` on a body that doesn't contain that path fails with `"JSON path '$.missing' not found in response body"`. A non-JSON response body fails any `Source=Body` assertion with `"response body is not JSON"`. `Source=Body` with `Operator=IsNull` distinguishes "JSON null" from "missing path" (only JSON null passes).
4. **Capture round-trip.** An API step with `Captures: [{Source: Body, JsonPath: "$.id", As: "AccountId"}]` followed by a sibling API step using `{{AccountId}}` in the URL substitutes correctly at runtime — verified via integration test, both inline and via deferred mode.
5. **Automatic context tokens.** A post-step under an API parent receives `{{ResponseStatus}}`, `{{ResponseBody}}` (truncated 16 KB), and `{{ResponseHeader.content-type}}` and can substitute them into its own fields.
6. **LLM precedence.** A step with `ApiAssertions.Count > 0` does NOT call `AskLlmAsync` for validation (verified by mock / spy). A step with no assertions and default legacy fields DOES call `AskLlmAsync` (preserves Normal-mode behaviour).
7. **Dry-run endpoint.** `POST /api/api-step/dry-run` resolves URL + auth via the standard resolvers, executes the call, returns status + headers + body (truncated 32 KB), respects the 10 / minute rate limit (429 above), and is gated by `Environments.<key>.AllowApiDryRun`. The UI shows a "this will call the real API" warning when `Method != GET`.
8. **Editor.** Clicking "Edit" on an API step (top-level or post-step) opens the dialog with assertions + captures tables populated. "Try call" returns status + body; clicking `+ Capture` on a JSON body field inserts a capture row pre-filled with the path; clicking `+ Assert` inserts an `Equals` assertion. Save PUTs through the existing endpoint and the panel refreshes.
9. **Chat assistant.** Sending "after the WinForms search step, call POST /accounts with body `{name:'Test', code:'ACME'}`, capture the returned id as AccountId, then assert status is 201 and `$.data.code` equals `ACME`" produces a `confirmCreatePostStep` action with: target=`API_REST`, the request payload, an `apiAssertions` list of two entries (`Status Equals 201`, `Body $.data.code Equals ACME`), and a `captures` entry `[{Source: Body, JsonPath: "$.data.id", As: "AccountId"}]`.
10. **Slash command.** `/add-api-step <m> <ts> <o> AseXmlDeliver 0 "GET /accounts/{{AccountId}} and assert $.status equals 'active' and capture $.lastUpdated as LastUpdated"` produces a working post-step end-to-end on a clean test set, including a green dry-run.
11. **Top-level injection.** `/add-api-step <m> <ts> <o> "" "" "POST /accounts ..."` (parent kind empty) appends a new top-level API step to the objective's `ApiSteps` list. Errors clearly when the test set has no API stack/module pinned.
12. **Diagnostics.** A failing API step's run-detail UI shows: per-assertion table (source / operator / expected / actual / passed) + response status + response body (truncated 4 KB). The single-line `Reason` is human-readable.
13. **Documentation.** `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, and `CLAUDE.md` (extension map + slash commands) are updated.

## Files most likely touched

**Backend (C#) — new:**

- `src/AiTestCrew.Storage/ApiAgent/ApiAssertion.cs`
- `src/AiTestCrew.Storage/ApiAgent/ApiCapture.cs`
- `src/AiTestCrew.Storage/ApiAgent/ApiAssertionSource.cs` (enum)
- `src/AiTestCrew.Agents/ApiAgent/ApiAssertionEvaluator.cs`
- `src/AiTestCrew.Agents/PostSteps/ScalarAssertionEvaluator.cs` (shared, extracted from REQ-002 if not already)
- `src/AiTestCrew.WebApi/Endpoints/ApiStepEndpoints.cs`

**Backend — modified:**

- `src/AiTestCrew.Storage/ApiAgent/ApiTestDefinition.cs` — add `ApiAssertions`, `Captures`; JSON-shim setter for legacy `expectedStatus/expectedBodyContains/expectedBodyNotContains`. Also propagate to `ApiTestCase` if it still exists separately (`FromTestCase`/`ToTestCase` updates).
- `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` — assertions-vs-LLM precedence in `ValidateResponseAsync`; capture extraction from response; structured failure metadata; extend `BuildPostStepContext` with `{{ResponseStatus}}`, `{{ResponseBody}}` (truncated), `{{ResponseHeader.<name>}}`.
- `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` — substitute new `ApiAssertions` + `Captures` fields; do NOT substitute `Captures[*].As`.
- `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs` — confirm capture-merge path from REQ-002 §3 also receives API-step captures (no new path; verify generic path covers this).
- `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` — confirm `CapturedTokens` field exists (added by REQ-002); add it if not yet merged.
- `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` — system prompt updates for the new `api` shape (`ChatIntentService.cs:581` and surrounding rules block).
- `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` — add `Api.PostStepBodyTruncationBytes` (default 16384).
- `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` — add `AllowApiDryRun: bool` (default true).
- `src/AiTestCrew.Runner/appsettings.example.json` — add the dry-run toggle example + post-step body truncation example.
- `src/AiTestCrew.Runner/Program.cs`, `src/AiTestCrew.WebApi/Program.cs` — DI for any new services (likely none — evaluator is static).

**Frontend (React/TS) — new:**

- `ui/src/api/apiStep.ts` — `dryRunApiStep(envKey, stackKey, moduleKey, method, endpoint, headers, queryParams, body, parameters)` client.
- (Optional) `ui/src/components/EditApiTestCaseDialog.tsx` — only if the planner finds `EditTestCaseDialog.tsx` is too large to extend in place.

**Frontend — modified:**

- `ui/src/types/index.ts` — extend `ApiTestDefinition` with `apiAssertions`, `captures`; add `ApiAssertion`, `ApiCapture`, `ApiAssertionSource` types.
- `ui/src/components/EditTestCaseDialog.tsx` — assertions table + captures table + token autocomplete + Try-call button + warn-on-write-method banner.
- `ui/src/components/PostStepsPanel.tsx` — update API post-step block to render assertions + captures (currently shows method/endpoint/expectedStatus only). Wire the edit dialog if it isn't already.
- `ui/src/components/chat/actions/ConfirmCreatePostStepCard.tsx` — render new shape.

**Skill / docs:**

- `.claude/commands/add-api-step.md` (new) — mirror `/add-db-assert` and `/add-event-assert`.
- `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, `CLAUDE.md` — updates per §9.

**Tests:**

- `tests/AiTestCrew.Agents.Tests/ApiAgent/ApiAssertionEvaluatorTests.cs` (new)
- `tests/AiTestCrew.Agents.Tests/ApiAgent/ApiTestDefinitionShimTests.cs` (new)
- `tests/AiTestCrew.Agents.Tests/ApiAgent/ApiTestAgentTests.cs` (extend — LLM precedence + post-step context)
- `tests/AiTestCrew.Agents.Tests/Environment/StepParameterSubstituterTests.cs` (extend)
- `tests/AiTestCrew.WebApi.Tests/ApiStepEndpointsTests.cs` (new — happy path + 429 + per-env gate)
- Integration test for capture round-trip (location to match REQ-002's integration test home).

## Open questions for the planner

1. **JSONPath library** — must reuse REQ-002's choice (whatever it picked: `JsonPath.Net` recommended). If REQ-002 hasn't merged, this REQ blocks on it OR the planner makes the choice on REQ-002's behalf (both REQs need the same evaluator).
2. **Operator-evaluator extraction** — REQ-002 §1 introduces `AssertionOperator` + `ColumnAssertionEvaluator`. This REQ wants to reuse that surface. Recommend extracting to `src/AiTestCrew.Agents/PostSteps/ScalarAssertionEvaluator.cs` so both API and DB share. If REQ-002 hasn't merged, coordinate; if merged but didn't extract, this REQ does the extraction as part of the work.
3. **Header capture / assertion casing** — HTTP headers are case-insensitive. Recommend the implementation lower-cases `HeaderName` on read and on assertion comparison; document it. Editor UI also lower-cases on save to keep things consistent.
4. **Body parse failure** — when the response body isn't JSON but the user has a `Source=Body` assertion, fail the assertion with a typed reason (per AC-3). Don't error the step — that hides the actual issue. Confirm this matches DB Assert's choice in REQ-002 §2.
5. **Captures from a failed step** — DB Assert REQ-002 §3 says captures still apply if other assertions failed. Recommend same here: the capture machinery runs regardless of assertion outcomes; if the capture itself fails (path missing, `Required: true`), that's a step failure independent of assertion failures. Clarify the precedence: a step can fail for either capture-missing OR assertion-failure, and both are reported.
6. **Body truncation** — `{{ResponseBody}}` token caps at 16 KB. Captures on `Source=BodyText` similarly cap? Recommend NO — captures see the full body (up to a safety ceiling of 8 MB) since they're typed extractions, not free-text dumps. Token-substitution is the bloat risk, not capture extraction. Confirm in tests.
7. **Dry-run safety for write methods** — UI banner is the only safeguard. Should there be a per-env toggle to *block* non-GET dry-runs in production envs? Recommend `Environments.<key>.AllowApiDryRunWriteMethods: bool` defaulting to `true` for non-prod envs, `false` for the prod env (configured per appsettings). Operator (Kalhara) confirms naming + defaults before merge.
8. **Top-level injection via skill** (§8 last paragraph) — when the user runs `/add-api-step` with empty parent kind/index, the skill appends to `objective.ApiSteps`. Confirm this matches the test set's authoring model (it does — `TestObjective.ApiSteps` is a list and accepts new entries; existing tests cover the migration). Edge case: what if the objective is `Source = "Recorded"` (UI-recorded)? Recommend allow it — adding an API step to a recorded objective is a useful augmentation pattern, not a violation. Unit-test the case.
9. **REQ-001 + REQ-002 + REQ-004 coordination** — same TODO-tag rule as REQ-002 §6. If concurrent REQs haven't merged, leave the rendering vanilla and TODO-tag.
10. **Test infrastructure for the integration test** — needs an HTTP server to spin up. Recommend `WebApplicationFactory<Program>` against a stub endpoint controller defined in the test project (lighter than spinning up the real Bravo API). Pick one and justify in the plan.
11. **Migration of in-flight chat conversations** — once the chat prompt is updated, in-flight conversations with the legacy shape on the wire still produce a valid action via the §1 shim. Verify the chat card renders a mixed-shape gracefully or normalises before display.
