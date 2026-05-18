---
id: REQ-020
title: Recording dispatch fills an existing imported placeholder in-place instead of creating a "recorded-" sibling
status: Proposed
created: 2026-05-18
author: Kalhara Samarasinghe
author-note: After REQ-017 + REQ-019, importing a Jira Xray ticket like BQ-35775 produces a Web UI test case (e.g. *Network Tariff Code Search Screen Functionality*) with `WebUiSteps[0].Steps = []` — a placeholder waiting for the QA to record the actions. The natural next step is "record the actions for that placeholder." Today, that's not directly supported: `RecordingService.RecordCaseAsync` builds `objectiveId = "recorded-{slug(caseName)}"` (line 78) and the imported objective has id `"{slug(title)}"` (no prefix), so the two never match. The recorder always creates a **second**, sibling `recorded-*` objective. The QA is then forced to either (a) delete the imported placeholder and lose the Xray description + post-steps, or (b) live with two objectives where one is empty noise. This REQ closes the seam by detecting the imported placeholder and filling it in-place.
area: agents + webapi
related: REQ-017 (Xray import — base), REQ-019 (post-step authoring — provides the post-steps that must be preserved)
---

# REQ-020 — Recording dispatch fills an existing imported placeholder in-place

## Goal

After REQ-020 ships, when a QA imports an Xray ticket and then asks the assistant (or runs `--record`) to record actions for the same test case name, the recorder should **fill the existing empty placeholder** rather than create a `recorded-*` sibling. Specifically:

1. The imported `TestObjective` (id = `<slug of title>`, `Source = "ImportedFromXray"`, `WebUiSteps[0].Steps = []`) is the one that gets populated.
2. The recorded actions land as `WebUiSteps[0].Steps[…]` on that objective.
3. Any post-steps authored by REQ-019 on `WebUiSteps[0].PostSteps[…]` are **preserved** (not cleared, not duplicated).
4. The objective's `Source` field is updated to reflect the new mixed lineage (`"ImportedFromXray+Recorded"`) so the test set page can show the right badge and rebaseline rules apply correctly.
5. No `recorded-*` sibling objective is created.

The fallback — record into a fresh `recorded-*` objective — still works when there is **no** matching imported placeholder, so existing recording workflows are unaffected.

## Why now

### Concrete defect — recorder always creates a sibling

`src/AiTestCrew.Agents/Recording/RecordingService.cs:78`:

```csharp
var objectiveId = $"recorded-{SlugHelper.ToSlug(r.CaseName)}";
…
var webExistingIdx = testSet.TestObjectives.FindIndex(o => o.Id == objectiveId);
if (webExistingIdx >= 0)
{
    testSet.TestObjectives[webExistingIdx].WebUiSteps.Add(uiStep);
}
else
{
    testSet.TestObjectives.Add(new TestObjective { Id = objectiveId, … });
}
```

The lookup key is `"recorded-{slug}"`. The imported objective's id is `"{slug}"` (no prefix — see `XrayImportService.cs:138`). Match always fails. A new objective gets created every time.

Real-world reproduction:
1. Import BQ-35775 → test set has objective id `network-tariff-code-search-screen-functionality`, empty `WebUiSteps[0]`.
2. Ask the assistant "record a Blazor test case called 'Network Tariff Code Search Screen Functionality' …".
3. Recorder dispatches; saves a new objective with id `recorded-network-tariff-code-search-screen-functionality`.
4. Test set page now shows **two** test cases with the same display name. The imported one is still empty.

### Concrete defect — assistant routes "record on existing X" to RecordVerification

`src/AiTestCrew.WebApi/Services/ChatIntentService.cs:512-525` lists four recording kinds (`Record`, `RecordSetup`, `RecordVerification`, `AuthSetup`). When the user says "record on the already existing X", the assistant infers `RecordVerification` and attaches a post-step. The recorded actions land in `PostSteps[0]` instead of the parent's `Steps[]`.

This is technically a different routing bug, but it has the same root cause from the QA's perspective: **there is no path from "imported placeholder" → "record into the parent step in place"**. REQ-020 introduces that path so the assistant has somewhere correct to route to.

### Why the workaround is bad

The current workaround is "delete the imported placeholder, record fresh." But the placeholder carries:

- The Xray ticket key + objective slug (`XrayTicketKey`, `XrayObjectiveSlug`) — used by re-import for idempotency (REQ-017 AC#8).
- The post-steps authored by REQ-019 (DB / Event / API verifications).
- The `Source = "ImportedFromXray"` lineage marker.
- `Preconditions`, `TestDataNotes`, `ParentObjective` metadata.

Deleting it loses all of that. Re-importing later would then create yet another placeholder, and the QA would have to re-record.

## Current behaviour

Tracing a record dispatch against an existing imported objective:

1. QA imports BQ-35775. Objective `network-tariff-code-search-screen-functionality` exists with empty `WebUiSteps[0]` + (post-REQ-019) populated `WebUiSteps[0].PostSteps[…]`.
2. QA asks the assistant to record the test case. Assistant emits `confirmRecord` with `recordingKind: Record`, `caseName: "Network Tariff Code Search Screen Functionality"`.
3. WebApi enqueues a recording job; agent picks it up; `RecordingService.RecordCaseAsync` runs.
4. Line 78 computes `objectiveId = "recorded-network-tariff-code-search-screen-functionality"`.
5. Line 150 looks for that id — not found.
6. Line 157 creates a brand-new `TestObjective` with `Source = "Recorded"`. The imported placeholder is untouched.
7. Test set now has two objectives with the same display name. QA is confused.

## Desired behaviour

### Lookup order

`RecordingService.RecordCaseAsync` adopts a two-step lookup (mirrored in the desktop branch at line 94 and the web branch at line 150):

1. **First** try to find an existing imported placeholder:
   - `o.Source == "ImportedFromXray"` AND
   - `o.Id == SlugHelper.ToSlug(r.CaseName)` (no `recorded-` prefix) AND
   - the relevant step list is empty (`WebUiSteps.Count == 0` or has a single step with `Steps.Count == 0`; same logic for `DesktopUiSteps`).

2. **If found**, fill in-place:
   - Append the recorded `WebUiTestDefinition` to `objective.WebUiSteps` (if it has one empty entry, **replace** that entry rather than appending — so the QA doesn't end up with `[empty, recorded]`).
   - Preserve `PostSteps` if they're attached to `WebUiSteps[0]` — the empty parent is being replaced, but its `PostSteps` list must carry over to the new entry.
   - Update `Source` from `"ImportedFromXray"` to `"ImportedFromXray+Recorded"`.
   - Keep `XrayTicketKey`, `XrayObjectiveSlug`, `Preconditions`, `TestDataNotes`, `ParentObjective`, `Description` unchanged.

3. **If not found**, fall back to existing behaviour (line 78 onwards) — create `recorded-{slug}` sibling. No change for non-imported workflows.

### Source field state machine

| Initial Source | After action | New Source |
|---|---|---|
| `ImportedFromXray` (empty placeholder) | record fills it | `ImportedFromXray+Recorded` |
| `ImportedFromXray+Recorded` | record again (overwrite) | `ImportedFromXray+Recorded` (unchanged) |
| `Recorded` | record again | `Recorded` (unchanged — current behaviour) |
| `Generated` | record fills | `Generated+Recorded` (not in scope here — flagged but out for v1) |

The `+Recorded` suffix is the marker that the test case is now executable. Rebaseline rules already gate on `Source == "Generated"` (`docs/architecture.md` — "Rebaseline is only allowed for generated objectives"); `ImportedFromXray+Recorded` should fall under the same rebaseline-disallowed bucket as `ImportedFromXray` and `Recorded`.

### Assistant routing change

`ChatIntentService.cs` system prompt gains an explicit rule:

```
When the user asks to record actions for an existing imported objective (the objective has Source = "ImportedFromXray"
and its step list is empty), emit recordingKind: Record (NOT RecordVerification). The recorder detects the imported
placeholder and fills it in place. Use RecordVerification only when the user explicitly asks for a verification post-step
of an existing recorded action.
```

Concretely: a prompt like *"Record Blazor UI test navigation on already existing 'Network Tariff Code Search Screen Functionality'"* should produce `recordingKind: Record` + `caseName: "Network Tariff Code Search Screen Functionality"`, not `RecordVerification`.

### UI affordance

The empty placeholder row in `WebUiTestCaseTable.tsx` (and the parent step row inside `EditTestObjectiveDialog`) gains a **"Record this"** button that calls `POST /api/recordings/start` with `kind: Record` + `caseName: <objective name>` + `target: <objective.TargetType>` + `environmentKey: <test set env>`. The button is visible only when:

- `objective.Source == "ImportedFromXray"` AND
- `objective.WebUiSteps.length === 0` OR `objective.WebUiSteps[0].steps.length === 0` (same for DesktopUi)

This gives the QA a one-click path that doesn't require typing a chat prompt or guessing field names. The assistant route remains for chat-driven flows.

## Files to touch

| File | Why |
|---|---|
| `src/AiTestCrew.Agents/Recording/RecordingService.cs` | Two-step lookup in `RecordCaseAsync` web + desktop branches. Replace empty `WebUiSteps[0]` / `DesktopUiSteps[0]`; carry `PostSteps`. Update `Source`. |
| `src/AiTestCrew.Agents/Persistence/TestObjective.cs` | Helper method `IsImportedPlaceholder()` returning true when `Source.StartsWith("ImportedFromXray")` and the active step list is empty. |
| `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` | System-prompt rule for Record-vs-RecordVerification routing on imported objectives. Add a worked example. |
| `src/AiTestCrew.WebApi/Endpoints/RecordingEndpoints.cs` *(or equivalent)* | No signature change; just confirm `POST /api/recordings/start` works when the caller passes only `kind: Record` + `caseName` against an imported placeholder. |
| `ui/src/components/WebUiTestCaseTable.tsx` | "Record this" button on imported empty rows. Wire to `startRecording` via existing `recordings` API. |
| `ui/src/components/DesktopUiTestCaseTable.tsx` | Same affordance for desktop placeholders. |
| `ui/src/components/EditWebUiTestCaseDialog.tsx` *(if dialog shows empty Steps[])* | Optional: add the same "Record this" button inside the dialog for parity. |
| `docs/functional.md` | "Recording into an imported placeholder" subsection under the Xray-import section. Cover the in-place fill, the preserved post-steps, and the new `Source` value. |
| `docs/architecture.md` | Add `ImportedFromXray+Recorded` to the `Source` field state diagram in the rebaseline section. |
| `CLAUDE.md` | Update the extension-map row for "fill an imported placeholder" → points at the new helper + the recording-service branch. |

## Acceptance criteria

1. **Imported placeholder is filled in place.** Given an imported objective with `Source == "ImportedFromXray"` and empty `WebUiSteps[0].Steps`, a `Record` dispatch with matching `caseName` writes the recorded actions into that objective's `WebUiSteps[0].Steps`. No new `recorded-*` objective is created.
2. **Post-steps are preserved.** If `WebUiSteps[0].PostSteps` contains REQ-019-authored entries before the recording, the same list is present afterwards (same count, same content) — verified by reading the test set JSON before and after.
3. **Source field updates.** After the recording, `objective.Source == "ImportedFromXray+Recorded"`. Re-recording the same objective leaves `Source` unchanged (it stays `ImportedFromXray+Recorded`).
4. **Metadata preserved.** `XrayTicketKey`, `XrayObjectiveSlug`, `ParentObjective`, `Preconditions`, `TestDataNotes` survive the recording with byte-identical values.
5. **Fallback path unchanged.** A `Record` dispatch with a `caseName` that does NOT match any imported placeholder creates a `recorded-{slug}` sibling exactly as today — verified by a recording into a test set that has no imported objectives.
6. **Assistant emits the correct kind.** A prompt like "record actions for the imported test case *Network Tariff Code Search Screen Functionality*" produces `confirmRecord` with `recordingKind: Record`, NOT `RecordVerification`. (Manual verification — run the chat against `ChatIntentService` and inspect the emitted JSON.)
7. **Desktop parity.** Same in-place behaviour applies to `DesktopUiSteps` for `Source == "ImportedFromXray"` placeholders with desktop targets.
8. **Re-import idempotency still works.** Re-importing the same Xray ticket after the placeholder has been recorded preserves the recorded `WebUiSteps[0].Steps`; the existing `ApiSteps.Clear() / WebUiSteps.Clear()` block in `XrayImportService.cs:152-156` must be relaxed to skip step lists when `Source.EndsWith("+Recorded")`. Without this, re-import would wipe the recorded work.
9. **UI button is visible only when relevant.** The "Record this" button appears on imported empty placeholder rows and disappears once the recording is saved (because `Source` changes to `…+Recorded`).
10. **One-click flow works.** Clicking "Record this" on a row → recorder launches → user drives → recording saves into the same objective. Browser test by importing BQ-35775 and using only the button (no chat, no CLI).

## Scope — what's out

- **No retro-renaming of existing `recorded-*` siblings.** Users who already have the duplicate-objective state from before this REQ keep it; they can manually merge by copying steps and deleting the duplicate. Out of scope to write a migration.
- **No support for `Source == "Generated"` placeholders.** The Source field state machine row for `Generated → Generated+Recorded` is flagged but explicitly out of v1 — Generated objectives are AI-generated API tests, not UI placeholders, and don't have an empty-steps shape that needs filling.
- **No multi-step UI placeholder filling.** If an imported objective ends up with multiple `WebUiSteps` entries (currently never the case, but theoretical future), only `WebUiSteps[0]` is replaced. The recorder writes one test definition; aligning multiple isn't in scope.
- **No assistant-side "are you sure?" dialog.** If the QA dispatches `Record` against a name that matches an imported placeholder, it fills in-place silently. No double-confirm. The undo path is "delete the objective and re-import".
- **No CLI flag changes.** `--record --case-name "<name>"` already passes through `caseName`; the new lookup happens in `RecordingService`, transparent to the CLI surface.
- **No new agent capability or registration.** Recording agents already advertise `UI_Web_Blazor` / `UI_Web_MVC` / `UI_Desktop_WinForms`; the dispatch routing is unchanged.

## Risks / notes

- **`Source` field divergence.** Introducing `"ImportedFromXray+Recorded"` adds a new string value. Anywhere that does a strict equality check on `Source == "Recorded"` or `Source == "ImportedFromXray"` needs to be updated to `.StartsWith("Recorded")` / `.StartsWith("ImportedFromXray")`. Audit needed in: rebaseline gate in `TestOrchestrator`, any UI badge logic in `*TestCaseTable.tsx`, any persistence migration in `MigrationHelper`. AC#8 is the explicit guard for the most likely breakage (re-import wiping recorded work).
- **Case-name slugification mismatch.** If the QA records with a slightly different case-name than the imported one ("Network Tariff Search" vs "Network Tariff Code Search Screen Functionality"), the slug won't match and the recorder falls back to creating a sibling. Mitigation: the UI "Record this" button passes the exact objective name, so the slug always matches. Chat dispatches rely on the user's typed name, which is best-effort.
- **PostSteps carrying across step replacement.** When `WebUiSteps[0]` is replaced (not appended), the new `WebUiTestDefinition` instance must inherit the old one's `PostSteps` list. Easy to forget — explicit in AC#2.
- **Recording failure semantics.** If the recorder runs but captures zero steps (line 143 of `RecordingService.cs` returns `Fail("No steps were captured…")`), the imported placeholder must NOT be modified. The fill-in-place path runs only after `webRecorded.Steps.Count > 0`. Same guard as today's `recorded-*` creation path.
- **Concurrent re-import + recording.** If a QA triggers re-import while a recording is in flight, the re-import's `Clear()` could wipe the just-recorded steps. AC#8 covers the "after-the-fact" case (re-import after recording is done) but not the in-flight race. Acceptable — the QA controls both actions and would not intentionally race them.

## How this lands with the existing system

REQ-020 is purely additive — it adds a lookup branch before the existing `recorded-*` creation path. Existing recordings, existing test sets, existing tests are unaffected. The new path activates only when:

1. A `Record` dispatch arrives, AND
2. There is an objective with `Source == "ImportedFromXray"`, AND
3. That objective's relevant step list is empty.

If any of those three is false, behaviour is identical to today. This keeps the blast radius small and the rollout safe.

## Demonstration script (for the reviewer)

1. Import BQ-35775 (or any Xray ticket with one Web UI test case).
2. Open the test set page; confirm the objective shows `0 steps` and has a "Record this" button.
3. Click "Record this"; the recorder launches a Blazor browser against the test set's default env.
4. Drive the browser; close it.
5. Verify the objective now shows `N steps` (where N = recorded actions); `Source == "ImportedFromXray+Recorded"`; the "Record this" button is gone; the post-steps panel is unchanged.
6. Re-import BQ-35775; verify the recorded steps survive (AC#8).
7. Repeat with an Xray ticket that maps to `UI_Desktop_WinForms` to cover the desktop branch (AC#7).
