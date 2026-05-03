---
id: REQ-001
title: Standardise test execution look & feel across all test case types
status: Proposed
created: 2026-05-03
author: Kalhara Samarasinghe
area: ui
---

# REQ-001 — Standardised Test Execution UI

## Goal

Unify the visual language of test execution — both **live** (running / queued / awaiting verification) and **historical** (passed / failed / skipped) — across every test case type (API, WebUI Blazor, WebUI MVC, Desktop WinForms, aseXML Generate, aseXML Deliver, Post-Delivery Verification) and both execution modes (Inline and Deferred). The result should feel like a single, coherent product surface rather than features layered on by different hands at different times.

## Why now

Inline-vs-deferred execution was added recently (commits `f78f731`, `2452983`). It works, but it surfaces the same state in three incompatible ways across `StepList.tsx`, `QueueBanner.tsx`, and the post-steps panels — and the broader execution UI has accumulated similar drift across per-type test case tables. Today the user has to look in three places to understand "is this run actually doing anything right now?" and visual cues for the same state differ by which test type they happen to be looking at.

## Current-state findings (from audit)

### 1. Status indication is duplicated and partially shared

- `StatusBadge.tsx` is the only shared status-pill component and is correctly reused in `RunHistoryTable.tsx` and `ExecutionDetailPage.tsx`.
- But step-level status in `StepList.tsx:108-196` uses **hardcoded emoji** (`✅ ❌ ⏳ ⚠️`) instead of the badge.
- `TriggerRunButton.tsx:67-71` and `QueueBanner.tsx:104-122` each maintain **their own palette objects** for the same Queued/Running/AwaitingVerification states.
- "Mode" badge (Reuse/Rebaseline/VerifyOnly) is hand-rolled inline in **both** `RunHistoryTable.tsx:41-43` and `ExecutionDetailPage.tsx:79-83`.

### 2. Deferred-verification state is presented in three incompatible ways

| Surface | Format | File |
|---|---|---|
| Per-step in result view | Cyan chip "in 5m 12s" | `StepList.tsx:225-233` |
| Per-job in global banner | Text "next attempt in ~5 min" | `QueueBanner.tsx:66-72` |
| Per-objective in editor | Static "Deferred" badge with tooltip | `PostStepsPanel.tsx:154-165` |

Same underlying state, three different visual treatments and time-precision rounding.

### 3. Per-test-case-type tables disagree on column conventions

- API / WebUI / Desktop / aseXML-Generate / aseXML-Deliver tables (`TestCaseTable.tsx`, `WebUiTestCaseTable.tsx`, `DesktopUiTestCaseTable.tsx`, `AseXmlTestCaseTable.tsx`, `AseXmlDeliveryTestCaseTable.tsx`) each define columns differently and **none surface execution status in the table itself** — that lives only in result views.
- aseXML delivery has **two parallel verification renderers**: legacy `VerificationsPanel` (inside `AseXmlDeliveryTestCaseTable.tsx:141-420`) and reusable `PostStepsPanel.tsx`. They render the same data with different column layouts and slightly different target-badge palette logic (lines 469-484 vs 525-534).

### 4. Spinners / progress affordances are inconsistent

- `TriggerRunButton.tsx:88` — 16px CSS ring
- `TriggerObjectiveRunButton.tsx:99-104` — 12px CSS ring
- `QueueBanner.tsx` — text-only, no spinner
- `StepList.tsx` — cyan chip with countdown for deferred, no animated indicator for actively-running steps

The user can't tell at a glance whether a run is making progress, idle, or deferred.

### 5. Run History view ↔ Live view are *almost* aligned but diverge on stats

- `RunHistoryTable.tsx:48-51` shows step counts as "Passed / Total" string.
- `ExecutionDetailPage.tsx:92-107` shows the same data as a stats grid.
- Both use `StatusBadge` for the run status itself — that part works.

## Scope — what's in

1. **A single shared design system for execution state**, including:
   - **`StatusBadge`** — extend the existing component to be the *only* way any run/step/job status is rendered. Replace emoji and ad-hoc palettes everywhere.
   - **`ModeBadge`** — for Reuse / Rebaseline / VerifyOnly run mode.
   - **`ExecutionModeBadge`** — for Inline / Deferred (currently `executionModeBadgeStyle()` duplicated in `PostStepsPanel.tsx:459-467` and `AseXmlDeliveryTestCaseTable.tsx:545-553`).
   - **`DeferredCountdownChip`** — single component used by `StepList`, `QueueBanner`, and any future surface. Decide one time-format ("Nm Ns" vs "~N min") and one precision rule.
   - **`RunningIndicator`** — single spinner/pulse component used by all trigger buttons and active-step rows. One size variant scale (sm / md / lg).
   - **`StatsBar`** — replaces both the "Passed / Total" string in history table and the stats grid in detail page. One canonical small-and-large variant.
   - All colours, paddings, and animation timings centralised — no per-file palette objects.

2. **Per-test-case-type tables**: every table (API, WebUI, Desktop, aseXML-Generate, aseXML-Deliver, Post-Delivery-Verification) gets a consistent left-hand status column that reflects the latest known execution state for that case (Pass / Fail / Skipped / Running / AwaitingVerification / Never Run). Today only some result views show status — the case tables themselves are status-blind.

3. **Live execution feedback**: when a run is in flight, the user can tell from any of: trigger button, queue banner, step row, case table — and they all agree. The agreed-upon "language" is:
   - Queued → muted purple chip
   - Running → animated indicator + Running pill
   - AwaitingVerification → cyan ⏳ chip + countdown (precise format consistent everywhere)
   - Pass / Fail / Skipped — final states, no animation

4. **Inline vs Deferred indication**: every step/objective that *can* run deferred should show on its result row whether it actually *did* run inline or deferred (not just on the editor panel). Deferred steps display the countdown; once they fire, they show their final state with a small "Ran deferred" annotation if useful.

5. **aseXML delivery verification consolidation**: collapse the legacy `VerificationsPanel` inside `AseXmlDeliveryTestCaseTable.tsx` to use `PostStepsPanel` directly. One renderer, one set of columns. Pick the better column set from each (the union is "Target, Description, Wait, Mode, Steps + Payload optional").

6. **Run history ↔ detail page alignment**: same `StatsBar` component used in both. Same layout primitives. Run history tile and detail page header should look like the same thing in two sizes.

## Scope — explicitly out

- No backend / SignalR / polling changes. This is a UI cleanup, not a runtime change.
- No changes to data models (`TestObjective`, `PersistedTestSet`, `PersistedExecutionRun`) — visual-layer only.
- No new test case types, agents, or capabilities.
- No theme/dark-mode work unless trivially falls out of centralisation.
- No restructure of routes or page hierarchy.

## Acceptance criteria

A reviewer should be able to verify each of these without ambiguity:

1. **One source of truth per visual concept**: `StatusBadge`, `ModeBadge`, `ExecutionModeBadge`, `DeferredCountdownChip`, `RunningIndicator`, `StatsBar` each exist as a single exported component. Grep for hardcoded `#cffafe`, `#dcfce7`, `#fee2e2`, `#ede9fe`, `#dbeafe` outside the shared component file should return nothing meaningful.
2. **No emoji status icons remain** in `StepList.tsx` — replaced by `StatusBadge`.
3. **Same step running in any of the 6 case-type contexts looks identical** at the row level (table, result view, run detail, history).
4. **Deferred countdown format is byte-identical** in `StepList`, `QueueBanner`, post-steps panel, and any new case-table status column.
5. **Spinner sizes follow the size-prop convention** (sm/md/lg) — no two `animation: spin 0.8s linear infinite` rules in different files with different pixel sizes.
6. **`AseXmlDeliveryTestCaseTable.tsx` no longer contains `VerificationsPanel`** — uses `PostStepsPanel` directly.
7. **Both `RunHistoryTable` and `ExecutionDetailPage`** render their stats via the same `StatsBar` component (in different size variants).
8. **Running an aseXML delivery objective with deferred verification end-to-end** (live + post-mortem in history) shows consistent visual state at every surface throughout the lifecycle: trigger → queued → running → awaiting verification with countdown → final pass/fail.

## Files most likely touched

**New / consolidated:**

- `ui/src/components/StatusBadge.tsx` (extend or split into `ui/src/components/execution/` directory)
- `ui/src/components/execution/` — new directory housing the 6 shared components above

**Refactored to consume the new components:**

- `ui/src/components/StepList.tsx`
- `ui/src/components/TriggerRunButton.tsx`
- `ui/src/components/TriggerObjectiveRunButton.tsx`
- `ui/src/components/QueueBanner.tsx`
- `ui/src/components/PostStepsPanel.tsx`
- `ui/src/components/AseXmlDeliveryTestCaseTable.tsx` (largest change — drop `VerificationsPanel`)
- `ui/src/components/AseXmlTestCaseTable.tsx`
- `ui/src/components/TestCaseTable.tsx` (API)
- `ui/src/components/WebUiTestCaseTable.tsx`
- `ui/src/components/DesktopUiTestCaseTable.tsx`
- `ui/src/components/RunHistoryTable.tsx`
- `ui/src/pages/ExecutionDetailPage.tsx` (or wherever it lives)

## Open questions for the planner

1. **Should the new shared components live in `ui/src/components/execution/` or be flat?** Lean toward a folder — there are 6+ pieces and they conceptually group.
2. **Countdown precision**: pick one — minute-and-seconds ("5m 12s"), or relative ("~5 min"), or both based on time remaining (e.g., precise under 1m, rounded otherwise). The audit found both extant. Recommend: precise under 2 minutes, rounded above.
3. **Should case-type tables show status for the *latest* run of each case, or status across *all* runs aggregated**? Today they show neither. Pick one and apply uniformly. Recommend: latest run, with a hover tooltip on count of historical runs.
4. **Does "Ran inline" need an explicit indicator on result rows, or is its absence (no countdown chip) enough**? Recommend: only mark deferred explicitly; inline is the default and unmarked.
5. **Should `RunningIndicator` use a CSS spin or a Tailwind `animate-pulse`-style approach**? Whatever is consistent with the rest of the app. Audit notes existing CSS rings.
6. **Storybook / visual regression**: out of scope unless one already exists. (None found in audit.)
7. **What about non-execution surfaces that show status (e.g., dashboard tiles, AuthHealthPanel, AgentsPanel)?** Audit didn't find disagreement here, but the planner should sanity-check whether `AgentsPanel.tsx`'s status-dot should also adopt `StatusBadge`. Recommend: out of scope unless trivial.
