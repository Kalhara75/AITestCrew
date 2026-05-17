---
id: REQ-018
title: Wire per-card accept / merge / rename controls into the Xray import dialog
status: Proposed
created: 2026-05-17
author: Kalhara Samarasinghe
author-note: REQ-017 shipped with AC#13 only partially covered — the import endpoint already accepts `AcceptedObjectiveSlugs`, `MergeRequests`, and `TitleOverrides`, but the dialog never lets the QA edit them. v1 always submits "accept all, no merges, no renames", so the merge / collapse / per-card controls promised in REQ-017 §6 don't actually exist yet. This REQ finishes the UI wiring.
area: ui
related: REQ-017 (Import test cases from Jira Xray) — completes AC#13
---

# REQ-018 — Wire per-card accept / merge / rename controls into the Xray import dialog

## Goal

Make the per-objective controls REQ-017 §6 described actually do something. Specifically, in `ImportFromXrayDialog.tsx` (the dialog reached from a test set's "Import from Xray" button):

1. **Each proposed objective card gets a checkbox.** Unchecking it removes that objective from the import on confirm. Default: all checked.
2. **Each card's title is editable.** The edited string flows through `TitleOverrides[slug]` to the server.
3. **Each card past the first gets a "Merge into above" button.** Clicking it merges the card's fragments into the previous accepted objective (`MergeRequests` entry sent to the server). Already-merged cards collapse visually under their target.
4. **The "Collapse all objectives into one" toggle keeps working** as it does today, and disables the per-card controls while it's on (since the slug-level decisions are moot when everything's collapsed).
5. **The Import button label updates** to reflect the count after un-checks and merges — "Import (2 objectives)" not "Import (3 objectives)" when one has been unchecked.

The server side already exists (`XrayImportConfirmRequest.AcceptedObjectiveSlugs`, `MergeRequests`, `TitleOverrides` — see `src/AiTestCrew.WebApi/Services/XrayImportModels.cs` and the `ConfirmAsync` handling in `XrayImportService.cs:80-90, 320-340`). This REQ is **frontend-only**.

## Why now

REQ-017 §6 commits to per-card controls in the proposed-objective list. The dialog as shipped:

- `ui/src/components/ImportFromXrayDialog.tsx:25` — `const [acceptAll] = useState(true)` (no setter, no checkbox).
- `ui/src/components/ImportFromXrayDialog.tsx:50` — `acceptedObjectiveSlugs: acceptAll ? [] : preview.proposedObjectives.map(o => o.slug)` (the `false` branch is unreachable; it would also be inverted from what the variable name implies — empty array means "accept all" on the server side).
- `ui/src/components/ImportFromXrayDialog.tsx:122-131` — proposed-objective cards are render-only: title, rationale, step-count summary. No checkbox, no title edit, no merge button.
- No `titleOverrides` or `mergeRequests` are ever populated; the dialog always sends `{}` / `[]`.

So a QA today has three choices: accept the whole decomposition, collapse everything into one, or cancel. There's no way to drop the "user without edit permission" scenario from the import, no way to merge two near-identical proposals, and no way to fix a proposal title that the LLM got slightly wrong. They have to import and then delete / edit / re-import — friction REQ-017 was specifically meant to avoid.

This came up the moment the first Xray ticket was imported in dev — the LLM proposed five objectives for a three-scenario ticket, and the QA had no way to merge the duplicates inside the dialog.

## Current behaviour

Open the import dialog, paste a ticket key, click Preview. The proposed-objective list renders as plain cards (`ImportFromXrayDialog.tsx:121-132`):

```tsx
{preview.proposedObjectives.map((obj) => (
  <div key={obj.slug} className="border-b pb-2 last:border-b-0">
    <p className="font-medium text-sm">{obj.title}</p>
    <p className="text-xs text-gray-500">{obj.rationale}</p>
    <p className="text-xs text-gray-400 mt-1">
      {obj.mappingRows.length} step(s): {' '}
      {[...new Set(obj.mappingRows.map(r => r.kind))].join(', ')}
    </p>
  </div>
))}
```

The only interactive control is the "Collapse all objectives into one" checkbox below the list. Click Import and every proposed objective is persisted as-is.

## Desired behaviour

Each card becomes interactive:

```
┌─ Proposed objectives for PROJ-1234 (3) ──────────────────────────────────┐
│ [✓] [Authorised user soft-deletes a Site without NMI         ] 4 steps  │
│     UI placeholder, DB Assert ×3                                         │
│                                                                          │
│ [✓] [User without edit permission cannot soft-delete          ] 1 step   │
│     [ Merge into above ↑ ]    UI placeholder                             │
│                                                                          │
│ [ ] [No delete option in UI when the Site has an NMI          ] 1 step   │
│     [ Merge into above ↑ ]    UNSUPPORTED — will write gap REQ           │
│                                                                          │
│ [ ] Collapse all objectives into one                                     │
└──────────────────────────────────────────────────────────────────────────┘
       Import (1 objective)    [ Back ]    [ Cancel ]
```

Behaviour rules:

1. **Checkbox** unchecks an objective. The button label re-counts, the card visually de-emphasises (e.g. `opacity-60`), and on confirm the slug is omitted from `acceptedObjectiveSlugs`. *Note the server contract: empty array means "accept all", non-empty means "accept exactly these slugs". The component should send the actual non-empty list whenever any card is unchecked OR title-edited OR merged, and `[]` only when the QA hasn't touched anything.*
2. **Title input** is a small inline editable text field, defaulting to `obj.title`. On blur or first edit, the edited value flows into a `titleOverrides: Record<slug, string>` state and ships to the server. Unedited titles do not appear in the dictionary.
3. **Merge into above** is a button on every card after the first. Clicking it appends `{ fromSlug: thisCard.slug, intoSlug: previousAcceptedCard.slug }` to `mergeRequests`. The merged card collapses into the target card's footer ("Merged: <title>"). A small "Undo merge" link reverses it.
   - "Previous accepted card" = the nearest preceding card that is *both* checked *and* not itself merged-into-something. If there is none, the button is disabled with a tooltip "No earlier objective to merge into."
   - Merged cards do not contribute their slug to `acceptedObjectiveSlugs` (the server already drops them — see `XrayImportService.cs` merge handling — but sending them anyway would be redundant).
4. **Collapse-to-single toggle** disables (greys out) every per-card control. The checkbox-state, title overrides, and merge requests are preserved in component state but not sent to the server while collapse is on. Turning collapse off restores the previous per-card state.
5. **Import button** label is computed from the post-edit count:
   - If collapse-to-single is on: `Import (1 objective — collapsed)`.
   - Else: `Import (N objective(s))` where N = checked-and-not-merged card count.
6. **Confirm payload** is unchanged on the wire — same shape as today, just with the user-edited values:
   ```ts
   {
     preview,
     acceptedObjectiveSlugs: explicitListIfTouchedElseEmpty,
     collapseToSingle,
     titleOverrides: { [slug]: editedTitle, ... },
     mergeRequests: [ { fromSlug, intoSlug }, ... ],
   }
   ```

## Files to touch

| File | Why |
|---|---|
| `ui/src/components/ImportFromXrayDialog.tsx` | All UI changes live here. State additions: `acceptedSlugs: Set<string>`, `titleOverrides: Record<string,string>`, `mergeRequests: { fromSlug: string; intoSlug: string }[]`. Replace the render-only card with an interactive one. Compute import-button label from these states. Wire confirm payload to send the actual values. |

That is the whole change. No backend, no DB, no API contract change, no migration. The `XrayImportConfirmRequest` model already carries the three fields and the server-side `ConfirmAsync` already honours them.

## Acceptance criteria

1. Each proposed-objective card has a checkbox; unchecking it removes that objective from the import on confirm.
2. Each proposed-objective card's title is inline-editable; an edited title appears as the persisted `TestObjective.Name` after confirm.
3. Every card after the first has a "Merge into above" button; clicking it visually collapses the card under the previous accepted card and, on confirm, the from-slug's fragments land inside the into-slug's objective.
4. A merged card shows an "Undo merge" affordance that restores the card to its original state.
5. The Import button label reflects the count of accepted, non-merged objectives in real time (or "1 objective — collapsed" when collapse-to-single is on).
6. The "Collapse all objectives into one" toggle visually disables all per-card controls but preserves their state when toggled off and on again.
7. When the QA has *not* touched checkbox / title / merge state, the request omits or empties those fields — so the existing "accept all" behaviour is unchanged for users who don't interact with the new controls (no regression to REQ-017 §6 happy path).
8. The dialog has unit tests covering: (a) uncheck-then-import omits that slug from `acceptedObjectiveSlugs`, (b) title-edit-then-import sends `titleOverrides`, (c) merge-then-import sends a `mergeRequests` entry and excludes the from-slug from `acceptedObjectiveSlugs`, (d) collapse-on hides the per-card state but doesn't lose it.

## Scope — what's out

- **No new server logic.** The server already implements merge + title overrides + slug filtering. This REQ does not touch `XrayImportService.cs`, `XrayImportModels.cs`, or the endpoints.
- **No splitting a proposed objective into more.** REQ-017 explicitly defers this. If the LLM under-fragments, the QA accepts and duplicates via the existing editor.
- **No reordering of objectives.** Merge-into-above is enough for v1.
- **No persistence of partial edits across reopens of the dialog.** If the QA closes the dialog mid-review, their per-card edits are lost. Re-running Preview produces the same proposal (the decomposition is deterministic for a given ticket and capability registry).
- **No multi-target merge** (i.e. merge A into B, then merge C into the now-larger B). v1 supports a single layer — each card either stands, is merged into one specific other card, or is unchecked. If needed later, the server's `MergeRequests` shape is already flexible enough; only the UI gets richer.
- **No JQL / bulk import.** Still single-ticket-per-dialog (REQ-017 scope).

## Risks / notes

- The `acceptAll` flag in the current dialog (`ImportFromXrayDialog.tsx:25`) is *not* a real toggle — there is no setter, the inverse branch is dead code, and the wire semantic is inverted from what the name suggests. The right cleanup is to delete that variable entirely and compute `acceptedObjectiveSlugs` from the new `acceptedSlugs: Set<string>` state. Don't try to preserve the existing variable name.
- The server treats an empty `AcceptedObjectiveSlugs` as "accept all". The component should keep that contract — only send a non-empty array when the QA actually unchecks or merges something. Otherwise unchanged confirms (the default flow) keep their existing wire shape and pass the same code path on the server.
- Reviewer feedback on REQ-017 noted that this gap was the only AC not covered; expect the test bar to be high (cases (a)-(d) in AC#8 are explicit) so the unit-test layer should land alongside the component change rather than after.
