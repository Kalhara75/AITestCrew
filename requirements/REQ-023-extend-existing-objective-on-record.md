---
id: REQ-023
title: Recording dispatch appends to any matching existing objective regardless of Source, not just the `recorded-*` shape
status: Proposed
created: 2026-05-18
author: Kalhara Samarasinghe
author-note: REQ-020 closed the empty-Xray-placeholder gap but exposed a follow-on gap. The recorder's append branch (`RecordingService.cs:199-202` for web, `:118-122` for desktop) only fires when an objective with id `recorded-{slug}` already exists. Xray-imported objectives have id `{slug}` (no prefix). So the first recording after import fills the placeholder in place (REQ-020 path) and flips `Source` to `ImportedFromXray+Recorded`, but the **second** recording with the same case name re-enters `RecordCaseAsync`, finds that the placeholder check now fails (steps are non-empty), looks up `recorded-{slug}` (doesn't exist — the objective's id is `{slug}`), and falls through to creating a `recorded-*` sibling. The user ends up with two test cases of the same name again — exactly the symptom REQ-020 was meant to eliminate. The same shape applies to any future objective whose id is the bare slug rather than `recorded-{slug}` (e.g. Generated UI objectives if/when those land, or any imported test case that has already been extended once). The user's mental model is consistent: "I should be able to extend any test case with another step, regardless of how it was created." This REQ generalises the recorder's lookup so the append-vs-sibling decision is governed by *name + target-type match*, not by the historical id prefix.
area: agents
related: REQ-020 (record-into-imported-placeholder — direct predecessor), REQ-017 (Xray import — sets up the `{slug}` id shape), REQ-022 (per-step description rendering — makes multi-step objectives readable in the UI)
---

# REQ-023 — Recording dispatch appends to any matching existing objective regardless of Source

## Goal

After REQ-023 ships, a `Record` dispatch with a `caseName` matching an existing objective (by name + compatible target type) appends the new step to that objective, regardless of the objective's `Source` value. The "append vs new sibling" decision is driven by **name match + target-type compatibility**, not by whether the objective's id begins with `recorded-`.

Specifically:

1. Given any objective whose Name (case-insensitive) matches the dispatched `caseName` AND whose `TargetType` matches the recording target AND whose relevant step list is non-empty (or empty-placeholder, covered by REQ-020), the recorder **appends** the new step to that objective.
2. A new sibling `recorded-{slug}` objective is created only when **no** matching objective exists.
3. The objective's `Source` field updates predictably:
   - `Recorded` → `Recorded` (no change — same as today)
   - `ImportedFromXray` (placeholder) → `ImportedFromXray+Recorded` (REQ-020 path, no change)
   - `ImportedFromXray+Recorded` → `ImportedFromXray+Recorded` (no change — fix is that the append happens at all)
   - `Generated` (UI-kind, hypothetical) → `Generated+Recorded`
   - `Generated+Recorded` → `Generated+Recorded` (no change)
4. The fallback path — sibling creation — still works when there is genuinely no matching objective. Existing single-recording workflows are unaffected.

Cross-kind extension (e.g. adding a Web UI recording to an API-only `Generated` objective) remains **out of scope** — it requires multi-kind execution in the orchestrator, which is a separate concern. When the recording target's kind doesn't match the objective's primary kind, the recorder falls through to sibling creation rather than mixing kinds.

## Why now

### Concrete defect — REQ-020 only handles the first recording

Reproduction:

1. Import Jira/Xray ticket BQ-35775 → test set has objective id `network-tariff-code-search-screen-functionality`, `Source = "ImportedFromXray"`, `WebUiSteps[0].Steps = []`.
2. Ask the assistant to record. Recorder takes the REQ-020 path — fills the placeholder in place, sets `Source = "ImportedFromXray+Recorded"`.
3. Test set page now shows the test case with `N steps` recorded. So far so good.
4. Ask the assistant to record **another** step for the same case name (e.g. *"record another Blazor step for 'Network Tariff Code Search Screen Functionality'"*).
5. Recorder enters `RecordCaseAsync`. Imported-placeholder check fails (`IsImportedPlaceholder` returns false because `WebUiSteps[0].Steps.Count > 0`). Lookup falls through to `FindIndex(o => o.Id == "recorded-{slug}")`.
6. That id doesn't exist — the objective's id is `{slug}`, not `recorded-{slug}`.
7. Recorder creates a new `recorded-network-tariff-code-search-screen-functionality` objective.
8. Test set now has **two** test cases with the same display name: the original (Xray-imported, 1 step) and a new `recorded-*` sibling (1 step). The user is back to the duplicate-objective state REQ-020 set out to prevent.

### Concrete defect — the lookup key embeds a creation lineage that no longer matches user intent

The current lookup key `recorded-{slug}` was chosen back when the recorder was the only way to create UI objectives. Every UI objective had id `recorded-*`, so the lookup was unambiguous. Then REQ-017 introduced Xray-imported objectives (id `{slug}`, no prefix), and REQ-020 made them fillable. After REQ-020 the codebase has two shapes of UI objective id (`{slug}` and `recorded-{slug}`), but the recorder still only knows how to extend one of them. The fix is to look up by **what the objective is** (name + target type), not by **how it was originally created** (id prefix).

### Why the user wants this

> "In my view irrespective of recorded, ai-generated or xray-generated, user should be able to extend with additional steps if required."

The user's mental model is uniform: "a test case is a test case; I should be able to add steps." Today the behaviour diverges based on internal id shape, which is invisible to the user. Closing this gap means the assistant's `Record` flow does what users expect across all sources.

### Why the workaround is bad

Today's workaround for the Xray+Recorded case is one of:

- **Merge manually**: open the test set JSON, copy the `WebUiSteps[1]` from the new sibling into the original objective, delete the sibling. Tedious and error-prone.
- **Edit dialog re-record**: deletes existing steps. Loses the first recording.
- **Live with two duplicates**: each runs separately; status is reported per-objective; users have to remember which is "the real one".

None of these is acceptable for a feature ("extend an existing test case") that should be one-shot from the assistant.

## Current behaviour

Trace of a second-record dispatch against an already-recorded Xray-imported objective:

1. Test set has `objective.Id = "tariff-search"`, `Source = "ImportedFromXray+Recorded"`, `WebUiSteps.Count = 1` (the first recording's steps).
2. Assistant emits `confirmRecord` with `recordingKind: Record`, `caseName: "Tariff Search"`.
3. `RecordingService.RecordCaseAsync` runs.
4. `RecordingService.cs:177`: `webCaseSlug = "tariff-search"`.
5. `RecordingService.cs:178-179`: `IsImportedPlaceholder` check returns false (steps non-empty).
6. `RecordingService.cs:199`: `FindIndex(o => o.Id == "recorded-tariff-search")` returns -1.
7. `RecordingService.cs:206-215`: creates a brand-new objective `recorded-tariff-search` with `Source = "Recorded"`.
8. Test set persisted; two objectives, same display name.

The desktop branch (`RecordingService.cs:97-135`) has the same defect with `DesktopUiSteps`.

## Desired behaviour

### Lookup order (replaces existing two-step lookup in both branches)

`RecordingService.RecordCaseAsync` adopts a three-step lookup, applied in both the desktop branch (around line 97) and the web branch (around line 177):

1. **First — imported placeholder match** (REQ-020 path, unchanged):
   - `o.Source.StartsWith("ImportedFromXray")` AND
   - `o.Id == SlugHelper.ToSlug(r.CaseName)` AND
   - `IsImportedPlaceholder(isDesktop)` returns true (i.e., the relevant step list is empty or has a single empty entry).
   - If found, fill in place per REQ-020 — replace the empty step entry, preserve `PostSteps`, set `Source = "ImportedFromXray+Recorded"`.

2. **Second (NEW) — extensible-objective match** by name + target-type:
   - `TestObjective.IsExtensibleByRecording(target, isDesktop)` returns true (helper, see below).
   - Match candidates, in order:
     - `o.Id == SlugHelper.ToSlug(r.CaseName)` — matches Xray-imported objectives (`{slug}`) and any other bare-slug objective.
     - `o.Id == $"recorded-{SlugHelper.ToSlug(r.CaseName)}"` — matches existing Recorded objectives (today's behaviour).
     - `o.Name.Equals(r.CaseName, StringComparison.OrdinalIgnoreCase)` — fallback for slug drift (punctuation differences). Only applied if neither id match succeeds.
   - If found, **append** the new `WebUiTestDefinition` / `DesktopUiTestDefinition` to the relevant step list. Update `Source` per the state machine below.

3. **Third — create new `recorded-{slug}` sibling** (existing fallback):
   - No matching objective found by either of the above.
   - Create a brand-new `TestObjective` with `Source = "Recorded"`, exactly as today.

### `IsExtensibleByRecording` helper

New method on `TestObjective` (sibling to the existing `IsImportedPlaceholder`):

```csharp
/// <summary>
/// True when this objective can accept an additional recorded step of the given target type.
/// Requires the objective's TargetType to match the recording target AND the objective to
/// be non-empty for that target's step list. Cross-kind extension (e.g. recording UI into an
/// API-only Generated objective) returns false — those cases should fork a sibling.
/// </summary>
public bool IsExtensibleByRecording(string recordingTarget, bool isDesktop)
{
    // Target type must match the recording target.
    if (!string.Equals(TargetType, recordingTarget, StringComparison.OrdinalIgnoreCase))
        return false;

    // The relevant step list must already contain at least one definition (i.e., not an
    // empty placeholder — that path is handled by IsImportedPlaceholder + REQ-020).
    if (isDesktop)
        return DesktopUiSteps.Count > 0 && DesktopUiSteps[0].Steps.Count > 0;
    return WebUiSteps.Count > 0 && WebUiSteps[0].Steps.Count > 0;
}
```

`recordingTarget` is the string the recorder is dispatching against (`UI_Web_Blazor`, `UI_Web_MVC`, `UI_Desktop_WinForms`). `isDesktop` is derived from `recordingTarget`. The function deliberately rejects empty step lists — those are placeholder shapes handled by REQ-020 — to keep the three steps in the lookup order disjoint.

### Source field state machine (extended from REQ-020)

| Initial Source | Action | New Source |
|---|---|---|
| `Recorded` | append step | `Recorded` (unchanged) |
| `ImportedFromXray` (placeholder, empty steps) | fill in place | `ImportedFromXray+Recorded` (REQ-020) |
| `ImportedFromXray+Recorded` | append step (REQ-023) | `ImportedFromXray+Recorded` (unchanged) |
| `Generated` (UI-kind, hypothetical) | append step (REQ-023) | `Generated+Recorded` |
| `Generated+Recorded` | append step | `Generated+Recorded` (unchanged) |
| `Generated` (API-only) + UI recording dispatch | TargetType mismatch → fall through to sibling | new `Recorded` sibling (no change to original) |

The `Generated` → `Generated+Recorded` row is currently dormant — today's `Generated` objectives are AI-generated **API** objectives (`TargetType = "API"`), and the recording target is always a UI kind, so the `TargetType` check in `IsExtensibleByRecording` rejects them and falls through to sibling creation. The row is documented so that any future "AI-generated UI objective" capability inherits the correct append behaviour automatically. There is no implementation work needed today to support that row — it is forward-looking.

### Rebaseline gate audit

`docs/architecture.md` notes that "rebaseline is only allowed for generated objectives." After REQ-023 there are at least three Source values that could trip up a naïve check:

- `Generated` — allowed to rebaseline (canonical case).
- `Generated+Recorded` — must **not** rebaseline; rebaselining would regenerate the API steps and wipe the recorded UI step.
- `ImportedFromXray+Recorded` — must **not** rebaseline (REQ-020 already documented this).
- `Recorded` — must **not** rebaseline.

Any existing code that gates on `Source.StartsWith("Generated")` or `Source.Contains("Generated")` would incorrectly allow `Generated+Recorded`. The gate must be tightened to `Source == "Generated"` (strict equality). Search the codebase for `"Generated"` matches and audit each — primary suspects:

- `TestOrchestrator.cs` rebaseline mode handling (around the `RunMode.Rebaseline` branches at lines 587, 705).
- `RunEndpoints.cs:67` already uses strict equality (`Source == "Recorded"`) — pattern is correct.
- UI badge logic in `TestSetDetailPage.tsx` (currently uses `startsWith`/exact for badge selection; verify the rebaseline button visibility).

### Assistant routing — no change required

`ChatIntentService.cs` already emits `recordingKind: Record` for "record another step on existing X" prompts (REQ-020 already aligned this). No system-prompt change in this REQ. The recorder, not the assistant, is responsible for deciding "extend vs new sibling".

### UI — minor badge work

The test set detail page currently renders badges for `Source = "Recorded"`, `Source = "ImportedFromXray"`, and `Source.StartsWith("ImportedFromXray+")` (REQ-020). REQ-023 adds:

- `Source = "Generated+Recorded"` → optional new badge ("AI + Recorded" or similar). Low priority — today no objective will land in this state because Generated is API-only and the TargetType check rejects the append. The badge would only become visible if a future feature introduces Generated UI objectives.

No new buttons or affordances. The "Record this" button on imported placeholders (REQ-020) still applies only to empty placeholders.

## Files to touch

| File | Why |
|---|---|
| `src/AiTestCrew.Storage/Persistence/TestObjective.cs` | Add `IsExtensibleByRecording(string recordingTarget, bool isDesktop)` helper next to the existing `IsImportedPlaceholder`. |
| `src/AiTestCrew.Agents/Recording/RecordingService.cs` | Both branches (`RecordCaseAsync` desktop ~line 97, web ~line 177): expand the two-step lookup to three steps. After the imported-placeholder check, try the extensible-objective match; only then fall through to `recorded-{slug}` creation. Update `Source` per the state machine when appending. |
| `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` | Tighten rebaseline gate so `Generated+Recorded` and `ImportedFromXray+Recorded` are not rebaseline-eligible. Probably `Source == "Generated"` strict-equality check — audit lines 587, 705 plus any helpers. |
| `ui/src/pages/TestSetDetailPage.tsx` *(optional, low priority)* | Add a `Source = "Generated+Recorded"` badge to mirror the existing `ImportedFromXray+Recorded` badge. Skip if not visible to current users. |
| `ui/src/components/WebUiTestCaseTable.tsx` | Verify the imported-placeholder "Record this" button still hides correctly under the new Source values (it gates on `Source == "ImportedFromXray"` AND empty steps — should already be correct). |
| `ui/src/components/DesktopUiTestCaseTable.tsx` | Same verification. |
| `docs/functional.md` | Add a paragraph under "Recording into an imported placeholder" (or sibling section) explaining the generalised extend-or-fork rule. |
| `docs/architecture.md` | Extend the `Source` state machine diagram with the new transitions. Update the rebaseline-gate description to mention strict equality. |
| `CLAUDE.md` | Update the extension-map row that mentions REQ-020's in-place fill to also reference REQ-023's general-purpose extension path. |
| `tests/AiTestCrew.Agents.Tests/Recording/*` *(if recording tests exist)* | Add coverage for the new lookup priorities — see Acceptance criteria below. |

No schema migration. No API contract change. No new agent capability or registration.

## Acceptance criteria

1. **Xray+Recorded objective extends in place.** Given an objective with `Source = "ImportedFromXray+Recorded"`, `Id = "tariff-search"`, `WebUiSteps.Count = 1`, a `Record` dispatch with `caseName = "Tariff Search"` and `target = "UI_Web_Blazor"` appends a second `WebUiTestDefinition` to the same objective. No `recorded-tariff-search` sibling is created. `Source` remains `"ImportedFromXray+Recorded"`. `WebUiSteps.Count == 2` after.
2. **Recorded objective extends in place (no regression).** Given `Source = "Recorded"`, `Id = "recorded-login"`, `WebUiSteps.Count = 1`, a `Record` dispatch with `caseName = "Login"` appends. `WebUiSteps.Count == 2`. `Source` stays `"Recorded"`. Verifies the existing append path remains intact under the new lookup logic.
3. **Empty imported placeholder still fills in place (REQ-020 path).** Given `Source = "ImportedFromXray"`, `WebUiSteps[0].Steps.Count = 0`, the imported-placeholder branch wins over the new extensible-objective branch. `Source` becomes `"ImportedFromXray+Recorded"`. Verifies the priority ordering — placeholder fill is checked before extension.
4. **No matching objective creates a sibling (no regression).** Given a test set with no objective named `"Brand New Case"`, a `Record` dispatch with that case name creates a new objective with `Id = "recorded-brand-new-case"`, `Source = "Recorded"`. No mutation of existing objectives.
5. **TargetType mismatch falls through to sibling.** Given a `Generated` API objective with `Id = "get-users"`, `TargetType = "API"`, `ApiSteps.Count = 1`, a `Record` dispatch with `caseName = "Get Users"` and `target = "UI_Web_Blazor"` does **not** append to the API objective. Instead, a new `recorded-get-users` sibling is created with `Source = "Recorded"`. The original API objective is untouched.
6. **Desktop parity.** Repeat AC#1 + AC#2 + AC#4 for `UI_Desktop_WinForms` against `DesktopUiSteps`. Same behaviour as the web branch.
7. **Slug drift via Name fallback.** Given an objective with `Name = "Tariff: Search"` and `Id = "tariff-search"` (the colon is dropped by slugification), a `Record` dispatch with `caseName = "Tariff Search"` (no colon, same slug) finds the objective via the slug match and appends. Given a dispatch with `caseName = "Tariff Search!"` (different slug `tariff-search-1` after collision handling, but same name after trimming), it falls through to the case-insensitive Name match and appends. Verifies the third candidate in step 2 of the lookup.
8. **Rebaseline gate excludes `+Recorded` Sources.** A rebaseline run dispatched against an objective with `Source = "Generated+Recorded"` (or any `+Recorded` variant) returns an error / is rejected by the orchestrator. The objective is unchanged. Only `Source = "Generated"` (strict) is rebaseline-eligible. Test set JSON before and after a blocked rebaseline is identical.
9. **PostSteps preserved on append.** When a step is appended to an existing objective, the objective's other steps' `PostSteps` are unchanged. The new step has empty `PostSteps` (consistent with a fresh recording). No cross-contamination.
10. **Re-import idempotency still works (REQ-020 AC#8 still passes).** Re-importing an Xray ticket after a second recording (AC#1) preserves both recorded steps. The `Source.EndsWith("+Recorded")` guard in `XrayImportService` continues to prevent the step lists being wiped.
11. **End-to-end demonstration via assistant.** Manually exercised: import BQ-35775 → record first step (REQ-020 fills placeholder) → ask assistant to record another step with the same case name → verify the test set shows ONE objective with TWO steps, not two objectives. Browser visual check.

## Scope — what's out

- **Cross-kind extension.** Adding a UI recording to an API-only objective (or vice versa) is rejected by the TargetType check. The user gets a new sibling, not a mixed objective. Supporting mixed objectives requires the orchestrator to run all step kinds for one objective, which is a substantially larger change. Deferred to a future REQ if real demand emerges.
- **AI-generated UI objectives.** Today `Generated` Source is always API. The state-machine row `Generated → Generated+Recorded` is documented but the transition is unreachable from current code paths. No work is done to introduce Generated UI objectives.
- **Re-recording (replace, not append).** This REQ is strictly about appending another step. A user who wants to *replace* an existing step opens the Edit dialog and uses its re-record affordance (or deletes the step and records again). No new "replace step" intent is added to the recorder.
- **Bulk extension.** Adding multiple recordings in one dispatch is not supported. Each dispatch produces exactly one new step (per existing single-recording semantics).
- **CLI flag changes.** `--record --case-name "<name>"` continues to pass `caseName` through unchanged. The new lookup is purely internal to `RecordingService`.
- **Assistant prompt changes.** `ChatIntentService.cs` already emits `recordingKind: Record` for "record another step on X" prompts (REQ-020 covered this). No system-prompt change.
- **Undo / step removal.** If a user appends a step they didn't want, they delete it via the existing per-step delete affordance (`WebUiTestCaseTable.tsx:46-63`). No new undo flow.
- **Conflict detection on concurrent record dispatches.** If two recording jobs target the same objective and complete near-simultaneously, the second `SaveAsync` could overwrite the first's append. REQ-008 (concurrent test-set edit protection) is the right place for this; out of scope here.

## Risks / notes

- **Source field divergence is now broader.** After REQ-020 the codebase had two `+Recorded` shapes (`Recorded`, `ImportedFromXray+Recorded`). REQ-023 introduces a third potential shape (`Generated+Recorded`), even though it's dormant today. Strict-equality `Source == "Generated"` checks are the safe pattern; `Source.StartsWith("Generated")` is the bug shape. Audit needed across `TestOrchestrator`, `*TestCaseTable.tsx` badge logic, `MigrationHelper`. AC#8 is the explicit guard.
- **Name-based fallback (third candidate) could match the wrong objective.** If two objectives in the same test set have the same `Name` but different `TargetType` (allowed by current persistence), the slug match might find one but the Name fallback could match the other. Mitigation: the `IsExtensibleByRecording` check requires `TargetType` match, so a name-only collision with a different target type still falls through to sibling creation correctly.
- **Multiple `recorded-{slug}` sibling forms could coexist after upgrade.** A user who already had the duplicate state from before REQ-023 (one Xray-imported objective + one `recorded-*` sibling, both with the same display name) doesn't get automatic cleanup. Future recordings will append to whichever the lookup hits first — the bare-slug objective takes priority (step 2 candidate 1). Acceptable: users with the legacy state can manually merge, and new state is always correct.
- **Append order is "newest last".** The new step lands at the end of `WebUiSteps` / `DesktopUiSteps`. UI rendering already preserves insertion order, so the user sees the new step as the bottom row. No sort needed.
- **Recording failure on append.** If the recorder fails (zero steps captured, line 168 returns Fail), the objective is unchanged — same guard as today's append path. Verified: `webRecorded.Steps.Count == 0` returns before any persistence call.
- **TestObjective.IsExtensibleByRecording vs IsImportedPlaceholder asymmetry.** The two helpers cover complementary cases — one returns true for *empty* placeholders, the other for *non-empty* objectives — and the lookup uses them in that order. Implementers should not refactor them into one combined helper; the separation matches the two distinct semantic outcomes (replace empty entry vs append new entry).
- **Multi-WebUiSteps objectives.** If an objective has `WebUiSteps.Count > 1` (shape produced by REQ-023 itself, or by manual JSON editing), `IsExtensibleByRecording` checks `WebUiSteps[0].Steps.Count > 0` for "is the first step real?". This is a heuristic. An objective with `WebUiSteps = [empty, real]` would pass the check but is structurally invalid. Acceptable risk — the empty-then-real shape only arises from manual JSON corruption and isn't producible via normal flows.

## How this lands with the existing system

REQ-023 expands the recorder's lookup branch — a single additional check between REQ-020's placeholder fill and today's `recorded-*` creation. The change is additive:

1. Existing behaviour for `Source = "Recorded"` objectives is unchanged (candidate 2 of step 2 matches the same id as today).
2. REQ-020's placeholder-fill behaviour is unchanged (step 1 still fires first).
3. The new path activates only for `Source = "ImportedFromXray+Recorded"` (today's primary defect), the dormant `Generated+Recorded` row, and the slug-drift case.
4. When no match is found, sibling creation is unchanged (step 3).

The blast radius is small and clearly bounded by the `IsExtensibleByRecording` guard. The Source state machine grows by one transition (`Generated → Generated+Recorded`) which is currently unreachable. Rebaseline-gate tightening is the only "ripple" change and is covered by AC#8.

## Demonstration script (for the reviewer)

1. **Repeat REQ-020 demo** (import BQ-35775, record first step, verify placeholder is filled). Confirms REQ-020 still works.
2. **New: record second step.** Ask the assistant *"record another Blazor step for 'Network Tariff Code Search Screen Functionality'"*. Drive the browser; close it.
3. Verify the test set page now shows **one** test case with **two** steps. No sibling `recorded-*` objective. `Source = "ImportedFromXray+Recorded"`.
4. Open the Edit dialog on the second step; confirm it's editable independently of the first.
5. **Regression check 1 — pure Recorded path.** Create a test set; record a UI test case called "Login". Record a second step with the same name. Verify ONE objective with two steps, `Source = "Recorded"`.
6. **Regression check 2 — fresh case.** Record a UI test case called "Brand New Case" against a test set with no matching objective. Verify a new `recorded-brand-new-case` objective is created. `Source = "Recorded"`.
7. **TargetType mismatch.** In a test set that has a `Generated` API objective named "Get Users", dispatch a `Record` with `caseName = "Get Users"` and target `UI_Web_Blazor`. Verify a separate `recorded-get-users` sibling is created (no append to the API objective).
8. **Rebaseline guard.** Attempt to rebaseline a `Generated+Recorded` objective (you'll need to synthesise one by editing JSON, since the production path doesn't produce them today). Verify the rebaseline is rejected and the JSON is unchanged.
9. **Desktop parity.** Repeat steps 2-3 with an Xray ticket that maps to `UI_Desktop_WinForms`. Same outcome on `DesktopUiSteps`.
