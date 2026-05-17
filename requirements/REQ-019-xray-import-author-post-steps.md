---
id: REQ-019
title: Xray importer authors post-steps (DB Assert / Event Assert / API) and populates API bodies at import time
status: Proposed
created: 2026-05-17
author: Kalhara Samarasinghe
author-note: REQ-017 shipped with two related gaps that together leave imported objectives feeling half-done. (1) The LLM is told `postStep` is a valid mapping kind, but `MapRowsToObjective` has no switch case for it — those rows are silently dropped, which is why some imported objectives land with zero steps (e.g. BQ-35775 → "Network Tariff Code Search Screen Functionality", 0 steps, Web UI). (2) When the LLM picks `api` for a top-level fragment, the importer sets `TargetType = API_REST` but never authors the `ApiTestDefinition` body — the QA opens an empty ApiStep with no method, endpoint, or assertions. Both gaps share a root cause: the importer's persistence step (`MapRowsToObjective`) only ever creates *empty skeletons*, never authored steps. This REQ closes that gap by reusing the existing `/add-api-step` / `/add-db-assert` / `/add-event-assert` scaffolding skills inside the importer so a freshly-imported objective is closer to runnable.
area: webapi + orchestrator
related: REQ-017 (Xray import — base feature), REQ-018 (per-card controls — the other half of post-import polish)
---

# REQ-019 — Xray importer authors post-steps + API bodies at import time

## Goal

After REQ-019 ships, an imported objective for a ticket like *BQ-35775 — Network Tariff Code Search Screen Functionality* should arrive with:

1. **A parent Web UI step** (placeholder for recording) — same as today.
2. **A DB Assert post-step** attached to that parent, with the Xray "verify the data shows in the search screen" fragment as its Description and a target/payload of `Db_SqlServer` / empty `DbCheckStepDefinition` — ready for the QA to fill in table + column assertions via the existing editor.
3. **An Event Assert post-step** attached to that parent when the Xray fragment mentions a Service Bus / event-driven outcome — same shape, with `Target = Event_AzureServiceBus` and an empty `EventAssertStepDefinition`.
4. **An authored API post-step** when the Xray fragment is "and then call `GET /api/...` and assert 200" — populated with method, endpoint, and assertions via the same LLM authoring pipeline `/add-api-step` already uses.
5. **An authored top-level API step** when the LLM picks `api` for a top-level fragment (not a post-step). Today this is set to `TargetType = API_REST` and stops there. Same as #4 — authored via the existing scaffolding service.

In other words, the LLM's mapping decisions should *actually persist* — not get dropped or stubbed-out to a content-free shell.

## Why now

### Concrete defect — `postStep` rows are silently dropped

`src/AiTestCrew.WebApi/Services/XrayImportService.cs:280-319` (`MapRowsToObjective`):

```csharp
switch (row.Kind)
{
    case "webUi":
    case "placeholder": /* adds WebUiTestDefinition */ break;
    case "api":         /* sets TargetType, never authors body */ break;
    case "desktopUi":   /* sets TargetType */ break;
    case "asexml":      /* sets TargetType */ break;
    case "asexmlDelivery": /* sets TargetType */ break;
    default: break;          // ← postStep, unsupported, and unknowns all fall through
}
```

The LLM is explicitly told in `MapFragmentsAsync` (line 246) that `postStep` is one of the valid kinds, and the mapping row carries a `PostStepType` field (`src/AiTestCrew.WebApi/Services/XrayImportModels.cs:16` — `"// DB Assert | Event Assert | API post-step | ..."`). But when the LLM chooses it, the resulting row hits `default: break` and vanishes.

This is the direct cause of "0 steps, Web UI" objectives showing up in the test set after import — the LLM looked at a fragment like "verify the row appears in the Network Tariff search results grid", reasoned correctly that it's a post-step (most likely a DB Assert + UI assertion against a parent search action), returned `kind: postStep, postStepType: dbAssert`, and the importer threw it away.

### Concrete defect — `api` rows arrive as empty TargetType shells

Same file, same method:

```csharp
case "api":
    obj.TargetType = row.Target ?? "API_REST";
    break;
```

No `ApiTestDefinition` is created. The objective has `TargetType = API_REST` but no `ApiSteps[]` entry, so the test case row in the editor shows "0 steps" and no method/endpoint to edit. The QA either opens the editor and types in the entire API call manually (using the Xray fragment as a mental brief), or runs `/add-api-step` afterwards — both of which defeat the point of importing in the first place.

### Why the scaffolding already exists

The repository already has a working "NL fragment → authored step" pipeline:

| Scaffolding skill | Service | What it does |
|---|---|---|
| `/add-api-step` | `ChatIntentService` + `POST /api/api-step/dry-run` | Drafts method + endpoint + body + assertions + captures from a one-sentence NL description, validated against the OpenAPI spec where available. |
| `/add-db-assert` | `ChatIntentService` | Drafts table + SELECT + column assertions from a one-sentence NL description, using configured `DbConnections`. |
| `/add-event-assert` | `ChatIntentService` | Drafts queue / subscription + match mode + criteria + body format from a one-sentence NL description, using configured `ServiceBusConnections`. |

The importer should reuse these — not re-implement them, not call the LLM with a fresh prompt — because they already handle the awkward bits (OpenAPI lookup for API, DB connection routing, body-format sniffing for events).

## Current behaviour

Tracing BQ-35775 through the importer today:

1. `JiraXrayClient` fetches the ticket. Description has bullets like "User opens the Network Tariff search screen", "Search returns expected results", "Audit log records the search action".
2. `DecomposeAsync` produces 1 objective: "Network Tariff Code Search Screen Functionality".
3. `MapFragmentsAsync` returns mapping rows like:
   - Fragment 1: `kind: webUi, target: UI_Web_Blazor` (the user action)
   - Fragment 2: `kind: postStep, postStepType: dbAssert` (search-result verification)
   - Fragment 3: `kind: postStep, postStepType: dbAssert` (audit-log verification)
4. `MapRowsToObjective` processes them. Fragment 1 → `WebUiTestDefinition` added. Fragments 2 and 3 → `default: break`, dropped silently.
5. Persisted state: 1 WebUiStep, 0 post-steps. Test set page shows "1 step" but feels like the verifications are missing — because they are.

For an `api`-kind ticket the parallel story is: `TargetType = API_REST` set, no `ApiTestDefinition` in `ApiSteps[]`, test set page shows "0 steps".

## Desired behaviour

### Mapping pipeline changes

`MapFragmentsAsync` (the LLM mapping prompt) gets two new fields on each row:

- `parentFragmentIndex: number | null` — for `postStep` kind, the index (into the same objective's `MappingRows`) of the parent step the post-step attaches to. `null` for non-post-step kinds. The LLM is instructed to set this to the fragment that performs the user action being verified — usually the immediately-preceding `webUi` / `api` / `desktopUi` row.
- `parentKind: string | null` — the parent's expected `kind`. Used to validate the pairing (`MapRowsToObjective` rejects a `postStep` whose claimed parent doesn't exist or has a kind that can't carry post-steps).

The existing fields (`postStepType`, `sourceFragment`, `confidence`, `rationale`) stay.

The capability registry (`src/AiTestCrew.Core/Capabilities/CapabilityRegistry.cs`) gains an explicit "Pairing rules" section so the LLM knows the valid combinations:

```
Post-steps attach to a parent. Valid parent kinds: api, webUi, desktopUi, asexml, asexmlDelivery.
postStepType ∈ { dbAssert, eventAssert, apiPostStep, uiVerification }.
A postStep MUST set parentFragmentIndex to the index of its parent fragment in the same objective.
```

### Persistence changes

`MapRowsToObjective` (`XrayImportService.cs:280-319`) gets rewritten in two phases:

**Phase 1 — author parent steps (existing kinds + populated API body).**

- `webUi` / `placeholder` — unchanged (adds empty `WebUiTestDefinition`).
- `desktopUi` / `asexml` / `asexmlDelivery` — unchanged for now (still TargetType-only; out of scope for this REQ).
- `api` — call `IApiStepAuthoringService.AuthorAsync(fragment, stackKey, moduleKey, envKey, ct)` to produce a populated `ApiTestDefinition` and add it to `obj.ApiSteps`. The service is a thin wrapper around the same logic that backs `POST /api/api-step/dry-run` + `ChatIntentService`. If authoring fails (LLM unavailable, no OpenAPI spec, transient error), fall back to the current empty-shell behaviour and log a warning — the QA can still open the editor.

**Phase 2 — author post-steps and attach to parents.**

After all parent steps are materialised, iterate `postStep` rows. For each row:

1. Resolve the parent step from `parentFragmentIndex` → look up the corresponding `ApiTestDefinition` / `WebUiTestDefinition` / etc. in the just-built `obj.ApiSteps` / `obj.WebUiSteps`.
2. If the parent can't be resolved (out-of-range index, dropped-because-unsupported parent), demote the post-step to a `placeholder` WebUiStep so its description isn't lost, log a warning, and continue.
3. Dispatch on `postStepType`:
   - `dbAssert` — call `IDbCheckAuthoringService.AuthorAsync(fragment, envKey, ct)` → returns a populated `DbCheckStepDefinition` (or an empty one with `Description` set if authoring failed). Wrap in a `VerificationStep { Description = fragment, Target = "Db_SqlServer", DbCheck = <result>, WaitBeforeSeconds = 0 }`. Append to `parent.PostSteps`.
   - `eventAssert` — `IEventAssertAuthoringService.AuthorAsync` → `EventAssertStepDefinition`. `Target = "Event_AzureServiceBus"`.
   - `apiPostStep` — `IApiStepAuthoringService.AuthorAsync` → `ApiTestDefinition`. `Target = "API_REST"`. The same authoring service used for top-level API; it doesn't care whether the result lands in `ApiSteps[]` or inside a `VerificationStep.Api`.
   - `uiVerification` — wrap as `VerificationStep { Target = parent.TargetType, WebUi = new WebUiTestDefinition { Description = fragment, Steps = [] } }` (matches the current "placeholder needs recording" semantics).

4. Default `WaitBeforeSeconds = 0` — the QA tunes it in the editor if needed. (Higher defaults make sense only for aseXML delivery verifications, which already have their own field.)

### New services to extract

Three thin wrappers around existing logic. None of these introduce new LLM prompts — they delegate to `ChatIntentService` (or its constituent helpers) so the same NL→step prompt the chat assistant uses is the one the importer uses.

| Service | New / reuses | Implementation |
|---|---|---|
| `IApiStepAuthoringService` | New interface, extracts from existing `/api/api-step/dry-run` handler | Calls `ChatIntentService.DraftApiStepAsync` (already exists), returns `ApiTestDefinition`. |
| `IDbCheckAuthoringService` | New interface | Calls the same NL→DB step path the chat assistant uses for "add a DB check that verifies row X exists in table Y". |
| `IEventAssertAuthoringService` | New interface | Calls the same path the chat assistant uses for "add an event assert listening for message M on queue Q". |

DI registration in both `WebApi/Program.cs` and (for completeness) `Runner/Program.cs` — though the Runner only ever delegates to the WebApi over HTTP for import, so its registration is no-op padding.

### XrayImportPreview changes (UI surface)

The preview now shows post-steps nested under their parent in the mapping table:

```
┌─ Objective: Network Tariff Code Search Screen Functionality ────────────────┐
│ Fragment                                       Kind        Confidence       │
│ 1. User opens search screen                    webUi       0.85             │
│    └─ 2. Search returns expected results       postStep    0.80   dbAssert  │
│    └─ 3. Audit log records the search          postStep    0.75   dbAssert  │
│ 4. /api/tariffs/search returns 200             api         0.90             │
│    └─ 5. Response body has at least one row    postStep    0.85   apiPostStep │
└─────────────────────────────────────────────────────────────────────────────┘
```

The UI changes for this are inside `ImportFromXrayDialog.tsx` (already styled to the inline-CSSProperties convention after the REQ-017 follow-up commit `7b81eee`). REQ-018 is rebuilding the per-card UI anyway — implement the nested post-step rows as part of the same UI pass so the dialog only needs one rewrite, not two.

## Files to touch

| File | Why |
|---|---|
| `src/AiTestCrew.WebApi/Services/XrayImportModels.cs` | Add `ParentFragmentIndex` and `ParentKind` to `XrayMappingRow`. |
| `src/AiTestCrew.WebApi/Services/XrayImportService.cs` | Rewrite `MapFragmentsAsync` prompt to ask for the new fields. Rewrite `MapRowsToObjective` to do the two-phase parent-then-post-step build. Inject the three new authoring services. |
| `src/AiTestCrew.Core/Capabilities/CapabilityRegistry.cs` | Add the "Pairing rules" section to the markdown. |
| `src/AiTestCrew.WebApi/Services/ApiStepAuthoringService.cs` (new) | Wraps `ChatIntentService.DraftApiStepAsync` behind a clean interface. |
| `src/AiTestCrew.WebApi/Services/DbCheckAuthoringService.cs` (new) | Same shape for DB Assert. |
| `src/AiTestCrew.WebApi/Services/EventAssertAuthoringService.cs` (new) | Same shape for Event Assert. |
| `src/AiTestCrew.WebApi/Program.cs` | DI registration of the three authoring services. |
| `src/AiTestCrew.WebApi/Endpoints/ApiStepEndpoints.cs` *(if it exists)* / wherever `/api/api-step/dry-run` lives | Refactor to call `IApiStepAuthoringService` (no behavioural change — just so the importer and the existing endpoint share the path). |
| `ui/src/components/ImportFromXrayDialog.tsx` | Render post-steps as nested rows under their parent in the preview mapping table. Coordinate with REQ-018's per-card rewrite — one UI pass, not two. |
| `ui/src/api/xray.ts` | Type updates for the new `ParentFragmentIndex` / `ParentKind` / nested-row shape. |
| `docs/functional.md` | "What gets imported" section: update the mapping enum description; add a "post-step authoring" subsection. |
| `docs/architecture.md` | Jira Xray Import data-flow diagram: add the two-phase build and the authoring-service indirection. |
| `CLAUDE.md` | Add `IApiStepAuthoringService` etc. to the extension map (and the file map for the new services). |

## Acceptance criteria

1. A `postStep` mapping row with `postStepType = "dbAssert"` is persisted as a `VerificationStep` on the parent step's `PostSteps` list with `Target = "Db_SqlServer"` and a populated-or-empty `DbCheckStepDefinition`. The objective is NOT silently emptied.
2. A `postStep` row with `postStepType = "eventAssert"` is persisted with `Target = "Event_AzureServiceBus"` and `EventAssert` populated.
3. A `postStep` row with `postStepType = "apiPostStep"` is persisted with `Target = "API_REST"` and `Api` populated. The body is authored via the same path as `/api/api-step/dry-run`.
4. A `postStep` row with `postStepType = "uiVerification"` is persisted with `Target = <parent's TargetType>` and an empty WebUi/Desktop placeholder (matches today's "needs recording" semantics for parent steps).
5. A top-level `api` row no longer arrives as `TargetType = API_REST` with empty `ApiSteps[]`; it has a populated `ApiTestDefinition` (method, endpoint, headers, body, baseline assertions).
6. When the parent step a `postStep` row points to doesn't exist (LLM hallucinated index, parent kind unsupported), the post-step is demoted to a placeholder `WebUiTestDefinition` so its description survives, and a warning is logged. The import does NOT fail.
7. When authoring fails for transient reasons (LLM unavailable, OpenAPI spec missing, DB/Service Bus connection unconfigured), the post-step is still created — just with an empty payload + the fragment as Description. The QA opens the editor and fills it in. The import does NOT fail.
8. Re-importing the same ticket replaces post-steps idempotently (the existing `Clear()` in `ConfirmAsync` already covers parent steps; this REQ extends the same idempotency to post-steps by clearing each parent's `PostSteps` list before re-attaching).
9. The preview dialog renders post-steps nested under their parent in the mapping table — the QA can see at a glance which fragment is a verification of which.
10. `docs/functional.md` "Importing from Jira Xray" section reflects the new behaviour: list of post-step kinds the importer auto-authors, the "needs filling" indicator on partial-authoring outcomes, and the new pairing semantics.

## Scope — what's out

- **No live DB schema introspection from the importer.** The authoring services use whatever metadata is available (configured `DbConnections`, OpenAPI spec, capability registry); they do not query Atlassian for additional context or sniff real schemas. The QA fills in concrete table/column names.
- **No live Service Bus introspection.** Authoring an `eventAssert` uses configured `ServiceBusConnections` to validate the connection key, but does NOT peek messages to infer body shape — the QA picks the queue/subscription and body format manually.
- **No retro-fitting of REQ-017-era imports.** Objectives imported before REQ-019 ships keep their existing (possibly-empty) structure; they don't get retroactively populated. A QA who wants the new behaviour re-imports the ticket.
- **No new step types.** This REQ only authors the existing step + post-step kinds. Adding new post-step kinds (PDF inspection, file diff, etc.) is gap-REQ territory.
- **No editor changes for post-steps.** The DB Assert / Event Assert / API post-step editor dialogs already exist (`EditDbCheckStepDialog`, `EditEventAssertStepDialog`, `EditTestCaseDialog`); they render whatever the importer creates. This REQ does not extend them.
- **No agent-side dependency.** Same as REQ-017 — the importer + authoring services run in the WebApi only. Agents don't need Jira creds or OpenAPI specs to play back imported tests.
- **No two-way write-back of authored steps to Xray.** Same out-of-scope as REQ-017.
- **No splitting of a fragment into multiple post-steps.** Each Xray fragment → at most one post-step (or one parent step). Compound fragments stay compound; the QA splits in the editor if needed.

## Risks / notes

- **The LLM may fail to set `parentFragmentIndex` correctly.** Mitigations: (a) the index is validated against the materialised parent list and a hallucinated index demotes to placeholder rather than crashing; (b) the prompt includes an example pairing; (c) acceptance criterion #6 explicitly covers this path.
- **The authoring services need to be cheap to call.** Each one is one extra LLM round-trip per fragment, and a multi-objective ticket could mean ~10-20 LLM calls per import. Today's importer does 2 LLM calls (decompose + map). REQ-019 makes it 2 + N where N is the number of authorable fragments. For typical tickets (~5 fragments) this is fine; for outlier tickets with 20+ fragments it could push imports past 30s. Acceptable given that the user is already on a preview-then-confirm flow.
- **Idempotency on re-import** must clear `PostSteps` per parent before re-attaching, not just clear parent step lists. AC #8 is the explicit guard.
- **Shared authoring services + `ChatIntentService` divergence.** If `ChatIntentService` changes signature, the three authoring wrappers ripple. Mitigation: keep them as thin pass-throughs, not as a place to add new logic.
- **REQ-018 + REQ-019 are interlocked on the dialog.** Both touch `ImportFromXrayDialog.tsx`. If REQ-018 ships first and REQ-019 reworks the preview table, the per-card controls (REQ-018) need to render alongside the nested post-step rows (REQ-019). Recommend implementing them together, in one feature-coordinator pass, with a combined UI pass at the end. Mentioned in the front-matter `related:` field.

## How this lands with REQ-018

REQ-018 (per-card accept/merge/rename) and REQ-019 (post-step authoring) both touch `ImportFromXrayDialog.tsx` and `XrayImportService.cs`. Implementing them in a single feature-coordinator pass makes sense:

- One planner survey of the dialog + import service.
- One implementer pass that builds the per-card controls **and** the nested post-step rows in the preview, in one cohesive UI pass.
- One doc-writer + reviewer pass that checks both ACs against the same code.

If implemented separately, the second one will redo most of the first one's UI work.
