---
id: REQ-003
title: UI text capture — bind a UI element's text/value to a {{Token}} for downstream steps
status: Proposed
created: 2026-05-09
author: Kalhara Samarasinghe
area: agents + ui
---

# REQ-003 — UI text capture (`capture-text` for desktop and web)

## Goal

Let a UI step capture an element's visible text (or, for web, an attribute value) and bind it to a `{{Token}}` that flows to:

1. **Later steps within the same UI test case** — e.g. step 3 captures the new invoice ID from a "Created" toast, step 7 enters that ID into a search field.
2. **Sibling post-steps under the same parent** — e.g. a Windows UI test case captures `InvoiceId`, and a sibling DB-check post-step uses `{{InvoiceId}}` in its `WHERE` clause.
3. **Deferred siblings** — the captured value round-trips through `DeferredVerificationRequest.CapturedTokens` (the same path REQ-002 added for DB captures), so a deferred sibling firing minutes later still receives the value.

This closes the gap left after REQ-002: the DB Assert step can capture from a database row, but there's no equivalent on the UI side. Today, if a freshly-created ID is only visible on the WinForms screen and you need it downstream, you have to either round-trip through the DB (write a query that finds the row by some other context AND captures the ID) or hardcode the value — which only works for one-shot manual runs, not repeatable test cases.

The DB-side workaround is fine in 95% of cases (the DB always has the canonical ID under some filter that's already in scope: customer NMI, MessageID, or `ORDER BY CreatedAt DESC`). REQ-003 covers the remaining 5%: scenarios where the ID is **only** visible on screen (e.g. a generated reference number, a session-scoped GUID the UI shows but doesn't write straight to a queryable table, or a confirmation toast you want to assert and then propagate verbatim).

## Why now

- REQ-002 added the carrier (`Metadata["capturedTokens"]` → `PostStepOrchestrator` merge → `DeferredVerificationRequest.CapturedTokens`). The runtime path is solved; this REQ only adds new producer surfaces that emit into it.
- The existing `assert-text` action already does the hard work — UIA `FromPoint` + selector resolution + OCR fallback for owner-drawn grids. A `capture-text` action reuses the same text-extraction code path; the only new code is the bind-to-token step.
- Adding it now (while the capture-token plumbing is fresh) is cheaper than retrofitting later when we've forgotten the merge precedence rules.

## Current-state findings (from audit — do NOT redo these)

✅ **Already in place — extend, don't rebuild:**

| Component | Path | Status |
|---|---|---|
| Desktop UI step model | `src/AiTestCrew.Storage/Shared/DesktopUiTestCase.cs:36-177` | `DesktopUiStep` with `Action`, selectors, `Value`, OCR region — extend with `As`, `Required`, `Attribute`, `Transform`. |
| Desktop step executor | `src/AiTestCrew.Agents/DesktopUiBase/DesktopStepExecutor.cs:53-128` | Action switch — add `capture-text` + `capture-text-ocr` cases. |
| Desktop text extraction | `src/AiTestCrew.Agents/DesktopUiBase/DesktopStepExecutor.cs:527-600` (`ExecuteAssertText`) | `GetElementText`, `FromPoint`, `QuickFindElement` — reuse. |
| Desktop OCR fallback | `src/AiTestCrew.Agents/DesktopUiBase/DesktopStepExecutor.cs:745+` (`ExecuteAssertTextOcr`) | `WindowsOcrService` — reuse for `capture-text-ocr` and as a fallback within `capture-text`. |
| Desktop recorder | `src/AiTestCrew.Agents/DesktopUiBase/DesktopRecorder.cs` | Hotkey wiring already present for assert-* (Ctrl+Shift+\* keys) — add a `capture` hotkey. |
| Web UI step model | `src/AiTestCrew.Storage/Shared/WebUiTestCase.cs:40-85` | `WebUiStep` with `Action`, `Selector`, `Value` — extend with `As`, `Required`, `Attribute`, `Transform`. |
| Web step executor (Bravo Web) | `src/AiTestCrew.Agents/WebUiBase/WebUiStepExecutor.cs` (or per-stack equivalent) | Add `capture-text` action; `Locator.TextContentAsync` / `Locator.GetAttributeAsync`. |
| Captured tokens runtime path | `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs` (merge), `src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs` (round-trip) | Wired by REQ-002 for DB captures — UI captures emit into the same dictionary. |
| Token substitution within steps | `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` | Already substitutes `{{Token}}` in step values and selectors — extend the per-test-case loop to merge captures from earlier steps before substituting later ones. |

❌ **Gaps this requirement addresses:**

1. **No UI-side capture primitive.** Desktop and web step executors only support `assert-*` actions for reading text. There is no action that *binds* a value to a token.
2. **Within-case context isn't built.** When a UI test case runs, the substituter applies env params + parent context once, then runs every step against that frozen dict. There's no mid-case dict update — so even if a step *did* capture something, later steps in the same case wouldn't see it.
3. **Recorder has no capture hotkey.** Recorder maps Ctrl+Shift+T (assert-text), Ctrl+Shift+V (assert-visible), etc. — nothing for capture.
4. **Editor dialogs don't list a capture action.** `EditDesktopUiTestCaseDialog.tsx` and `EditWebUiTestCaseDialog.tsx` action dropdowns enumerate only the existing actions.
5. **Sibling deferral path doesn't include UI captures yet.** REQ-002 only touched the DB agent's emit point. The desktop/web agents need to copy `Metadata["capturedTokens"]` onto their `TestStep` outputs the same way.

## Scope — what's in

### 1. New step actions (desktop + web)

**Desktop (`DesktopUiStep`):**

- `capture-text` — read the element's UIA-visible text (`Name`, `ValuePattern`, then OCR fallback if both empty AND coords are present, mirroring `assert-text`). Bind to `{{<As>}}`.
- `capture-text-ocr` — force OCR over the recorded region (no UIA attempt). Use when UIA is known to fail (Bravo's owner-drawn grid cells).

**Web (`WebUiStep`):**

- `capture-text` — `Locator.TextContentAsync()` (Playwright). Trimmed.
- `capture-attribute` — `Locator.GetAttributeAsync(attribute)` for `value` / `data-id` / `href` / etc. The `Attribute` field selects which.

### 2. New step fields (carriers)

On both `DesktopUiStep` and `WebUiStep`:

- `As` (string, required for capture-* actions) — token name to bind, e.g. `"InvoiceId"` (no braces).
- `Required` (bool, default `true`) — if true and the read returns null/empty, the step **fails** with a typed reason. If false, the token is left undefined (downstream `{{InvoiceId}}` survives as a literal and is logged as WARN by the existing `unknownTokens` collector).
- `Transform` (string, optional) — a regex with one capturing group; if set, the captured value is `Regex.Match(rawText, transform).Groups[1].Value`. Useful for "extract just the digits from `Invoice #12345 created`". Failure to match → step fails (treats as `Required: true` violation regardless of the flag, because the user explicitly asked for a transform).
- Web only: `Attribute` (string, optional) — for `capture-attribute`, which attribute to read.

These fields are additive; old steps without them deserialise as `As=null`, which is rejected by the executor only if `Action == "capture-*"`.

### 3. Within-case token propagation

The desktop and web step executors must build a per-case mutable context dict:

```
caseContext = new Dictionary<string,string>(parentContext, OrdinalIgnoreCase);
foreach step in steps:
    substitute step against caseContext
    execute step
    if step is capture-* and succeeded:
        caseContext[step.As] = capturedValue
```

This means **the substituter is invoked per-step inside the agent loop**, not once at the top of the case. Today the substituter is called by `StepParameterSubstituter.Apply(DesktopUiTestDefinition, ...)` once before execution begins; the loop becomes per-step.

**Precedence:** within-case captures > parent context > env params (matches REQ-002's "captured > parent > env" rule).

### 4. Sibling propagation (mirrors REQ-002's DB path)

After the UI test case completes, the agent attaches the merged `caseContext` minus the parent context (i.e. just what *this case* captured) to `TestStep.Metadata["capturedTokens"]`. `PostStepOrchestrator` already knows how to merge that into siblings (REQ-002 wired it). No orchestrator change needed.

### 5. Recorder hotkey

- Desktop: **Ctrl+Shift+C** (currently unbound — verify against `DesktopRecorder.cs` hotkey table). Pressing it over an element emits a `capture-text` step with selectors auto-populated and `As` set to a default name (`Captured1`, `Captured2`, …). The user is expected to rename `As` in the editor afterwards.
- Web: same chord; Bravo / Blazor recorders both add the binding.

A short modal (or a status-bar prompt) at hotkey-press time *could* prompt the user for the `As` name immediately. Out of scope for v1 — auto-name and let them rename. **Open question for the planner.**

### 6. Editor integration

`EditDesktopUiTestCaseDialog.tsx` and `EditWebUiTestCaseDialog.tsx`:

- Add `capture-text` (and `capture-text-ocr` for desktop, `capture-attribute` for web) to the action dropdown.
- When a capture-* action is selected, show: `As` text input (required), `Required` checkbox, `Transform` text input (optional, with a help tooltip showing a worked regex example), and (web only) `Attribute` text input (defaults `value`).
- Hide the `Value` field for capture actions (it's not used).

No autocomplete for downstream `{{Token}}` references in *later* steps' `Value` / `Selector` fields — REQ-002 explicitly punted on that for the DB editor (free-text + highlight). REQ-003 keeps the same default; users type the token name and the existing `highlightTokensStr` shows it.

### 7. Documentation

- `docs/architecture.md` — extend the "DB Assert Step" section's capture-token sub-section to be agent-agnostic (it's no longer DB-specific). Document the per-step in-loop substitution change in desktop/web agents.
- `docs/functional.md` — new "Capturing UI values" section under the existing UI authoring sections. Worked example: WinForms invoice creation captures `InvoiceId` for a downstream DB check.
- `docs/file-map.md` — additive only.
- `CLAUDE.md` "Where to extend — quick map":
  - "A new step action that captures a value into a `{{Token}}` → enum + executor branch + editor dropdown" — single new row covering the pattern.

### 8. Tests

- **Unit** — `DesktopUiStep` / `WebUiStep` JSON round-trip with the new fields.
- **Unit** — `StepParameterSubstituter` per-step in-loop substitution: step #2 captures `X`, step #4's `Value="{{X}}"` substitutes to the captured value.
- **Integration** — desktop: launch a known WinForms harness app (use whatever is already in `tests/` or rig a small XAML window) → `capture-text` over a label → assert the captured value flows. Skip cleanly when no display is available (CI without RDP).
- **Integration** — web: a Playwright spin-up against a static HTML page → `capture-text` + `capture-attribute` → assert the captured value flows. Reuse Testcontainers' fixture pattern from REQ-002 if a similar harness exists.
- **Capture round-trip** — extend REQ-002's `DbCaptureRoundTripTests` with a sibling-direction case: a desktop capture feeds a DB-check post-step. Confirms the bidirectional plumbing (UI-emit → orchestrator-merge → DB-substitute) works.

## Scope — explicitly out

- **Multi-element capture** (capturing a list of values, e.g. all rows in a grid). Future REQ.
- **Capturing structured data** (entire row → object → field-by-field). Future REQ.
- **Image-based capture** (region screenshot → store as base64 token). Future REQ.
- **Token autocomplete in downstream step editors.** REQ-002 punted on this for the DB editor; REQ-003 holds the same line. Free-text + `highlightTokensStr` only.
- **Chat-assistant NL authoring of capture steps.** Recording + manual editor only. The chat-assistant pattern for editing existing UI steps isn't established yet; punt to a future REQ when the broader chat-edits-UI-cases story is scoped.
- **`Required:false` semantics across reruns.** If a capture is missing on attempt 1 and present on attempt 2 (a flaky element), should the late capture overwrite the un-captured token? Recommend YES — same precedence as REQ-002's deferred-retry roll-forward. Lock in implementation but don't add a switch.
- **Substituting `As`** itself (the capture target). NOT substituted, same as REQ-002's `Captures.As` rule. Substituting it would let parent context redirect captures unexpectedly.

## Acceptance criteria

A reviewer should be able to verify each of these without ambiguity:

1. **Within-case capture (desktop).** A test case with steps `[..., capture-text → As="Foo", ..., fill → Value="{{Foo}}"]` runs end-to-end with the captured text typed into the later fill step. Verified by an integration test that asserts the recipient field's content.
2. **Within-case capture (web).** Same as #1 but using a Playwright-driven web case.
3. **Sibling capture flowing to DB.** A desktop UI test case captures `InvoiceId`; a sibling DB post-step on the same objective uses `WHERE InvoiceId = {{InvoiceId}}` and finds the row. Verified by an integration test combining REQ-002's DB-check infrastructure with a desktop UI case.
4. **Deferred sibling.** Same as #3 but with `WaitBeforeSeconds > defer threshold` — the captured value round-trips through `DeferredVerificationRequest.CapturedTokens` and arrives at the deferred DB check.
5. **OCR fallback (desktop).** A `capture-text` step over an owner-drawn cell where UIA returns empty falls through to OCR and captures the OCR'd text.
6. **`capture-text-ocr` (forced OCR).** Bypasses UIA entirely; uses the recorded OCR region.
7. **`capture-attribute` (web).** With `Attribute="value"` over a populated `<input>`, captures `"Hello"` when the input value is `"Hello"`.
8. **`Transform` regex.** A capture with `Transform="Invoice #(\d+) created"` against text `"Invoice #12345 created successfully"` binds `12345`.
9. **`Required: true` + missing value.** The step fails with a typed reason naming the selector and the action.
10. **`Required: false` + missing value.** The step passes; downstream `{{Foo}}` survives as a literal; the run log records a WARN via the existing `unknownTokens` collector.
11. **Recorder hotkey (desktop).** Ctrl+Shift+C over an element produces a `capture-text` step in the recorded JSON with selectors populated and `As="Captured<N>"`.
12. **Editor.** The action dropdown in `EditDesktopUiTestCaseDialog` and `EditWebUiTestCaseDialog` includes the new capture actions; selecting one shows the `As` / `Required` / `Transform` (and `Attribute` for web) fields and hides `Value`.
13. **Documentation.** `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, `CLAUDE.md` updated.

## Files most likely touched

**Backend — modified:**

- `src/AiTestCrew.Storage/Shared/DesktopUiTestCase.cs` — add `As`, `Required`, `Transform` to `DesktopUiStep`; document new actions in the `Action` XML doc.
- `src/AiTestCrew.Storage/Shared/WebUiTestCase.cs` — add `As`, `Required`, `Attribute`, `Transform` to `WebUiStep`; document new actions.
- `src/AiTestCrew.Agents/DesktopUiBase/DesktopStepExecutor.cs` — `capture-text` + `capture-text-ocr` cases; in-loop substitution; emit `Metadata["capturedTokens"]` on the case's `TestStep`.
- `src/AiTestCrew.Agents/DesktopUiBase/DesktopRecorder.cs` — Ctrl+Shift+C hotkey + auto-naming.
- `src/AiTestCrew.Agents/WebUiBase/*` (per-stack: Bravo Web, Blazor Cloud, possibly MVC) — `capture-text` + `capture-attribute` cases; same in-loop substitution + Metadata emission.
- `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` — small helper for "substitute one step against this dict" (the per-step variant). The existing whole-case `Apply` overloads stay for callers that want frozen-context behaviour (parent steps, etc.) but UI agents call the per-step helper.

**Frontend — modified:**

- `ui/src/types/index.ts` — extend `DesktopUiStep` and `WebUiStep` types.
- `ui/src/components/EditDesktopUiTestCaseDialog.tsx` — action dropdown + conditional fields.
- `ui/src/components/EditWebUiTestCaseDialog.tsx` — action dropdown + conditional fields.

**Tests — new:**

- `tests/AiTestCrew.Agents.Tests/DesktopUiBase/CaptureTextTests.cs` (within-case + sibling propagation)
- `tests/AiTestCrew.Agents.Tests/WebUiBase/CaptureTextTests.cs`
- Extend `tests/AiTestCrew.Agents.Tests/Environment/StepParameterSubstituterTests.cs` with the per-step in-loop case.
- Extend REQ-002's `DbCaptureRoundTripTests` (or add a new file) with a UI-source → DB-sink case.

**Docs:**

- `docs/architecture.md`, `docs/functional.md`, `docs/file-map.md`, `CLAUDE.md` per §7.

## Open questions for the planner

1. **Recorder hotkey naming prompt** — auto-name `Captured<N>` and rely on the user to rename in the editor (recommended; smallest scope), or pop a tiny win32 input modal at hotkey-press time? If the latter, how does it interact with the recorder's window-focus logic?
2. **Recorder hotkey collision** — Ctrl+Shift+C is the recommended chord. Verify against the existing hotkey map; if it collides (it's a common copy variant), pick another (Ctrl+Shift+X or F8).
3. **`Transform` syntax** — locked-in regex in this requirement. Worth adding `trim` / `lower` / `upper` as named transforms instead? Recommend NO — regex covers all of these (`^\s*(.*?)\s*$` for trim) and keeps the surface tiny.
4. **OCR-vs-UIA precedence in `capture-text`** — recommend "UIA first, OCR fallback when (a) UIA returned empty AND (b) coords are present". Same as `assert-text`. Confirm.
5. **Per-step substitution performance** — substituting every step against a fresh dict on each iteration is O(n × dict-size). Test cases are small (typically <20 steps, dicts <10 keys), so this is fine. Note for the planner: don't optimise prematurely.
6. **Deferred path for UI captures** — REQ-002's UI agents don't currently emit `Metadata["capturedTokens"]` (only DbCheckAgent does). Verify that `PostStepOrchestrator`'s merge logic doesn't depend on the source agent — it should just look at `step.Metadata["capturedTokens"]` regardless of who set it. Confirm in code.
7. **Within-case captures and sibling captures — same pool?** Recommendation: YES. After the UI case completes, ALL `capture-*` results (every `As → value` pair) are emitted to siblings via `Metadata["capturedTokens"]`. Within-case context is just a moving snapshot of that pool. Simpler model, no edge cases around "which captures are private to the case".
8. **Cross-browser web `capture-text`** — Bravo Web (Playwright/Chromium), Blazor Cloud (Playwright/Chromium), MVC (Playwright/Chromium). Same code path; no per-stack divergence expected. Confirm if MVC uses a different driver.
9. **`/add-ui-capture` slash command?** Out of scope per §"Scope — explicitly out" (no chat-assistant authoring), but a tiny scaffolder skill that takes `<moduleId> <testSetId> <objectiveId> <stepIndex> "<NL — what to capture>"` and inserts a `capture-text` step might be valuable parity with `/add-db-assert`. Recommend punting until users ask.
