# AITestCrew — Recording Troubleshooting Guide

This guide captures hard-won lessons from real recording defects in **Bravo Web (Kendo UI / jQuery)**, **Bravo Cloud (Blazor / MudBlazor)**, and **Bravo Desktop (WinForms / UIA)**. Consult this before modifying `PlaywrightRecorder.cs` / `DesktopRecorder.cs` or debugging a broken recording.

For deep dives:
- Web — `.claude/commands/bravo-web-reference.md` / `blazor-cloud-reference.md`
- Desktop — `.claude/commands/desktop-winui-reference.md`

---

## Desktop (WinForms / UIA) — quick diagnosis

The desktop recorder and replay engine use a **coord-first strategy**: every click captures the pixel relative to the process's largest visible window, and replay clicks that exact pixel via `Mouse.Click`. UIA element lookup exists but is only a readiness probe (waits for the right element to appear under the pixel before clicking). The recorder also auto-captures **inter-step delay** (`DelayBeforeMs`) so your recording pace is reproduced at replay.

### Why this design

Legacy WinForms apps (especially those using Infragistics / DevExpress or custom-drawn controls) **don't fully expose themselves to UI Automation**:

- Ribbon buttons often don't exist as individual `Button` elements — only the parent `ToolBar` is visible to UIA.
- `CheckedComboBox` popup items render on screen but aren't in the UIA tree at all (ControlView *or* RawView).
- Disabled controls are skipped by `FromPoint` hit-testing; UIA returns the parent instead of the disabled child.

WinForms still processes raw mouse events at the pixel level, though — so a `Mouse.Click` at the right coordinates fires the underlying control's handler regardless of UIA. That's why the replay is coord-first.

### Desktop symptoms → root cause

| Symptom | Likely root cause | Fix |
|---|---|---|
| Replay clicks ribbon button but nothing happens | UIA only sees the `ToolBar` parent; clicking the ToolBar centre is a no-op | Confirm `Coords=(X,Y)` is non-null in the log. If yes, coord-first path should work. If no, re-record — the old recording predates coord capture |
| `Coords=(null,null)` on every click except the first | Recorder using stale `mainWindow` reference (captured at Login window, dead after login transition) | Recorder now uses `FindLargestVisibleWindow(processId)` refreshed per click — verify this in `DesktopRecorder.cs` |
| Coords wildly large (e.g. X=5080 on a sensible window) | Reference window was a tiny helper at (0,0) (e.g. `Process.MainWindowHandle` picked the wrong one) | Ensure `FindLargestVisibleWindow` filters by `IsWindowVisible` + largest area of the target process |
| Coords relative to a popup, not main window | Reference was `GetForegroundWindow()` — returned the popup | Never use foreground window; always `FindLargestVisibleWindow` |
| `(Select All)` / combo popup checkbox "Element not found" | Popup items not in UIA tree at all | Coord-first path handles this via `Mouse.Click` at the recorded pixel |
| Recorded step shows `Name='Invoice Actions'` / `ControlType='ToolBar'` when user intended a ribbon button | Button was disabled at click time, or click missed the icon by a few pixels — UIA returned the container | `RefineContainerHit` in the recorder walks descendants for actionable children whose `BoundingRectangle` contains the click pixel. Re-record clicking squarely on the icon if refinement can't find it (control genuinely not in tree) |
| Replay fires clicks faster than recording — button clicked before search returns | Inter-step delay not preserved | Recorder stores `DelayBeforeMs` per step (delta from previous step). Executor sleeps that long before each step, capped at 30,000 ms. Null on old recordings |
| New `DesktopUiStep` field disappears between agent and DB | Docker-hosted WebApi is older build; `System.Text.Json` silently drops unknown fields | Rebuild Docker image (`docker compose build && up -d`) whenever `DesktopUiStep` schema changes. See `desktop-winui-reference.md` "Changing the schema" checklist |

### Desktop diagnostic flow

1. **Look at the `[DesktopStepExecutor] click step — Name='X' Aid='Y' Coords=(N,M)` log line** for the failing step.
   - `Coords=(null,null)` → recorder didn't capture. Either legacy recording or `FindLargestVisibleWindow` returned 0. Re-record.
   - `Coords=(huge,...)` → wrong reference window. Check for regressions.
2. **Look at the `FromPoint(X,Y) hit Ct='C' Name='N' (recorded Name='R') match=yes/no` line.**
   - `match=yes` → right element under the pixel. If the click doesn't fire the expected action, the recorded `ControlType` is likely a container (`ToolBar`/`Pane`) — re-record clicking squarely on the intended child.
   - `match=no` → upstream state is wrong: a menu didn't open or a search didn't complete. Usually a missing / too-short `DelayBeforeMs` on the preceding step, or a recorded step that hit a container instead of a trigger button.
3. **Verify recording quality in the UI editor**: every click has `Coords`, `ControlType` is a specific actionable type (`Button`/`Hyperlink`/`MenuItem`/`CheckBox` — not `ToolBar`/`Pane`), `DelayBeforeMs` reflects reality.

---

## Web recording (Kendo / MudBlazor) — see sections below.

---

## Symptoms → Root Cause Map

| Symptom | Likely root cause | Fix direction |
|---|---|---|
| Step records but `value` column is empty (e.g. `click li[role="option"]` with no text) | Selector picks a non-distinctive ARIA role before falling through to text | Skip the role in `bestSelector` priority 9 or scope to popup + use text |
| Replay clicks the wrong option / wrong date cell / wrong row | Selector is globally non-unique — matches elements in sibling widgets or hidden popups | Add popup scope prefix (`#Endpoint_listbox >> …`, `#CreatedFromDate_dateview >> …`) |
| Popup opens during recording but no step is saved | Click target is a weak tag (`span`, `div`) without stable attributes — recorder drops it | Walker must recognise `[aria-controls]` ancestors; `bestSelector` must check `aria-controls` uniqueness |
| Date / time commit produces no `fill` step | Kendo fires `change` only through jQuery (`$(el).trigger('change')`) — native `document.addEventListener('change', …, true)` never sees it | Attach a jQuery-delegated listener for `input[data-role*="picker"]` |
| Fill value is out of order with the next click | `change` event fires on blur AFTER the subsequent `click`; blur/change/click ordering is unreliable in Chromium | Use `input` event (per keystroke) for fills, not `change` |
| Menu search filter doesn't trigger during replay | `FillAsync` dispatches `input`/`change` but not `keyup` — jQuery keyup handlers miss it | Replay code dispatches `keyup` after `fill` |
| Kendo Grid hyperlink selector never matches on replay | Grid `<a>` hrefs are placeholders at page load; `mousedown` rewrites them just before click | Skip href-based selection inside `.k-grid`; use text |
| WELCOME modal blocks every click after login | Kendo Window with `.k-overlay` backdrop | Replay auto-dismisses via Escape → close button → JS removal |

---

## Diagnostic Playbook (use this order — fastest path to root cause)

### 1. Inspect the live DOM before guessing

Don't assume Kendo widget structure from memory. The bundled Playwright node can run a one-off inspection script against the real page:

```bash
# Example from the Delivery Search defect diagnosis
./src/AiTestCrew.Runner/bin/Debug/net8.0-windows/.playwright/node/win32_x64/node.exe ./tmp-inspect.mjs
```

Minimal template:

```js
import { chromium } from './src/AiTestCrew.Runner/bin/Debug/net8.0-windows/.playwright/package/index.mjs';
import path from 'path';

const storageState = path.join(
  'C:/MyCode/github/AITestCrew/src/AiTestCrew.Runner/bin/Debug/net8.0-windows',
  'legacy-auth-state.sumo.json'  // or bravecloud-auth-state.sumo.json for Blazor
);

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ storageState, viewport: { width: 1600, height: 1000 } });
const page = await context.newPage();
await page.goto('https://sumodev.braveenergy.com.au/Bes.WEB.MIL/MILDeliverySearch?m=Y',
                { waitUntil: 'networkidle', timeout: 45000 });
await page.keyboard.press('Escape').catch(() => {});

// Dump the DOM around the broken widget
const info = await page.evaluate(() => {
  const el = document.querySelector('input[data-role="datetimepicker"]');
  return { outer: el?.outerHTML?.slice(0, 500), parentChain: /* walk up */ [] };
});
console.log(JSON.stringify(info, null, 2));

await browser.close();
```

Delete the temp script when done.

### 2. If a commit produces no step → instrument events

Attach listeners in **both** native capture/bubble phases AND via jQuery, drive the real UI with Playwright clicks (not JS API calls — those skip user-event paths), then observe which event actually carries the value:

```js
await page.evaluate(() => {
  window.__events = [];
  ['change', 'input', 'keyup', 'blur', 'focusout'].forEach(name => {
    document.addEventListener(name, (e) => window.__events.push({
      type: `${e.type}@capture`, id: e.target.id, value: e.target.value, isTrusted: e.isTrusted
    }), true);
    document.addEventListener(name, (e) => window.__events.push({
      type: `${e.type}@bubble`, id: e.target.id, value: e.target.value
    }), false);
  });
  if (window.jQuery) {
    jQuery(document).on('change.__test', '#TargetInputId', function() {
      window.__events.push({ type: 'change@jQuery', id: this.id, value: this.value });
    });
  }
});

// ... drive the real UI flow with page.click, then dump window.__events
```

If **only** `change@jQuery` carries the value, Kendo is firing the event through jQuery's internal `trigger()` call. Native `document.addEventListener('change', ...)` will never fire for that widget — you need a jQuery-delegated listener in the recorder.

### 3. If a selector is non-unique → count matches on the live page

```js
const count = await page.locator(selector).count();
// Must be 1 for reliable replay
```

If `count > 1`, scope to the owning widget's stable container id:

| Widget | Popup container | Scope prefix example |
|---|---|---|
| Kendo ComboBox / DropDownList | `<ul id="X_listbox" role="listbox">` | `#Endpoint_listbox >> text="Gateway SPARQ"` |
| Kendo DatePicker / DateTimePicker | `<div class="k-animation-container" id="X_dateview">` | `#CreatedFromDate_dateview >> a[title="Thursday, 1 January 2026"]` |
| Kendo Window | The `.k-window` has a dynamic id, but the title span has `id="X_wnd_title"` | Match by title text: `[role="dialog"]:has(.k-window-title:has-text("WELCOME"))` |

### 4. Always verify the fixed selector before declaring done

Run `page.locator(newSelector).count()` on the live page — expect `1`. Don't trust the recorder output on its own; the recorder's JS runs in a different execution context than Playwright's Locator resolution (attribute escaping, visibility rules, frame scoping).

### 5. Validate replay symmetry for Kendo widgets

| Widget | Replay via | Notes |
|---|---|---|
| DateTimePicker | `fill '#CreatedFromDate' = "1/01/2026 12:00 AM"` | Kendo parses the text; updates both input.value AND the widget's Date model |
| ComboBox | Click the option via scoped text selector | Hidden `<input id="X">` has `display:none` so `fill` fails. Either fill the visible `input[name="X_input"]` or click the option |
| PanelBar group header | `click text="Market Integration"` | Group headers are `<span>` — use text, never `href` |

---

## Recorder Selector Priority (current, as of 2026-04)

`bestSelector(el)` tries these in order and returns the first match:

| Priority | Match | Example |
|---|---|---|
| 1 | Stable `id` (skip dynamic: `_active`, `_pb_`, UUIDs, `\d{4,}`) | `#btnSearch` |
| 2 | `name` attribute | `input[name="Endpoint_input"]` |
| 3 | `type="submit"` or typed `input` (not `button`) | `input[type="submit"]` |
| 4a | **`aria-controls`** if globally unique | `span[aria-controls="Endpoint_listbox"]` |
| 4b | `aria-label` (≤60 chars, skip `"Toggle ..."`) | `button[aria-label="Notifications"]` |
| 5 | `title` attribute | `li[title="NMI Search"]` |
| 6 | Link `href` (contains match, skip inside `.k-grid`) | `a[href*="/Bes.WEB.SDR/NMISearch"]` |
| 7 | MudBlazor child text (`.mud-nav-link-text`, `.mud-button-label`) | `text="Notifications"` |
| 8 | `data-*` attributes (id, name, value, key, feature, role) | `a[data-value="2026/0/1"]` |
| 9 | ARIA `role` — **skip** `menuitem`, `menu`, `group`, `option` | `a[role="button"]` |
| 10 | First distinctive class (skip state/utility classes, require uniqueness) | `div.search-result-row` |
| 11 | `text="ownText"` (direct text children only) | `text="Gateway SPARQ (GatewaySPARQ)"` |
| 12 | `text="innerText"` (≤50 chars, no newlines) | `text="Standing Data"` |
| 13 | Grid row context (`tr:has-text("X") >> td[data-label="Y"] >> button >> nth=N`) | — |
| 13b | SVG icon fingerprint (`__icon__<path-prefix>\|<nth>`) | Replayed via `click-icon` action |

**Post-processing — Kendo listbox scope wrapper:**
If the element lives inside `ul[id][role="listbox"]` or `.k-list-container[id]` / `.k-popup[id][data-role="popup"]`, the returned selector is prefixed with `#<containerId> >> `. Skipped when the raw selector is already an id (`#id`), an icon sentinel (`__icon__…`), or already chained (`>>`).

---

## Event Listeners (current)

| Event | Listener | Records as |
|---|---|---|
| `input` on any form field | native, capture | `fill` with current value (dedup updates last) |
| `change` on `<select>` | native, capture | `fill` |
| `change` on `input[data-role="(date\|time\|datetime)picker"]` | **jQuery delegated** (polled) | `fill` with the formatted date text |
| `click` (non-form fields) | native, capture | `click` with best selector |
| `click` inside `.k-animation-container[id$="_dateview"]` or `[id$="_timeview"]`, or inside `.k-calendar`/`.k-calendar-container` | **suppressed** | — (the jQuery picker listener will record the commit) |
| `keydown` Escape | native, capture | `press Escape` |

---

## Click Walker

Walks up from the click target to the nearest meaningful interactive element. Stops at:
- `<button>` or `<input>`
- `<a>` with a real `href` (not `#`, not `javascript:`)
- Any element with `[aria-controls]` (Kendo widget triggers — inner chevron icons resolve to their controlling span)

Falls back to the original target if no interactive ancestor is found.

---

## Kendo-Specific Quick Reference

| Widget | Stable id pattern | Commit event | Recorder strategy |
|---|---|---|---|
| DateTimePicker | `input#X` + popup `div#X_dateview` | `change@jQuery` on input | Suppress popup clicks; jQuery listener records final `fill` |
| DatePicker | same as above, with `X_dateview` popup | `change@jQuery` | same |
| TimePicker | popup is `X_timeview` | `change@jQuery` | same |
| ComboBox | hidden `input#X` (display:none) + visible `input[name="X_input"]` + `ul#X_listbox` popup | `change@jQuery` on hidden input | Record option click as `#X_listbox >> text="..."`; trigger click as `span[aria-controls="X_listbox"]` |
| DropDownList | same shape as ComboBox | same | same |
| PanelBar | `<ul id="featureMenu">` with nested `<span class="k-link k-header">` groups | N/A (no commit event) | `bestSelectorForPanelBarHeader` resolves header clicks to `text="GroupName"` |
| Window (modal) | `<div class="k-window">` with `<span id="X_wnd_title">` | N/A | Dismiss via Escape (recorded as `press Escape`) |
| Grid | `<div class="k-grid">` with dynamic row hrefs | N/A | Skip href-based selection inside `.k-grid`; use visible cell text |

---

## When In Doubt

1. Read `.claude/commands/bravo-web-reference.md` (Bravo Web / Kendo) or `.claude/commands/blazor-cloud-reference.md` (Bravo Cloud / MudBlazor).
2. Run a live-DOM inspection script (template above).
3. Add an event-instrumentation pass if a commit produces nothing.
4. Verify selector uniqueness with `page.locator(sel).count()` === 1 before calling it done.
