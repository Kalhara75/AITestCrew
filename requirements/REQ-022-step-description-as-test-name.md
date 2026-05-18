---
id: REQ-022
title: Step description renders as the "Test Name" in test-case inner tables, with the objective name as a fallback
status: Proposed
created: 2026-05-18
author: Kalhara Samarasinghe
author-note: Today the inner "Test Name" column under a Test Case echoes the parent objective's name for every step row, because all five inner-table components render `tc.objectiveName` rather than the step's own `Description`. Single-step objectives look fine (they collapse to a clean 1:1 display), but multi-step objectives show N identical rows, and renaming the objective via the Edit dialog renames both the outer "Test Case" cell and every inner "Test Name" cell simultaneously — which is confusing because the user thought they were editing two different things. `WebUiTestDefinition.Description` and its peers already exist on the persistence model (`WebUiTestDefinition.cs:12`, `DesktopUiTestDefinition.cs:12`, `WebUiTestCase.cs:15`, `DesktopUiTestCase.cs:15`) and are already editable in `EditWebUiTestCaseDialog.tsx:139-141`. The aseXML tables already render a "objective name + description subtitle when different" pattern (`AseXmlDeliveryTestCaseTable.tsx:51-53`, `AseXmlTestCaseTable.tsx:51-53`) but it's only on two of the five surfaces. This REQ unifies the rendering: a single rule — "Test Name" cell shows `step.description` when non-empty, otherwise falls back to `objective.name` — applied consistently to all five inner tables.
area: ui
related: REQ-020 (record-into-imported-placeholder — the multi-step shape this exposes), REQ-017 (Xray import — produces objectives whose Description and Name may diverge)
---

# REQ-022 — Step description renders as the "Test Name" in test-case inner tables

## Goal

After REQ-022 ships, when a user looks at a Test Case and expands its details, the inner "Test Name" column shows each step's own `Description` field if the field is non-empty; if the field is empty the cell falls back to the parent objective's `Name`. This applies uniformly to the Web UI, Desktop UI, API, aseXML transaction, and aseXML delivery inner tables.

Concretely:

1. Single-step objective with empty `Description` → no visible change from today (cell shows objective name).
2. Single-step objective with non-empty `Description` → cell shows the description; the outer "Test Case" cell still shows the objective name.
3. Multi-step objective → each row's "Test Name" cell shows that step's own `Description` (or the objective name when blank), so the rows are individually distinguishable instead of N identical labels.
4. Editing the step's `Description` via the existing Edit dialog updates the inner cell only; editing the objective's `Name` updates the outer "Test Case" cell and any inner rows where `Description` is blank.

The persistence model is **not** changed. `Description` already exists on every step-definition type and is already editable; this REQ is a rendering change plus minor dialog parity.

## Why now

### Concrete defect — renaming "Test Name" silently renames "Test Case"

Reproduction (from the user's screenshot):

1. Test set with one objective *Network Tariff Code Search By NMI, Meter Serial and NTC* containing one Web UI step.
2. Outer table shows "Test Case: Network Tariff Code Search By NMI, Meter Serial and NTC".
3. Expand the row. Inner table shows "Test Name: Network Tariff Code Search By NMI, Meter Serial and NTC".
4. User clicks the inner pencil, edits the "Display name" field in the dialog, saves.
5. **Both** the inner "Test Name" cell and the outer "Test Case" cell change to the new value.

The user expected to rename one thing — they perceived "Test Case" and "Test Name" as two independent labels because the column headers are different words. Today they're literally the same field (`objective.name`) being rendered in two places (`TestSetDetailPage.tsx:453` and `WebUiTestCaseTable.tsx:91`).

### Concrete defect — multi-step objectives show N identical rows

Real shape: a Web UI objective with three recorded steps (e.g. "Tariff search" with sub-scenarios *Search by NMI*, *Search by Meter Serial*, *Search by NTC*) — the inner table flattens to three rows under `WebUiTestCaseTable.tsx:33-42`, all displaying the same `objectiveName`. The user cannot tell the rows apart without opening each Edit dialog. The `Description` field on each `WebUiTestDefinition` already holds the disambiguating text (when populated); it just isn't rendered.

### Why a separate per-step `Name` field is the wrong fix

Considered: add a new `Name` property to `WebUiTestDefinition` / `DesktopUiTestDefinition` / `WebUiTestCase` / `DesktopUiTestCase` / `ApiTestDefinition` / `AseXmlTestDefinition` / `AseXmlDeliveryTestDefinition`. Rejected because:

- `Description` already exists on every type and already serves this purpose.
- Adds a forced second-naming step during recording / generation, which is friction for the single-step common case.
- Migration: existing test sets have no per-step Name, so the column would render blank until users re-edit.

The `Description`-or-fallback rule keeps the model unchanged and lets single-step objectives stay frictionless (the cell just falls back to the objective name).

### Why now, not later

The seam is visible *because* of REQ-017 + REQ-020. Xray-imported objectives can legitimately have a meaningful `Description` (the Jira test summary) that differs from the slugged `Name`. As we move from "one user-written objective per AI generation" (rarely multi-step) to "Xray import → record → execute" (frequently multi-step, frequently with rich descriptions), the inner table becomes a primary read surface. Fixing it now means the description fields populated by REQ-017's two-pass importer light up immediately without further work.

## Current behaviour

The five inner tables today:

| File | What the "Test Name" cell renders |
|---|---|
| `ui/src/components/WebUiTestCaseTable.tsx:91` | `{tc.objectiveName}` only |
| `ui/src/components/DesktopUiTestCaseTable.tsx:102` | `{tc.objectiveName}` only |
| `ui/src/components/TestCaseTable.tsx:101` | `{tc.objectiveName}` only (API) |
| `ui/src/components/AseXmlTestCaseTable.tsx:51-53` | `{tc.objectiveName}` + subtitle `{tc.step.description}` when description differs |
| `ui/src/components/AseXmlDeliveryTestCaseTable.tsx:51-53` | `{tc.objectiveName}` + subtitle `{tc.step.description}` when description differs |

The persistence model already supports the change. Every step definition type has a `Description` field:

- `WebUiTestDefinition.Description` (`src/AiTestCrew.Storage/Shared/WebUiTestDefinition.cs:12`)
- `WebUiTestCase.Description` (`src/AiTestCrew.Storage/Shared/WebUiTestCase.cs:15`)
- `DesktopUiTestDefinition.Description` (`src/AiTestCrew.Storage/Shared/DesktopUiTestDefinition.cs:12`)
- `DesktopUiTestCase.Description` (`src/AiTestCrew.Storage/Shared/DesktopUiTestCase.cs:15`)
- API: `ApiTestDefinition` carries description on its underlying case
- aseXML: already used (see the two tables above)

The Edit dialogs already let users edit `Description` — `EditWebUiTestCaseDialog.tsx:139-141` renders a "Description" input bound to `form.description`. No new editor surface is needed for Web UI; verify parity for Desktop UI, API, and aseXML dialogs (see "Files to touch").

## Desired behaviour

### Rendering rule (applied uniformly to all five inner tables)

The "Test Name" cell renders by this precedence:

```
displayedTestName = (step.description ?? "").trim().length > 0
  ? step.description.trim()
  : objective.name
```

That is — non-empty trimmed `description` wins; blank or whitespace-only falls back to `objective.name`.

The cell is single-line for all surfaces. The current aseXML dual-line variant (objective name on top, description as muted subtitle below) is **collapsed** to the single-line rule. Rationale: consistency across all five surfaces; the subtitle was only there in aseXML because the description was the canonical label and the objective name was redundant — under the new rule the canonical label simply *is* what the cell shows.

### Outer "Test Case" column — unchanged

The outer `TestSetDetailPage.tsx:453` "TEST CASE" cell continues to render `obj.name`. Multi-step objectives still aggregate under a single outer row; the outer row has no concept of "which description to pick" and the objective name is the correct identifier there.

### Edit dialog parity

For every step kind that supports inline edit:

- The dialog has a clearly-labelled "Description" field that maps to `step.description`.
- Saving the dialog writes the new description back to the step definition (not to the objective).
- If the user clears the description, the inner cell falls back to the objective name automatically — no other action required.

Audit and verify (no new editor work expected for Web UI; verify the others):

| Dialog | Description editor present? | Action if missing |
|---|---|---|
| `EditWebUiTestCaseDialog.tsx` | Yes (line 139-141) | none |
| `EditDesktopUiTestCaseDialog.tsx` | verify | add a `Description` input matching the Web UI dialog if absent |
| `EditTestCaseDialog.tsx` (API) | verify | add a `Description` input if absent |
| aseXML edit dialog(s) | verify | add a `Description` input if absent |

### Search / filter behaviour

If any inner table currently supports text search over the visible cells (none do today, but checking), the search should match against the *rendered* name — i.e., search against `description || name`, not the raw objective name. Today no filter exists on the inner tables so this is documentary; called out so a future filter feature inherits the rule.

### No change to outer-row aggregation, run-trigger, or persistence

- Outer-row sort, status badges, run/move/delete actions: unchanged.
- Persistence JSON shape: unchanged.
- API responses: unchanged.
- Migration: none. Existing test sets pick up the new rendering on first reload — blank descriptions just render as objective names, matching today.

## Files to touch

| File | Why |
|---|---|
| `ui/src/components/WebUiTestCaseTable.tsx` | Replace `<td>{tc.objectiveName}</td>` at line 91 with the description-or-fallback rule. Also update the `caseName` passed to `EditWebUiTestCaseDialog` so the description override is reflected in the dialog header (or leave it as `objectiveName` and add a separate `descriptionForHeader` prop — TBD by implementer). |
| `ui/src/components/DesktopUiTestCaseTable.tsx` | Same change at line 102. |
| `ui/src/components/TestCaseTable.tsx` | Same change at line 101 for the API inner table. |
| `ui/src/components/AseXmlTestCaseTable.tsx` | Collapse the existing two-line variant (lines 51-53) into the single-line rule for consistency. |
| `ui/src/components/AseXmlDeliveryTestCaseTable.tsx` | Same — collapse two-line variant (lines 51-53). |
| `ui/src/components/EditDesktopUiTestCaseDialog.tsx` | Verify a `Description` input is present and persisted; add it if missing, matching `EditWebUiTestCaseDialog.tsx`. |
| `ui/src/components/EditTestCaseDialog.tsx` | Verify a `Description` input is present and persisted; add it if missing. |
| `ui/src/components/<aseXml edit dialogs>` | Verify `Description` is editable. |
| `ui/src/types/index.ts` *(if needed)* | No change expected — `description` already on the step definition types. |
| `docs/functional.md` | Add a one-paragraph note under the "Test Cases" section explaining the rendering rule, with a worked example of a multi-step objective. |
| `CLAUDE.md` | Update the relevant entry in the "Where to extend" map if step description rendering needs a dedicated row (probably not; this REQ is self-contained). |

No C# changes are required.

## Acceptance criteria

1. **Description wins over objective name.** Given a Web UI test case with `objective.name = "Search"` and `step.description = "Search by NMI"`, the inner table's "Test Name" cell shows `Search by NMI` (not `Search`). Verified by editing the description in `EditWebUiTestCaseDialog`, saving, and observing the cell.
2. **Empty description falls back to objective name.** Given a Web UI test case with `objective.name = "Search"` and `step.description = ""`, the cell shows `Search`. Verified by clearing the description in the Edit dialog, saving, and observing the cell.
3. **Whitespace-only description falls back to objective name.** Given `step.description = "   "` (whitespace), the cell shows the objective name. Verified by manually editing the JSON to whitespace and reloading.
4. **Outer "Test Case" cell unchanged.** Editing only the step description does NOT change the outer "TEST CASE" cell on `TestSetDetailPage`. Verified by inspecting the outer row after the same edit as AC#1.
5. **Multi-step disambiguation.** Given a Web UI objective with three steps whose `description` values are `"Search by NMI"`, `"Search by Meter Serial"`, `"Search by NTC"`, the inner table shows three rows with those three labels — not three rows labelled with the objective name. Verified by manually adding three steps to a test set and reloading.
6. **All five inner tables apply the rule.** Repeat AC#1 + AC#2 for: Web UI (`WebUiTestCaseTable`), Desktop UI (`DesktopUiTestCaseTable`), API (`TestCaseTable`), aseXML (`AseXmlTestCaseTable`), aseXML delivery (`AseXmlDeliveryTestCaseTable`). Each surface must render the same precedence.
7. **aseXML two-line layout collapsed.** The previous "objective name + description subtitle" variant in `AseXmlTestCaseTable.tsx:51-53` and `AseXmlDeliveryTestCaseTable.tsx:51-53` is reduced to a single line following the precedence rule. The cell no longer shows both labels at once.
8. **Edit dialog parity.** For every step kind that supports inline edit (Web UI, Desktop UI, API, aseXML), the Edit dialog has a "Description" input that reads and writes `step.description`. Where missing, a matching input is added. Verified by opening each dialog and confirming the field exists.
9. **No persistence change.** Loading a test set JSON saved before this REQ shipped renders correctly without migration. Blank-description steps render the objective name (matching today's behaviour); non-blank steps render the description. Verified by checking an existing test set.
10. **No regression in run-trigger / status / delete flows.** Running, moving, and deleting a test case from the inner table behaves identically to today. The display-only rename does not affect `objective.id` (which is the canonical identifier).

## Scope — what's out

- **No new `Name` field per step.** The model stays as it is. Per-step naming uses `Description`.
- **No auto-population of `Description`.** The recorder does not start writing a default description on save. (Future REQ if desired — e.g., auto-fill the description from the recorded URL or the first step's element label.)
- **No outer "Test Case" cell change.** The outer table continues to show `obj.name`. Aggregating descriptions there would need a "pick one" rule that doesn't exist; out of scope.
- **No search/filter implementation.** This REQ specifies the rule for a future filter; no filter UI is being added now.
- **No backend changes.** Pure UI rendering. No C# touched.
- **No tooltip / hover affordance on the rendered cell.** A user who wants to see both the description and the objective name can open the Edit dialog. A tooltip ("Step description; objective is X") is a nice-to-have but out of v1.
- **No badge / icon difference between "description rendered" vs "fallback to objective name".** The cell looks the same either way; users don't need to know which path produced the text.
- **No bulk-edit affordance.** Setting descriptions on all steps in a multi-step objective in one operation is out of scope.

## Risks / notes

- **Edit dialog header confusion.** Today `EditWebUiTestCaseDialog` receives `caseName={tc.objectiveName}` as its header. After this change, the inner row shows `step.description` when present, but clicking the row opens a dialog whose header is the objective name. Implementer should either (a) pass the rendered name through as the dialog header, or (b) keep the objective name in the header and label the Description field clearly so users understand the distinction. Option (b) is cleaner — the dialog edits *both* fields and the user can see them side by side.
- **`caseName` prop misuse downstream.** Several call sites pass `tc.objectiveName` as `caseName` to `EditWebUiTestCaseDialog` (line 169), `PostStepsPanel` (line 155), and `startRecording` (line 233). These all want the **canonical case identity** (which is the objective name / slug source), not the display label. Leave these as `objectiveName`. Only the rendered `<td>` cell changes.
- **aseXML layout regression.** Power users who relied on the existing two-line aseXML display may notice the secondary subtitle is gone. Mitigation: where `objective.name` differs meaningfully from `step.description`, both are now reachable via the Edit dialog. If real pushback comes in, the two-line variant can be reintroduced as a per-table opt-in — but consistency wins by default.
- **Description values not yet populated for legacy data.** Existing recorded objectives have `description = ""`. The rendering falls back to objective name, matching today exactly — no regression. New objectives created via Xray import already have meaningful descriptions (the LLM-decomposed fragment), so they light up immediately.
- **Multi-step rendering depends on per-step description being populated.** If a user adds a second recording to an existing objective without setting a description, the two rows will display identically (both fall back to objective name). This is the same confusion the REQ is trying to solve, just shifted from "objective rename surprises" to "two rows look the same". Mitigation: the Edit dialog's Description field is the user's tool — when they notice the ambiguity they have an obvious place to fix it. A follow-up REQ could prompt for a description at recording-save time.
- **Test set list / search surfaces.** If any other page (e.g., a global "all test cases" search) renders step rows, it should apply the same rule. Audit during implementation; not expected to exist today.

## How this lands with the existing system

REQ-022 is a pure UI rendering change. No schema, no API, no agent behaviour changes. The five inner tables learn one precedence rule. The aseXML tables already had a partial form of this (subtitle) — the REQ aligns them to the new single-line rule. Existing test sets render identically when descriptions are blank (the common case today) and start lighting up disambiguating labels wherever descriptions are present (Xray-imported objectives, AI-generated cases with populated descriptions, user-edited entries).

## Demonstration script (for the reviewer)

1. Open a test set with a Web UI test case whose step has no description (most existing ones). Confirm the inner "Test Name" cell shows the objective name — same as today.
2. Click the inner row, open the Edit dialog, type a description like `"Smoke — happy path"`, save.
3. Confirm the inner cell now shows `Smoke — happy path`. Confirm the outer "TEST CASE" cell still shows the objective name. Reload the page; confirm both persist.
4. Add a second step to the same objective (via Xray import + record, or manual JSON edit) with a different description. Confirm the inner table shows two rows with distinct labels.
5. Repeat steps 1-3 for a Desktop UI test case and an API test case to verify cross-surface parity.
6. Open an aseXML test set; confirm the cell is single-line (no more "objective name + description subtitle"). Confirm the precedence rule applies.
7. Edit any step's description back to empty; confirm the cell falls back to the objective name.
