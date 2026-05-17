---
id: REQ-017
title: Import test cases from Jira Xray and auto-write capability-gap requirements
status: Proposed
created: 2026-05-17
author: Kalhara Samarasinghe
author-note: QAs already maintain test cases in Jira Xray. Today they hand-translate each Xray ticket into an AITestCrew objective by typing the steps into the chat assistant or the editor. That work is duplicate, brittle, and silently drops detail (preconditions, expected results, data tables). We want the QA to paste an Xray ticket key and get back a scaffolded `TestObjective` ready to record/extend. **Critically, the import has to handle *both* shapes of Xray test the team actually writes**: (a) structured tests with a `Steps[]` array (Manual / Cucumber / Generic test types), and (b) description-driven tests where the entire test body — preconditions, data, expected outcomes — lives as free-form prose in the Jira Description field with no structured steps at all. The second shape is common: e.g. a "soft-delete Site without NMI" test that lists six expected outcomes (UI behaviour, DB `IsDeleted` flag, audit-log columns, permission-gating, UI-element absence) as bullets under "Expected Outcome". The importer must parse that prose into separate AITestCrew steps, not collapse it into one placeholder. And when an Xray step describes something AITestCrew genuinely cannot do, the system should write a stub requirement file the QA can hand to tooling — instead of silently scaffolding a placeholder that no one knows is broken.
area: orchestrator + ui + tooling
---

# REQ-017 — Import test cases from Jira Xray and auto-write capability-gap requirements

## Goal

Give a QA two new things:

1. **Import an Xray test case by key.** Provide `PROJ-1234` (and a target module + test set) and AITestCrew:
   - Fetches the Xray test from Jira via REST.
   - Detects whether the test is **step-structured** (Xray `Steps[]` populated) or **description-driven** (test body lives in the free-form `Description` field with sections like Preconditions / Test Data / Expected Outcome). Both shapes are first-class inputs — the team uses each.
   - **Proposes a decomposition into 1..N AITestCrew `TestObjective`s.** Xray test cases are authored for human execution; a single manual ticket frequently bundles several semantically-distinct scenarios (e.g. happy-path delete + permission-blocked-user attempt + UI-element-absence check when a guard condition differs) because a human tester reads through them sequentially. Automation benefits from splitting these into independent objectives — they can fail and retry in isolation, run in parallel, and produce clearer reports. The importer's first pass identifies whether the ticket is **cohesive** (one objective) or **multi-scenario** (N objectives) and presents the proposal in the preview before any mapping work happens. The QA either accepts the split, merges two proposals back into one, or collapses everything into a single objective.
   - Maps the Xray content **per proposed objective** to one or more AITestCrew steps (`ApiSteps`, `WebUiSteps`, `DesktopUiSteps`, `AseXmlSteps`, `AseXmlDeliverySteps`, DB Assert post-step, Event Assert post-step) using the same LLM-driven scaffolding the chat assistant already runs. For description-driven tests, **each "Expected Outcome" bullet typically becomes its own AITestCrew step** (UI placeholder, DB Assert, negative-permission check, UI-element-absence check, etc.) within the objective it was assigned to — not a single placeholder containing the whole description, and not stuffed into a single objective when the bullets describe independent scenarios.
   - Persists one or more new `TestObjective`s in the chosen module/test set, all with `Source = "ImportedFromXray"` and the same back-link to the ticket. Each mapped step carries the originating Xray fragment (a step line, an Expected Outcome bullet, or a Precondition) as its description so the QA knows what they're filling in when they record/extend it.
   - Returns a per-objective mapping report: *Objective A "happy-path soft-delete" → 4 steps (1 UI placeholder + 3 DB Asserts, confidence 0.85)*; *Objective B "permission-blocked attempt" → 2 steps (UI placeholder under a different user, confidence 0.6)*; *Objective C "no delete option when NMI present" → 1 step (UNSUPPORTED, see REQ-XXX — negative UI assertion missing)*.

2. **Auto-write a capability-gap REQ when a step can't be mapped.** If the LLM (or a rules check) flags an Xray step as outside AITestCrew's current capabilities — e.g. "verify PDF watermark text", "compare two Excel files cell-by-cell", "drive an SAP GUI window" — the import drops a stub `requirements/REQ-XXX-<slug>.md` in the working copy quoting the Xray ticket and step text as the source of need. The QA hands that file to the tooling team. No code change, no commit, no PR — just a populated stub on disk.

The point isn't to fully automate Xray → runnable test. The point is to **eliminate the typing step** and **make capability gaps visible the moment they're hit**, instead of three days later when a tester gives up and writes a manual case.

## Why now

- QA team already maintains test cases in Xray as the system of record. AITestCrew duplicates that content into its own JSON test sets. Today the translation is manual and lossy — preconditions, datasets, and "Expected Result" fields get summarised into a single chat-assistant prompt.
- The chat assistant + `/add-api-step` / `/add-db-assert` / `/add-event-assert` skills already do most of the mechanical work (NL → structured step). Xray just needs to be plumbed in as another input source. The hard part — turning English into typed steps — is solved.
- Capability gaps are currently invisible. A QA who hits "verify PDF" today either fakes it with an `assert-text` against the surrounding page, skips the step with a TODO comment, or escalates verbally. None of those produce a tracked piece of work for the tooling team. Auto-writing a REQ stub at the point of pain is the cheapest possible feedback loop.
- The existing five-agent pipeline (`docs/agentic-development-team.md`) takes a `requirements/REQ-*.md` file and turns it into a feature branch. Closing the loop — Xray ticket → REQ stub → agentic implementation → recordable AITestCrew objective — turns a multi-day human-coordinated task into one that runs largely on its own.

## Current-state findings (audit — do NOT redo these)

✅ **Already in place — extend, don't rebuild:**

| Component | Path | Status |
|---|---|---|
| `TestObjective` carrier | `src/AiTestCrew.Agents/Persistence/TestObjective.cs` | Wraps `ApiSteps`, `WebUiSteps`, `DesktopUiSteps`, `AseXmlSteps`, `AseXmlDeliverySteps`. `Source` field already exists with values `"Generated"` / `"Recorded"` — needs a new `"ImportedFromXray"` value (additive, lenient deserialisation handles old data). |
| Chat NL → step scaffolding | `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` | Already maps NL descriptions to `apiAssertions`, `captures`, `dbAssertions`, `eventCriteria`, etc. The Xray importer reuses this service rather than duplicating prompt-engineering work. |
| `/add-api-step` / `/add-db-assert` / `/add-event-assert` skills | `.claude/agents/add-*.md` | Pattern for "scaffold a typed step from a single English sentence under a chosen parent". The importer iterates these patterns per Xray step. |
| LLM proxy (so agents don't need API keys) | REQ-011 already shipped | The importer runs server-side in WebApi, so it uses the same in-process LLM the chat assistant uses; agent boxes don't need Jira creds either. |
| Editor dialogs | `ui/src/components/EditTestCaseDialog.tsx` + `EditWebUiTestCaseDialog.tsx` + `EditDbCheckStepDialog.tsx` + `EditEventAssertStepDialog.tsx` | Already render any `TestObjective`. An imported objective shows up unchanged — no UI work to *display* an Xray-imported objective. |
| Recording flow | `--record --module <m> --testset <ts> --case-name "<name>" --target <UI_*>` | A placeholder `WebUiStep` (or `DesktopUiStep`) from the import lands as an empty case the user records against. No new recorder code needed. |
| `requirements/` directory + REQ template | `requirements/REQ-NNN-*.md` (REQ-001..REQ-016) | All existing REQs share a frontmatter + section structure. The gap-REQ generator emits a file matching the same shape so it's hand-editable. |

❌ **Genuinely missing — this REQ adds:**

- A `JiraXrayClient` that talks to the Xray Cloud REST API (or Xray Server, see §3 below).
- A `XrayImportService` in WebApi that orchestrates `fetch ticket → call LLM mapper → build TestObjective → write gap REQs`.
- A `CapabilityRegistry` — a structured catalog of "what AITestCrew can do" that the LLM mapper consults. Today this knowledge is scattered across `CLAUDE.md`'s "Where to extend" table, the `ApiAssertionSource` enum, `AssertionOperator` enum, `MatchMode` enum, etc. The registry is the single document the LLM is given as ground truth so its "supported / unsupported" verdict is consistent.
- New endpoint `POST /api/xray/import` (preview) and `POST /api/xray/import/confirm` (apply).
- New CLI flag `--import-xray <ticketKey>` on the Runner.
- A new "Import from Xray" button on the test-set page, opening a dialog that calls the preview endpoint and shows the per-step mapping report before the QA hits Confirm.

## Scope — what's in

### 1. Capability Registry (the foundation)

A new file `src/AiTestCrew.Core/Capabilities/CapabilityRegistry.cs` (or JSON resource) enumerating, in a single LLM-readable document, what AITestCrew supports today. Categories:

- **Top-level step types**: `ApiStep`, `WebUiStep` (Bravo Web, Brave Cloud), `DesktopUiStep` (WinForms), `AseXmlStep`, `AseXmlDeliveryStep`.
- **Post-step types**: API post-step, DB Assert post-step, Event Assert post-step, aseXML post-delivery verification.
- **Assertion primitives per category** (sourced from the enums that already exist — `ApiAssertionSource`, `AssertionOperator`, `MatchMode`, desktop assertion actions). The registry is generated from those enums + hand-written one-liners, not duplicated.
- **What is *not* supported today**, with hints — e.g. "PDF content inspection (no primitive; would need a new agent type)", "Excel file diff (not supported)", "image comparison beyond exact pixel sampling", and (likely) "negative UI assertions — *element should not be visible* / *button should be absent*" if a survey of the Web + Desktop agents confirms this is missing. The importer in §3a relies on this entry to decide whether outcomes like "no delete option in UI" are mappable or gap-REQ-worthy; getting the registry honest about negative assertions is part of this REQ's scope.

The registry is loaded once at WebApi startup and exposed via `GET /api/capabilities` for both the UI (Xray import dialog) and the LLM mapper (system-prompt context).

This is **not** a marketing document. It is the LLM's ground truth — its job is to look at an Xray step and decide which entry in the registry covers it. If nothing covers it, the step is marked unsupported and a gap REQ is generated.

### 2. Jira Xray client

`src/AiTestCrew.Integrations.JiraXray/JiraXrayClient.cs` — a typed REST client.

**Auth model** (config-only via `appsettings.json → JiraXray`):

```json
"JiraXray": {
  "Mode": "Cloud",
  "BaseUrl": "https://yourtenant.atlassian.net",
  "XrayClientId": "...",
  "XrayClientSecret": "...",
  "JiraEmail": "tooling@yourdomain",
  "JiraApiToken": "..."
}
```

Two flavours behind one interface:
- **Xray Cloud**: token exchange against `https://xray.cloud.getxray.app/api/v2/authenticate` → JWT → call `GET /api/v2/test/<key>` for steps.
- **Xray Server / DC**: Basic auth (Jira API token) against `/rest/raven/2.0/api/test/<key>`.

The mode is picked from config; both expose the same `IJiraXrayClient.GetTestAsync(string ticketKey, CancellationToken ct)` returning a normalised `XrayTestDto`:

```
XrayTestDto {
  Key, Summary, Description (raw markup/ADF — full body),
  Labels[], TestType ("Manual"|"Cucumber"|"Generic"),
  Steps: [ XrayStep { Index, Action, Data, ExpectedResult } ]  // may be EMPTY for description-driven tests,
  CucumberScenario?: string,
  GenericDefinition?: string,
  // Parsed from Description when Steps is empty (see §3a):
  ParsedDescription?: {
    Preconditions[]: string,        // bullet list
    TestData?: string,
    ExpectedOutcomes[]: string,     // bullet list — each typically maps to one AITestCrew step
    OtherSections: { sectionName: string => body: string }  // anything not in the known set
  }
}
```

Jira's Cloud REST returns the description in Atlassian Document Format (ADF); Xray Server returns wiki markup. The client normalises both to plain text + a section-tagged bullet structure via a small Markdown/ADF parser. Section names recognised by exact-match (case-insensitive) **and** common variants:

- `Description` / `Summary` (free-form lead-in)
- `Preconditions` / `Pre-conditions` / `Setup`
- `Test Data` / `Data` / `Inputs`
- `Expected Outcome` / `Expected Outcomes` / `Expected Result` / `Expected Results` / `Acceptance Criteria` / `Verifications`
- Anything else falls into `OtherSections` verbatim and is included in the LLM prompt as context.

If both `Steps[]` is populated AND `Description` contains an Expected-Outcome section, the importer uses **both**: Steps become the action sequence, Expected Outcomes become additional assertions/verifications post-step. The LLM is told which is which.

Errors map cleanly: 404 → `XrayTicketNotFoundException`, 401 → `XrayAuthException`, 5xx → `XrayUpstreamException`. The importer surfaces these as actionable error toasts on the UI dialog.

The client lives in a new project `AiTestCrew.Integrations.JiraXray` so its `Atlassian.SDK` (or HTTP-only) dependency doesn't leak into `Core`. Dependency direction: `WebApi → Integrations.JiraXray → Core`. No project references upward.

### 3. Xray Import Service (the orchestrator)

`src/AiTestCrew.WebApi/Services/XrayImportService.cs`:

```
public async Task<XrayImportPreview> PreviewAsync(string ticketKey, string moduleId, string testSetId, CancellationToken ct);
public async Task<XrayImportResult>  ConfirmAsync(XrayImportPreview preview, CancellationToken ct);
```

`PreviewAsync` does **everything except persist**:

1. Call `IJiraXrayClient.GetTestAsync(ticketKey)`.
2. Pass the steps + capability registry to the LLM with a prompt that:
   - Tells it the available step types and their carriers.
   - Asks it to emit, per Xray step, one of:
     - `{ "kind": "api"|"webUi"|"desktopUi"|"asexml"|"asexmlDelivery", "definition": { ... }, "confidence": 0.0-1.0, "rationale": "..." }`
     - `{ "kind": "postStep", "parent": <index>, "type": "db"|"event"|"api", "definition": { ... }, "confidence": ..., "rationale": "..." }`
     - `{ "kind": "placeholder", "target": "UI_Web_Blazor"|"UI_Web_MVC"|"UI_Desktop_WinForms", "description": "...", "confidence": ..., "rationale": "..." }` — for steps that are *supported in principle* but need recording (e.g. "Click the Save button on the customer form").
     - `{ "kind": "unsupported", "description": "...", "rationale": "...", "suggestedReqTitle": "...", "suggestedExtensionPoint": "..." }` — for steps that genuinely have no path forward in AITestCrew today.
3. Build (in memory, do not persist) a draft `TestObjective` from the supported entries.
4. Build (in memory, do not write to disk) a list of draft REQ stubs from the `unsupported` entries.
5. Return both, plus a per-step mapping table, as `XrayImportPreview`.

`ConfirmAsync`:

1. Re-validate the preview against the current state of the module/test set (concurrency guard — see REQ-008 pattern).
2. **Persist the `TestObjective`** to `modules/{moduleId}/{testSetId}.json` via the existing test-set repository. Set `TestObjective.Source = "ImportedFromXray"` and `TestObjective.XrayTicketKey = "PROJ-1234"`.
3. **Write the gap REQ stubs** to `requirements/REQ-XXX-<slug>.md` on the working copy. Numbering: scan `requirements/` for the highest existing REQ number and increment per stub. The stubs are *not* committed and *not* PR'd — just written to disk for the QA to review. (Optional: print the file path so the QA can `git status` and find them.)
4. Return a structured result: persisted objective id, list of gap REQ paths written, list of placeholder steps that still need recording.

Re-importing the same Xray ticket key is **idempotent**: the existing objective with matching `XrayTicketKey` is updated, not duplicated. Newly-added Xray steps appear as new steps in the objective. Removed Xray steps are flagged in the preview but **not deleted** automatically — they're listed as "this step is no longer in Xray; delete manually if intended". This conservative posture protects work the QA has already recorded against a step.

### 3a. Decomposition pass (one Xray ticket → 1..N AITestCrew objectives)

Before mapping any step, the importer runs an LLM-driven decomposition pass over the Xray ticket. The LLM is given the full ticket (Summary, Description sections, Steps if any) and asked: **"Should this map to one AITestCrew `TestObjective` or several? If several, what are they, and which Xray steps / Expected Outcomes belong to each?"**

The LLM returns a structured list:

```
ProposedObjectives: [
  {
    Slug: "happy-path-soft-delete-no-nmi",
    Title: "Authorised user soft-deletes a Site without NMI",
    Rationale: "Positive flow with cohesive setup + action + assertions",
    AssignedFragments: [<Expected Outcome 1>, <Expected Outcome 2>, <Expected Outcome 6>]
  },
  {
    Slug: "permission-blocked-soft-delete",
    Title: "User without edit permission cannot soft-delete a Site",
    Rationale: "Different actor (no-permission user) — independent failure isolation matters",
    AssignedFragments: [<Expected Outcome 4>]
  },
  ...
]
```

**Heuristics the LLM is prompted with** (these are guidance, not hard rules — the LLM applies judgment):

1. **Different actor → split.** A scenario that requires a different user (no permission, read-only, admin) is its own objective. Same login + same permission set = same objective.
2. **Different precondition data → split.** "Site without NMI" and "Site with NMI" are two starting points; they should be separate objectives even if the verifications look similar.
3. **Negative-case verification with no positive action → split.** "Verify the delete button is absent when NMI is present" is a UI-state assertion with no action sequence; it deserves its own objective rather than being grafted onto a delete flow it doesn't share.
4. **Different downstream system being verified → keep together unless the action diverges.** A scenario that touches UI then DB then audit-log columns is *one* objective — the UI action is what triggers all three verifications.
5. **Repeated structure with different data → consider parameterisation, not duplication.** If the bullets describe "the same scenario, three times, with different data", the importer notes this in the proposal but **does not auto-split** — the QA decides whether to parameterise via per-env tokens or to keep one example case and add variants by hand.

**Conservative posture.** The LLM is instructed to **prefer fewer objectives** when in doubt. Over-fragmentation creates more test-set noise than under-fragmentation, and the QA can always split later. A single Xray ticket producing more than 4 proposed objectives triggers a "review carefully" flag in the preview.

**QA override.** The preview dialog (§6) shows the proposed objectives as a list with each one expandable to see its assigned fragments. The QA can:
- **Accept all** — persist as proposed.
- **Merge two** — pick two adjacent proposals and combine their fragments into one objective. (UI: drag-and-drop or a "Merge into above" button.)
- **Collapse to one** — a top-level toggle "Import as a single objective" that overrides the decomposition entirely. Useful when the QA reads the proposal and decides the system over-fragmented.
- **Re-title** — edit the proposed title/slug per objective inline before confirming.

Splitting *more* than the LLM proposed is **out of scope for v1**. If a QA wants finer granularity, they accept the proposal and then duplicate/edit objectives via the existing test-set editor.

**Idempotency under decomposition.** When re-importing the same ticket, matching is by `(XrayTicketKey, Slug)` — not just `XrayTicketKey`. So three objectives created on import 1 remain three objectives on import 2, each updated in place. If a re-import proposes a *different* decomposition (e.g. the ticket changed in Jira and now has a new scenario), new objectives are added; objectives whose slugs no longer appear in the proposal are flagged in the preview but **not** deleted — same conservative posture as removed-step handling.

### 3b. Description-driven mapping (test body lives in the Description field)

This is the path taken when `XrayTestDto.Steps` is empty and the whole test is encoded as prose in `Description`. The mapper:

1. **Preconditions** become **comments on the persisted objective** (a new optional `TestObjective.Preconditions: List<string>` field) — they're not executable steps, but they need to survive the import so the QA sees them in the editor and so a future reviewer understands what the test assumed. Two recognised patterns also trigger automatic scope hints:
   - "Different user setups with NO Access / Read-Only / Edit permissions" → the mapper notes that the objective is a **permission-matrix test** in the import preview's notes column. The objective is created once; the QA decides whether to clone it per role or use a single objective with per-environment user credentials (existing `EnvironmentParameters` feature). The mapper does **not** auto-duplicate the objective into N variants — too aggressive without human input.
   - "X records must exist" (e.g. "Some Site records must exist without an associated NMI") → the mapper notes this as a data-setup requirement. If a matching startup data-pack exists for the target env, it references it; otherwise it suggests `/add-data-pack-script` in the preview notes.

2. **Test Data** content is attached to the objective's `Preconditions` block (or `TestData` field if non-empty), and made available to per-step `{{Token}}` substitution where the LLM can identify named values.

3. **Each "Expected Outcome" bullet is mapped independently** to one (or more) AITestCrew steps. The mapper does **not** collapse the outcomes into a single placeholder — that loses the point of having separate verifications.

#### Worked example — the user's "soft-delete Site without NMI" test

Given the Description content:

> **Preconditions:**
> - Access to the TASN Environment and Database
> - Different User setups with NO Access, Read Only (View) and Edit (Create/Update/Soft delete). Must have required permissions
> - The UI must be available
> - Some Site records must exist without an associated NMI
>
> **Test Data:** None
>
> **Expected Outcome:**
> 1. Verify authorized user (with proper permission) can soft-delete existing Site records that do not have an associated NMI and deleted Sites are flagged in the database using an IsDeleted flag i.e. IsDeleted flag is set to true (1)
> 2. Verify that soft deletion actions are captured in the database audit logs (ModifiedBy, ModifiedOn fields populated with correct details and IsDeleted flag is updated as 1)
> 3. Verify that a soft-deleted Site is no longer visible in the default search results (UI)
> 4. Verify that users without edit/delete permissions cannot perform soft deletion, even if the record has no NMI
> 5. Verify there is no delete option in the Site search UI to soft delete Site records with a NMI
> 6. Verify that soft-deleted Sites do not appear in standard search results (in UI) but can be retrieved via audit or admin queries in database

**Decomposition pass output.** The §3a heuristics fire on this ticket: outcomes 1, 2, and 6 share an actor + action + setup (authorised user, performs soft-delete, verify post-state across UI + DB + admin query) — one objective. Outcome 4 has a *different actor* (no-permission user) — split. Outcome 5 has a *different precondition* (Site WITH NMI, not without) and no positive action — split. Outcome 3 is the post-deletion search; it shares the action with outcomes 1/2/6 (the deletion that happened a moment ago), so it stays with them. The LLM proposes **three objectives**:

- **Objective A — "Authorised user soft-deletes a Site without NMI"** (outcomes 1, 2, 3, 6)
- **Objective B — "User without edit permission cannot soft-delete a Site"** (outcome 4)
- **Objective C — "No delete option in UI when the Site has an NMI"** (outcome 5)

The QA sees the three proposals in the preview dialog and accepts. The mapper then produces step skeletons per objective (target env: TASN):

**Objective A — Authorised user soft-deletes a Site without NMI**

| # | Source bullet | AITestCrew shape | Confidence | Notes |
|---|---|---|---|---|
| Pre | "Some Site records must exist without an associated NMI" | `Preconditions[]` + data-pack hint | — | Suggests `/add-data-pack-script` to seed Sites without NMI in TASN env if no matching pack exists |
| 1 | EO #1 — "Authorized user can soft-delete... IsDeleted=1" | **WebUiStep placeholder** (Bravo Web — locate Site, click Delete, capture `{{siteId}}`) + **DB Assert post-step** (`SELECT IsDeleted FROM Sites WHERE Id={{siteId}}`, assert `= 1`) | 0.85 | UI step needs recording; DB Assert auto-authored |
| 2 | EO #2 — "Audit logs: ModifiedBy, ModifiedOn populated, IsDeleted=1" | **DB Assert post-step** (`SELECT ModifiedBy, ModifiedOn, IsDeleted FROM Sites WHERE Id={{siteId}}`, assert `ModifiedBy IS NOT NULL`, `ModifiedOn within last 60s`, `IsDeleted = 1`) | 0.9 | Auto-authored |
| 3 | EO #3 — "Soft-deleted Site no longer visible in default search (UI)" | **WebUiStep placeholder** (search by the deleted Site's identifier, assert not-present) | 0.7 | Negative UI assertion — see capability check below |
| 4 | EO #6 — "Soft-deleted Sites... retrievable via audit/admin DB query" | **DB Assert post-step** (`SELECT * FROM Sites WHERE IsDeleted=1`, assert ≥ 1 row with `Id = {{siteId}}`) | 0.85 | Auto-authored |

**Objective B — User without edit permission cannot soft-delete a Site**

| # | Source bullet | AITestCrew shape | Confidence | Notes |
|---|---|---|---|---|
| Pre | "User configured with No Access / Read-Only role" | `Preconditions[]` + permission-matrix hint | — | Flagged for QA — needs a Read-Only-user env or per-env credentials override before this can run |
| 1 | EO #4 — "Users without edit/delete permissions cannot soft-delete" | **WebUiStep placeholder under a different user** (locate Site without NMI; assert delete button absent OR delete attempt is rejected) | 0.6 | Hybrid: either "button absent" (negative UI assertion) or "click → expect error toast". QA decides at record-time. |

**Objective C — No delete option in UI when the Site has an NMI**

| # | Source bullet | AITestCrew shape | Confidence | Notes |
|---|---|---|---|---|
| Pre | "Site records must exist WITH an associated NMI" | `Preconditions[]` + data-pack hint | — | Different starting data than Objective A — Sites that *have* NMIs (the production-normal case) |
| 1 | EO #5 — "No delete option in Site search UI for Sites WITH an NMI" | **WebUiStep placeholder** (open a Site with NMI, assert delete button absent) | 0.7 | Negative UI assertion — see capability check below |

If the existing Bravo Web agent does not yet support **negative UI assertions** ("element should be absent" / "button should not be visible") as a first-class assertion, the relevant steps in Objectives A and C are marked `kind: "unsupported"` and a single gap REQ is generated (deduplicated — one REQ per missing capability, even when multiple imported steps trigger it). The "Where to extend" table in `CLAUDE.md` currently lists `assert-text` / `assert-count` for desktop and assorted Web assertions, but the absence-direction is not explicit — this is exactly the kind of gap the import is designed to surface.

The QA opens each objective in the existing editor, sees its steps with one-line descriptions quoting the Xray bullets, records the UI placeholders against the TASN environment (Objective B needs the no-permission user wired up first), and the DB Asserts are already authored. The three objectives can run, fail, and retry independently — which is the whole point of the decomposition.

### 4. Capability-gap REQ stub generator

`src/AiTestCrew.WebApi/Services/GapRequirementWriter.cs`:

Given an `unsupported` entry from the LLM, emit a REQ stub matching the existing house style. Template:

```markdown
---
id: REQ-XXX
title: <suggestedReqTitle from the LLM, cleaned up>
status: Proposed
created: <today>
author: Auto-generated from Xray import
area: <suggestedExtensionPoint>
source-ticket: <ticketKey>
---

# REQ-XXX — <title>

## Goal

<one paragraph generated from the Xray step text + the LLM's rationale>

## Source of need

This requirement was auto-generated by the Xray importer (REQ-017) when importing **<ticketKey>** ("<ticket summary>") on <date>. The Xray step below was identified as outside AITestCrew's current capabilities:

> **Step <index>:** <Xray step action>
> **Data:** <Xray step data>
> **Expected result:** <Xray step expected result>

## Why now

<LLM-generated paragraph linking the gap to the broader test scenario>

## Proposed direction (sketch — to be refined by tooling team)

<LLM-suggested extension point, e.g. "New post-step type — PDF Content Assert. Mirror the DB Assert pattern: data model in `Storage/PdfAgent/`, evaluator + agent in `Agents/PdfAgent/`, NL skill `/add-pdf-assert`. JSONPath-equivalent for PDFs is unresolved — consider iText7 text extraction + regex.">

## Acceptance criteria (skeleton)

1. <empty bullets for the human to fill in>
2. <empty bullets for the human to fill in>

## Out of scope

- <LLM-suggested boundary>

---

*This stub was auto-generated. A human must review the goal, refine the proposed direction, and fill in acceptance criteria before this requirement is workable.*
```

The footer is mandatory — the stub is explicitly marked as *unfinished* so it's obvious in code review that the human owner skipped the polish step if they did.

### 5. CLI flag

`src/AiTestCrew.Runner/Program.cs` gains:

```
dotnet run --project src/AiTestCrew.Runner -- --import-xray <ticketKey> --module <m> --testset <ts>
```

The CLI path calls `XrayImportService.PreviewAsync` then immediately `ConfirmAsync` (no interactive prompt — CI-friendly). Output is a Spectre-rendered table mirroring the UI's preview dialog plus the list of REQ stub paths.

Optional `--xray-dry-run` flag returns the preview only and persists nothing — useful for CI smoke tests.

### 6. UI surface

`ui/src/pages/TestSetPage.tsx` (or wherever objectives are listed for a test set) gains an **"Import from Xray"** button next to the existing "Add objective" / "Record" buttons. Clicking it opens a new `ImportFromXrayDialog.tsx`:

1. Single text input — Xray ticket key. Validation: must match `^[A-Z][A-Z0-9_]+-\d+$`.
2. "Preview" button. Calls `POST /api/xray/import` (preview-only). Shows a spinner while the importer runs (typically 5-15 s — one LLM call).
3. Renders **the proposed decomposition first**, then the per-objective mapping. Each proposed objective is a collapsible card; clicking it expands the mapping table for that objective.

   ```
   ┌─ Proposed objectives for PROJ-1234 (3) ──────────────────────────────────┐
   │ [✓] A. Authorised user soft-deletes a Site without NMI       (4 steps)  ▼│
   │ [✓] B. User without edit permission cannot soft-delete       (1 step)   ▶│
   │ [✓] C. No delete option in UI when the Site has an NMI       (1 step)   ▶│
   │                                                                          │
   │ [ ] Import as a single objective instead                                 │
   └──────────────────────────────────────────────────────────────────────────┘
   ```

   - Each card has a checkbox (uncheck to skip that objective on confirm), an editable title, and a "Merge into above" button (disabled on the first card).
   - The "Import as a single objective" toggle at the bottom collapses all proposals into one objective using the union of their fragments. The proposal cards grey out when this is checked.
   - Expanding a card shows that objective's mapping table:

   | Xray fragment | Mapped to | Confidence | Notes |
   |---|---|---|---|
   | EO #1 — "Authorized user can soft-delete... IsDeleted=1" | WebUiStep placeholder + DB Assert | 0.85 | UI step needs recording |
   | EO #2 — "Audit logs..." | DB Assert post-step | 0.9 | Auto-authored |
   | EO #3 — "Soft-deleted Site no longer visible..." | UNSUPPORTED | n/a | Will write REQ-018 — Negative UI assertion |
   | EO #6 — "Retrievable via admin DB query" | DB Assert post-step | 0.85 | Auto-authored |

4. "Confirm" button — calls `POST /api/xray/import/confirm` with the (possibly QA-edited) decomposition + mappings, persists each accepted objective, writes the gap REQ stubs (deduplicated across objectives — one REQ per missing capability), and closes the dialog with a success toast listing what was created.

Existing chat assistant prompts (`docs/qa-assistant-curriculum.md`) get a new "Import from Xray" lesson under the Orient stage.

### 7. Persistence + schema

`TestObjective` gains five optional fields:

- `Source = "ImportedFromXray"` — new allowed value (lenient enum read; existing data unaffected).
- `XrayTicketKey?: string` — null for non-imported objectives. **Multiple objectives may share the same `XrayTicketKey`** when the decomposition pass split one ticket into several — this is by design and is the mechanism the test-set page uses to group them visually.
- `XrayObjectiveSlug?: string` — populated only when `XrayTicketKey` is set. Stable identifier for the decomposed slice (e.g. `"happy-path-soft-delete-no-nmi"`). Re-import idempotency keys off `(XrayTicketKey, XrayObjectiveSlug)`.
- `Preconditions?: List<string>` — bullet list lifted from the Xray Description's Preconditions section (also usable on non-imported objectives — there's no reason to gate this behind the importer). Surfaced as a read-only note panel in the existing edit dialog so the QA sees the assumed context when authoring/recording steps.
- `TestDataNotes?: string` — free-form text lifted from the Xray Description's "Test Data" section, if any. Same treatment as `Preconditions` in the editor.

All five fields are additive — no migration helper needed (the existing lenient deserialisation pattern handles missing fields).

The test-set page UI groups objectives sharing an `XrayTicketKey` under a single header (collapsible, with a link to the Jira ticket) so a QA can see at a glance that three objectives all originated from one Xray ticket. This grouping is purely visual; the underlying objectives are independent for run/edit/delete purposes.

The `--rebaseline` flow is **not** allowed for `ImportedFromXray` objectives. Rebaseline already only runs for `Source = "Generated"`. Imported objectives are treated like recorded ones — the source of truth is Xray + the user's recording, not the LLM that mapped them.

## Scope — what's out

- **No two-way sync.** AITestCrew does not push runs back to Xray as test executions (yet). Read-only import only. A future REQ can add the execution-write-back side once the import side is steady.
- **No Xray webhook integration.** Imports are user-triggered (CLI / UI button / chat). Auto-detecting "ticket changed in Jira → re-import here" is out of scope. Re-importing is a one-button action; that's good enough for v1.
- **No support for Xray test sets / test plans / test executions.** Only individual test cases (`Test` issue type). Bulk import by JQL is a future step.
- **No auto-commit / auto-PR for gap REQ stubs.** The stubs land in the working copy. The QA reviews them and decides whether to commit. (Auto-committing pre-approved stubs to a branch is plausible future work but breaks the "humans own shared state" rule today.)
- **No mapping for AI-generated objectives.** If the QA wants AI to dream up a test from scratch, the chat assistant already covers that. Xray import specifically operates on a real ticket.
- **No customer-environment selection in the import flow.** The imported objective inherits the test set's existing default environment + allowed-environments list. Per-env parameters are filled in by the QA after import using the existing editor.
- **No retro-fitting old test sets.** Existing AITestCrew objectives that *happen* to correspond to an Xray ticket do not get the `XrayTicketKey` field back-filled. Future work; not blocking.
- **No re-running of the LLM mapper at confirm time.** The mapping computed by `PreviewAsync` is the mapping that gets persisted. If the QA wants a different mapping, they re-run Preview.
- **No finer-grained decomposition than the LLM proposes.** v1 supports the QA *merging* proposed objectives or *collapsing* everything into one, but not *splitting* a proposed objective into more. If a QA wants finer granularity than the LLM offered, they accept the proposal as-is and then duplicate/edit objectives via the existing test-set editor. Adding in-dialog splitting is reasonable future work once we see whether the LLM consistently under-fragments.

## Acceptance criteria

1. Given a valid Xray Cloud configuration in `appsettings.json` and a ticket `PROJ-1234` whose test type is `Manual` with three steps, calling `POST /api/xray/import { ticketKey: "PROJ-1234", moduleId: "billing", testSetId: "invoicing" }` returns a preview within 30 seconds. The preview contains exactly three mapping rows, each with a `kind`, a `confidence`, and either a `definition` or a `rationale` for placeholder/unsupported.
2. `POST /api/xray/import/confirm` on that preview persists a new `TestObjective` in `modules/billing/invoicing.json` with `Source = "ImportedFromXray"` and `XrayTicketKey = "PROJ-1234"`. The objective contains the mapped steps in the right carriers (e.g. an API step in `ApiSteps`, a DB Assert post-step under the right parent).
3. If one of the three steps was `kind: "unsupported"`, the confirm call writes `requirements/REQ-<next>-<slug>.md` to the working copy. The file matches the existing REQ frontmatter + section structure and contains the Xray step text under "Source of need" and the LLM's suggested extension point. The file is **not** committed.
4. Re-running the same import overwrites the existing objective (matched by `XrayTicketKey`) rather than creating a duplicate. The `Modules` page never shows two objectives for the same Xray ticket.
5. Running `dotnet run --project src/AiTestCrew.Runner -- --import-xray PROJ-1234 --module billing --testset invoicing` performs the same import non-interactively and prints (a) the path of the persisted objective, (b) the list of gap REQ paths written.
6. With `--xray-dry-run`, the CLI prints the preview table without persisting and without writing any REQ stubs. `git status` is clean afterward.
7. Calling `GET /api/capabilities` returns the capability registry as JSON. The registry covers every existing top-level step type, every existing post-step type, and every assertion operator currently shipped. Adding a new step type elsewhere in the codebase forces a registry update via a unit test that asserts every `TestTargetType` enum value appears in the registry.
8. The UI dialog renders the mapping table from §6, confidence values, and a "Confirm" button. Confirming shows a toast listing the persisted objective + the gap REQs written. Errors (auth failure, ticket not found, LLM timeout) render as inline error states with a retry button.
9. An imported objective shows up in the existing editor dialogs unchanged. `--rebaseline` refuses to run on it with a clear error: "Imported objectives cannot be rebaselined — re-import from Xray instead."
10. No new react-query keys beyond `xrayPreview` and `capabilities`. No new background polling. No agent-side dependency on Jira — the import runs server-side in WebApi only; agent boxes never see Xray credentials.
11. **Description-driven import works end-to-end.** Given an Xray ticket with empty `Steps[]` whose Description matches the structure in §3b's worked example ("soft-delete Site without NMI"), the preview returns: a `Preconditions[]` list quoting the precondition bullets per objective, a permission-matrix note flagged for QA decision on Objective B, a data-setup note suggesting `/add-data-pack-script` per objective if no matching pack covers TASN, and **one mapping row per Expected Outcome bullet** (not a single placeholder swallowing all six). At least the three DB-assert outcomes (1's IsDeleted check, 2's audit-log check, 6's admin-query check) are auto-authored as DB Assert post-steps within Objective A. The two negative-UI outcomes (3, 5) are either authored as supported assertion shapes OR flagged `unsupported` with **a single deduplicated REQ stub** written for the missing negative-assertion primitive — never silently scaffolded as plain placeholders, and never duplicated as two separate REQs.
12. **Preconditions and Test Data round-trip.** After confirm, each persisted `TestObjective` has populated `Preconditions[]` and (where applicable) `TestDataNotes` for the fragments assigned to it, and the existing editor dialog renders both as a read-only note panel above the step list. Re-importing does not duplicate precondition entries.
13. **Decomposition into multiple objectives.** Given the soft-delete-Site-without-NMI ticket in §3b, the preview proposes **3 objectives** (A: happy-path delete with verifications, B: permission-blocked attempt, C: no-delete-when-NMI-present), shown as collapsible cards. Accepting the proposal persists three `TestObjective`s sharing the same `XrayTicketKey` but with distinct slugs. Re-importing the same ticket updates all three in place (matched by `(XrayTicketKey, Slug)`), does not duplicate them. Checking "Import as a single objective" instead persists one objective containing the union of all six Expected Outcomes as steps. Unchecking any proposal card and confirming skips that objective entirely (no orphan persisted, no gap REQ written for skipped fragments).
14. **Decomposition is conservative.** A simple Xray ticket with one actor, one action, and one verification (e.g. "POST /customers with valid payload, verify 201") produces exactly **one** proposed objective — never multiple. A ticket producing more than 4 proposed objectives carries a "review carefully" flag in the preview header, prompting the QA to consider whether the system over-fragmented.

## Out of scope (future work)

- **Bulk import via JQL** — "import all tickets in this Xray test set". Trivial extension once single-ticket import is steady.
- **Write-back of execution results to Xray** — closes the loop with QA tooling. Separate REQ; needs decisions about how AITestCrew run IDs map to Xray test execution issues.
- **Auto-running the agentic development team on generated gap REQs.** Today the QA reads the stub and hands it to a human (or `feature-coordinator`). Auto-triggering the pipeline is plausible once the stubs are reliable enough to trust, but premature now.
- **Mapping non-test Xray issues** (User Story → AITestCrew objective). The import path is specific to `Test` issue types.
- **TestRail / Zephyr / qTest connectors.** Same shape, different upstream. Build them on top of the same `XrayImportService` / `CapabilityRegistry` interfaces once those are stable.
- **A "what changed in Xray since last import?" diff view.** Useful but separable. The conservative re-import policy in §3 already prevents work loss; a diff view is a quality-of-life improvement on top.
