Reference guide for the WinForms Desktop UI recording and replay engine (FlaUI/UI Automation). Consult this before modifying the desktop recorder, replay logic, element resolution, or step execution for desktop UI tests.

This is a **read-only reference** — do not modify this file as part of a task. Use the information here to inform your implementation decisions.

---

## Technology stack

- **FlaUI.UIA3** — .NET wrapper around Windows UI Automation 3 (COM interop)
- **Windows hooks** — `WH_MOUSE_LL` and `WH_KEYBOARD_LL` via P/Invoke for event capture
- **Message pump** — `PeekMessage`/`TranslateMessage`/`DispatchMessage` required for hook callbacks
- **Target framework** — `net8.0-windows` with `UseWindowsForms=true` (for Clipboard access)
- **Global using** — `GlobalUsings.cs` aliases `Application = FlaUI.Core.Application` to resolve ambiguity with `System.Windows.Forms.Application`

---

## Key files

| File | Responsibility |
|---|---|
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopRecorder.cs` | Recording — hooks, message pump, click/keyboard capture, Ctrl+V, assertion pick mode |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopStepExecutor.cs` | Replay — action dispatch, multi-strategy click, polling assertions, element search |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopElementResolver.cs` | Selector building (recording) + cascading element lookup (replay) |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopWindowNormalizer.cs` | Forces app's main window to a fixed size at record + replay so coords round-trip across monitors / DPI / resolutions |
| `src/AiTestCrew.Agents/DesktopUiBase/BaseDesktopUiTestAgent.cs` | Agent base — app lifecycle, two-phase LLM generation, test case execution loop, calls normalizer post-launch + per-step |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopAutomationTools.cs` | Semantic Kernel plugin for LLM exploration (snapshot, click, fill, screenshot) |
| `src/AiTestCrew.Agents/WinFormsUiAgent/WinFormsUiTestAgent.cs` | Concrete agent — config wiring, `CanHandleAsync` for `UI_Desktop_WinForms` |
| `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs` | Clones `DesktopUiStep` for `{{Token}}` substitution — **must copy every field** including coords/delay (see schema-change checklist) |
| `src/AiTestCrew.Agents/Shared/DesktopUiTestCase.cs` | `DesktopUiTestCase` + `DesktopUiStep` models |
| `src/AiTestCrew.Agents/Shared/DesktopUiTestDefinition.cs` | Persistence model with `FromTestCase`/`ToTestCase` converters |
| `ui/src/components/DesktopUiTestCaseTable.tsx` | React table listing desktop test cases |
| `ui/src/components/EditDesktopUiTestCaseDialog.tsx` | React step editor dialog with 5 selector fields |

---

## Design principle: coordinates are the source of truth for clicks

Read this before touching click execution logic. It will save you hours.

**Legacy WinForms apps (Infragistics, DevExpress, Bravo's custom controls, etc.) do not fully expose their UI to UI Automation.** Specifically:

- **Ribbon/toolbar buttons** are often drawn visually but the group (e.g. `ToolBar` named "Invoice Actions") is the only thing in the UIA tree. Individual `Button` children don't exist.
- **`CheckedComboBox` popups** render visible checkboxes that are *not* in the UIA tree (neither ControlView nor RawView). Hit-testing via `FromPoint` can see them; tree traversal via `FindFirstDescendant` cannot.
- **Disabled controls** are skipped by UIA hit-testing — `FromPoint` returns the parent container instead.
- **Custom-drawn menus, popup panels, owner-draw controls** frequently aren't in the tree.

Meanwhile, **WinForms always processes raw mouse input at the pixel level** regardless of UIA. A `Mouse.Click` at the right coordinates fires the underlying control's handler even when UIA has no idea that control exists.

**Because of this, replay is coord-first**: every captured click stores `WindowRelativeX`/`Y`, and the executor clicks those pixels via `Mouse.Click`. UIA is used only as a readiness probe (waiting for the UI to settle), not to locate the click target. This single rule dissolves most failure modes at once:

| Broken control class | Why UIA-based click fails | Why coord click works |
|---|---|---|
| Ribbon button | Not in UIA tree — clicking the ToolBar parent is a no-op | Pixel click fires button's mouse handler |
| CheckedCombo popup item | Not in UIA tree at all | Pixel click hits the rendered checkbox |
| Disabled-at-click-time ribbon button | `FromPoint` returns parent; can't find element | Pixel click works once the button enables (after `DelayBeforeMs`) |
| Multiple elements with same `Name` (e.g. several "Open") | UIA's first-match is typically the wrong one | Pixel click is unambiguous |
| Stale `TreePath` | Tree shifts after login / MDI | Pixel click doesn't use the tree |

UIA tree lookup is still the fallback for pre-existing recordings that have no `WindowRelativeX`/`Y`.

---

## Window-size normalization — make recordings portable across monitors

Without normalization, recorded coords are tied to the monitor they were recorded on. Bravo (and most legacy WinForms apps) remembers its last `Form.WindowState` per user — record on a 5K monitor at 5120×1440, replay on a 1920×1080 office screen, and recorded X coords like `5079` fall off the right edge. The harness *itself* doesn't force maximize; the app does, based on its own saved prefs.

**`DesktopWindowNormalizer.TryNormalize(processId, targetWidth, targetHeight, logger)`** — shared helper that finds the largest visible window owned by the process, un-maximizes it via `ShowWindow(SW_RESTORE)`, and resizes via `SetWindowPos(0, 0, W, H, SWP_NOZORDER | SWP_NOACTIVATE)`. It's idempotent (no-op when already at target ±16 px) and skips windows under 500×400 (login dialogs / transient popups).

Both call sites use the same helper so coords round-trip exactly:

| Caller | When |
|---|---|
| `DesktopRecorder.RecordAsync` | Once after main window appears; then once per second inside the polling loop (catches login → main-form transitions, where the main form is a brand-new window that would come up at the app's saved size) |
| `BaseDesktopUiTestAgent.ExecuteAsync` | After `WaitForAppReady`; after each between-test relaunch; **on every per-step window refresh inside `ExecuteDesktopTestCase`** (catches the same login → main-form transition during replay) |

**Config** (`TestEnvironmentConfig` → `appsettings.json`):

| Key | Default | Purpose |
|---|---|---|
| `WinFormsNormalizeWindow` | `true` | Master switch. Set `false` to honour whatever size the app picks. |
| `WinFormsWindowWidth` | `1600` | Target main-form width in pixels. Should fit the smallest target monitor. |
| `WinFormsWindowHeight` | `900` | Target main-form height. Leave room for taskbar (~40 px). |

**Pitfall — DPI scaling differences**: even with normalization, if recorder runs at 100% DPI and replay runs at 150%, WinForms scales controls and pixel offsets no longer line up. Match Windows scaling on both machines. The app must also run un-maximized — `WindowState=Maximized` means "fill the monitor" and the saved size is ignored.

---

## The recording filter chain — why clicks get silently dropped

The mouse hook (`DesktopRecorder.cs`, `mouseProc`) runs every click through this chain. Each filter has its own log line at `Information` level — when a click goes "missing", read the filter logs first, never guess.

| # | Filter | Log line on reject | Reason it exists |
|---|---|---|---|
| 1 | `fgProcId == targetProcessId` (foreground process must be the target app) | `DROP click — not on target (fgMatches=False, ...)` | Cheap process check. Lies during focus-change races (the click that activates a window fires the hook *before* the OS updates fg). |
| 2 | `IsPointInsideProcessWindow(targetProcessId, x, y)` (cursor must fall inside one of the target process's visible top-level windows) | `DROP click — not on target (insideRect=False, ...)` | Geometry doesn't lie. Closes the focus-race gap — even if `GetForegroundWindow` says target, geometry confirms the click really landed on it. |
| 3 | `automation.FromPoint(point)` exception | `FromPoint threw at (X,Y): ... — falling back to coord-only capture` | Some legacy controls throw COMException from FromPoint. Falls through to coord-only. |
| 4 | `IsWindowChrome(element)` (TitleBar / ScrollBar / Thumb) | `DROP click — chrome/system element (TitleBar)` | Drag-resize, scroll-thumb-drag aren't meaningful test interactions. |
| 5 | `IsSystemElement(element)` (taskbar `TaskListButton`, `Shell_*` shell tray, UWP `*!App` peers) | `DROP click — chrome/system element (...)` | Catches taskbar clicks that slip through process-id checks. |
| 6 | `RefineContainerHit + BuildSelector` exception (e.g. `NotSupportedException` from owner-drawn grid rows) | `Coord-only capture — UIA property access threw: ...` | Owner-drawn surfaces (Infragistics UltraGrid rows, DevExpress cells, custom controls) often expose enough to UIA for `FromPoint` to return *something*, but `Properties.AutomationId.ValueOrDefault` or `BuildTreePath`'s parent-walk throws. **Without this catch the click was lost forever** — outer hook catch swallowed the entire callback. |
| 7 | `FromPoint` returned `null` → coord-only capture | `Coord-only capture — element not exposed to UIA` | True null hit-test result. Owner-drawn grid rows in some grids return null entirely. |
| ✓ | Captured normally | `Captured click: <id> at rel=(X,Y)` | Element resolved cleanly. |

**Critical invariant**: every click on the target app produces exactly one log line at `Information` level. If the user reports "missing click" and you don't see *any* line for that click, the OS-level mouse hook didn't fire — different problem entirely (message pump issue, or click happened before/after recorder window).

### Coord-only capture — the universal fallback

When element resolution can't produce a useful selector (null FromPoint, exception, etc.), the recorder still creates a `DesktopUiStep` with:
- Empty `AutomationId` / `Name` / `ClassName` / `ControlType` / `TreePath`
- Populated `WindowRelativeX` / `WindowRelativeY`
- Populated `Action` (`click` / `right-click` / `double-click`)
- Populated `DelayBeforeMs`

At replay, the readiness probe times out (no Name to match), then `Mouse.Click(recorded_screen_x, recorded_screen_y)` fires regardless. WinForms processes the raw mouse input and the underlying control's handler runs — exactly what happened during recording. **Owner-drawn grid rows, custom popups, ribbon items not in the UIA tree all rely on this path.**

Don't add new filters that reject coord-only steps. They look "empty" in the editor but they're load-bearing.

---

## Element selector model

Desktop steps use **composite selectors** — five fields with cascading priority, unlike web UI's single CSS selector string.

```
DesktopUiStep {
  AutomationId      // Priority 1 — maps to Control.Name in WinForms code
  Name              // Priority 2 — visible text/label of the control
  ClassName         // Priority 3 — e.g. "WindowsForms10.EDIT.app.0.141b42a_r6_ad1"
  ControlType       // Priority 3 — e.g. "Button", "Edit", "ComboBox", "TreeItem"
  TreePath          // Priority 4 — positional path: "Pane[0]/Edit[3]"

  // Coordinate-based fallback (preferred at replay when present):
  WindowRelativeX   // screen X minus main-window Left (pixels)
  WindowRelativeY   // screen Y minus main-window Top  (pixels)

  // Recording pacing (auto-captured delay from previous step):
  DelayBeforeMs     // ms to sleep before this step executes; capped at 30,000
}
```

### Selector building rules (DesktopElementResolver.BuildSelector)

| Property | Include when | Skip when |
|---|---|---|
| `AutomationId` | Non-empty and not auto-generated | Pure numeric (window handles like "5049672"), default designer names (`textBox1`, `button2`, `panel1`, etc.) |
| `Name` | Non-empty | Always include if available |
| `ClassName` | Non-empty | Always include for fallback |
| `ControlType` | Always | — |
| `TreePath` | Always | Built as fallback even when higher-priority selectors exist |

### Element resolution rules (DesktopElementResolver.TryFindElement)

1. **AutomationId** → `FindFirstDescendant(cf.ByAutomationId(...))` — fastest, most stable
2. **Name** → `FindFirstDescendant(cf.ByName(...))` — good for labelled controls
3. **ClassName + ControlType** → `FindAllDescendants(condition)` — **only used if exactly ONE match**. Multiple matches (e.g. several Edit text boxes on a form) fall through to TreePath
4. **TreePath** → `ResolveTreePath(root, path)` — walks indexed segments like `Pane[0]/Edit[3]`

### Common pitfalls

- **Numeric AutomationId**: Window handles change every launch. `IsAutoGeneratedId` must catch pure numeric strings.
- **Ambiguous ClassName+ControlType**: Forms with multiple text boxes all share the same class. MUST fall through to TreePath.
- **Stale mainWindow reference**: After navigating through MDI forms/dialogs, the original `mainWindow` may not contain the target element. Search all windows.
- **TreePath fragility**: Adding/removing controls shifts indices. TreePath is the least stable selector — higher-priority selectors are always preferred.

---

## Recording architecture

### Hook lifecycle

```
RecordAsync()
  ├── Launch app (ProcessStartInfo with WorkingDirectory = exe's folder)
  ├── Wait for main window
  ├── Install WH_MOUSE_LL hook (mouseProc callback)
  ├── Install WH_KEYBOARD_LL hook (keyboardProc callback)
  ├── Main loop:
  │     ├── PeekMessage / TranslateMessage / DispatchMessage  ← CRITICAL: hooks don't fire without this
  │     ├── Check assertion conversion (pendingAssertType + new step)
  │     ├── Check app.HasExited
  │     └── Thread.Sleep(50)  ← must stay on same thread as hooks
  ├── FlushKeyBuffer()
  ├── UnhookWindowsHookEx (both hooks)
  ├── Close app
  └── ValidateRecordedSteps()
```

### Critical: message pump requirement

Low-level Windows hooks deliver callbacks via the installing thread's message queue. Without `PeekMessage`/`TranslateMessage`/`DispatchMessage` in the main loop, **hook callbacks never fire**. This is the #1 reason recording can silently capture 0 steps.

**Do NOT use `await Task.Delay`** in the main loop — it can switch threads, breaking the hook association. Use `Thread.Sleep` instead.

### Mouse hook filtering

The mouse hook (`mouseProc`) applies these filters in order:

1. **Foreground window process check**: `GetForegroundWindow()` → `GetWindowThreadProcessId()` → must match `targetProcessId`
2. **Element resolution**: `automation.FromPoint(cursorPosition)` → resolves the UI Automation element at click coordinates
3. **Window chrome filter** (`IsWindowChrome`): Skip `TitleBar`, `ScrollBar`, `Thumb` control types
4. **System UI filter** (`IsSystemElement`): Skip taskbar buttons (`TaskListButton` class), shell tray (`Shell_*` class), UWP app IDs (`*!App` pattern in AutomationId)
5. **Container refinement** (`RefineContainerHit`): if `FromPoint` landed on a generic container (`ToolBar`/`Pane`/`Group`/`Custom`), walk its descendants looking for an actionable child (`Button`/`MenuItem`/`Hyperlink`/`CheckBox`/`SplitButton`/`RadioButton`) whose bounding rect contains the click pixel. Captures the specific child even if it was disabled and hit-test-invisible at click time.
6. **Selector building**: `DesktopElementResolver.BuildSelector(refined, mainWindow)`
7. **Coordinate capture**: relative to `FindLargestVisibleWindow(targetProcessId)` — see below.
8. **Delay capture**: `DelayBeforeMs = now - lastStepUtc` on the new step; `lastStepUtc = now` afterward.

### Why `FindLargestVisibleWindow`, not `Process.MainWindowHandle` or `GetForegroundWindow`

The coordinate reference window must be consistent across record and replay.

- `Process.MainWindowHandle` is cached per `Process` instance and frequently picks tiny helper windows at `(0,0)` for WinForms apps with multiple top-level windows — coordinates become nearly-absolute-screen values that don't translate at replay.
- `GetForegroundWindow()` returns whatever is topmost *at that moment*, which for a click on a dropdown popup is the popup itself — a small window with its own origin. Coords relative to it make no sense at replay (the popup won't be open yet).
- `FindLargestVisibleWindow(pid)` enumerates every top-level window, filters to visible ones owned by the target process, and returns the largest by area. The app's main form is always the largest window — stable across login transitions, unaffected by transient popups.

Both recorder and executor use the same `FindLargestVisibleWindow` implementation so offsets round-trip exactly.

### Keyboard hook — Ctrl+V paste handling

```
keyboardProc callback:
  ├── Check GetKeyState(VK_CONTROL)
  ├── If Ctrl held:
  │     ├── Ctrl+V → FlushKeyBuffer, read clipboard on STA thread, create fill step
  │     └── Ctrl+A/C/X → ignore (no text produced)
  ├── Enter/Tab/Escape → FlushKeyBuffer, add press step
  ├── Backspace → remove last char from buffer
  └── Printable char (0x20–0x7E) → append to keyBuffer
```

Clipboard must be read on an STA thread (`Thread.SetApartmentState(ApartmentState.STA)`). The main hook thread is not STA.

### Assertion capture flow

1. User presses T/V/E in console → `pendingAssertType` set
2. User clicks element in target app → mouse hook fires, step added to `steps` list, `lastClickedElement` stored
3. Main loop detects `steps.Count > lastStepCount && pendingAssertType != ""`
4. Converts last step's action from "click" to the assert type
5. For `assert-text`: calls `ExtractTextFromElement(lastClickedElement)` — uses the **stored element reference**, not a re-search from stale `mainWindow`
6. Text extraction searches: ValuePattern → Name → children → descendant Text/Edit controls

---

## Replay architecture

### Per-step delay (DelayBeforeMs)

Before executing any step (except the first), the executor sleeps `step.DelayBeforeMs` milliseconds, capped at 30,000. This reproduces the pacing the user had during recording — pauses for search to complete, menus to animate in, modal dialogs to load. **This replaces the need for manual `wait` steps in most cases.** Null on old recordings means "no delay"; the executor degrades gracefully.

### Click execution

Two paths, gated on whether the step carries recorded coordinates:

**Path A — step has coords (modern recordings):**

1. `TryFindWithFallback` runs as a **readiness probe only**: it polls `FromPoint` at the recorded screen position until the hit element's `Name` matches the recorded `Name` (or the step's `TimeoutMs` expires). This lets the replay wait for slow operations (search completing, disabled buttons becoming enabled) without per-step hacks.
2. **Click executes via `Mouse.Click(screenX, screenY)`** at the translated recorded pixel. The UIA element is *not* used for the click itself — that's critical for controls outside UIA (ribbon buttons, custom popups). WinForms processes the raw mouse event and the control's handler fires.

**Path B — no coords (legacy recordings):**

Falls back to the classic UIA-element click strategy in `ClickElement(AutomationElement)`:

1. **ExpandCollapsePattern** — if supported and element is collapsed, call `Expand()`. Opens combo dropdowns programmatically, avoiding centre-of-Pane-misses-the-arrow pitfalls.
2. **InvokePattern** — reliable for Button/Hyperlink/MenuItem/ToolBar items. **Skipped for `Pane`/`Window`/`Group`/`Custom` control types** — these often report Invoke as supported but the invoke action is a no-op or differs from a real user click (e.g. a CheckedComboBox Pane doesn't open its popup on Invoke).
3. **`TryClickDropdownArrow`** — for `Pane` controls shaped like a combo (`Width > Height * 2`), click near the right edge where the dropdown arrow lives rather than the centre (which is on the display text).
4. **`element.Click()`** — standard FlaUI coordinate-based click.
5. **`Mouse.Click(clickablePoint)`** — raw Win32 click at the element's clickable point.

### Readiness probe name-matching

`NameMatches(actual, expected)` is case-insensitive and strips ampersand mnemonics so recorded `"&Yes"` matches UIA's `"Yes"`. An empty `expected` (text fields, anonymous panes) matches any hit.

### Window transition detection

After every click, the executor checks if the window count changed:
```csharp
windowCountBefore = app.GetAllTopLevelWindows(automation).Length;
ClickElement(element, logger);
windowCountAfter = app.GetAllTopLevelWindows(automation).Length;
if (changed) → Thread.Sleep(1500) + Wait.UntilInputIsProcessed(500ms)
```

This handles: login dialog → main form, button → child dialog, close dialog → parent form.

### Element search scopes (FindElementWithRetry)

Polls across three scopes with 300ms intervals until timeout:

1. **Primary window** → `TryFindInScope(window, step, automation)`
2. **All top-level app windows** → iterates `app.GetAllTopLevelWindows()`, skips primary
3. **Desktop root** → `automation.GetDesktop()` — catches deeply nested MDI children

For assertions, `QuickFindElement` does a **single-attempt** (no retry) across all three scopes. The polling happens in the assertion's own loop, allowing text to be re-read every 500ms.

### Assertion polling (assert-text)

```
while (elapsed < timeout):       // minimum 15 seconds
    element = QuickFindElement()  // single-attempt, all scopes
    if element found:
        text = GetElementText(element)  // searches children/descendants
        if text.Contains(expected):
            return Pass
    Thread.Sleep(500)
```

This handles async operations where text transitions through intermediate states (e.g. "Search is in progress" → "Search completed").

### Text extraction (GetElementText)

Searches four levels for text content:

1. **ValuePattern** on element (text boxes, editable controls)
2. **Name property** on element (labels, buttons, tree items)
3. **Immediate children** — ValuePattern and Name on each child
4. **All descendant Text controls** — `FindAllDescendants(ByControlType(Text))`

Critical for container elements (Pane, Group) where the visible text lives in a child element.

---

## Common issues and solutions

| Symptom | Root cause | Solution |
|---|---|---|
| 0 steps captured | No message pump | Ensure `PeekMessage`/`TranslateMessage`/`DispatchMessage` in main loop |
| `await Task.Delay` breaks hooks | Thread switch | Use `Thread.Sleep` — must stay on hook-installing thread |
| App fails to launch ("could not instantiate") | Wrong working directory | `ProcessStartInfo.WorkingDirectory = Path.GetDirectoryName(appPath)` |
| Taskbar click captured | `FromPoint` resolves taskbar element | `IsSystemElement` filters by ClassName patterns (TaskListButton, Shell_*) |
| Ctrl+V records "V" instead of clipboard | Ctrl modifier not detected | Check `GetKeyState(VK_CONTROL)`, read clipboard on STA thread |
| Fill targets wrong field | Ambiguous ClassName+ControlType | `FindAllDescendants` → if multiple matches, fall through to TreePath |
| Assert finds element but text is empty | Container element (Pane) | `GetElementText` must search children and Text descendants |
| Assert-text always gets stale text | `FindElementAcrossWindows` consumes full timeout | Use `QuickFindElement` (single-attempt) in polling loop |
| Numeric AutomationId (window handle) | Changes every launch | `IsAutoGeneratedId` skips pure numeric strings |
| Element not found in MDI app | Only searching primary window | Search all windows → desktop root fallback |
| Replay clicks ribbon button but nothing happens | UIA sees only the ToolBar parent; `element.Click()` clicks the ToolBar centre which is a no-op | Coord-first path — `Mouse.Click(WindowRelativeX, Y)`. If coords are missing, re-record; disabled-at-click-time means UIA returned the container at record time and `RefineContainerHit` should pick up the Button child if it's in the tree |
| Replay fails on combo-dropdown checkbox (`(Select All)`, etc.) with "Element not found" | Popup items are rendered visually but not in UIA tree (ControlView or RawView) | Coord-first path handles this; `FromPoint` hit-tests through where tree enumeration can't |
| All coords are `null` on non-first clicks of a legacy recording | Recorder used a stale `mainWindow` (captured at the Login window) for the offset; after login transition the reference is dead and `BoundingRectangle` throws | Use `FindLargestVisibleWindow(targetProcessId)` — a fresh enum scan each click, not a cached Window reference |
| Recorded coords are absurd (e.g. X=5080 on a reasonable-sized window) | `Process.MainWindowHandle` returned a tiny helper window at `(0,0)`, so "relative" ≈ absolute screen coord | Same — `FindLargestVisibleWindow` filters to the largest-area visible window of the process |
| Coords relative to dropdown popup, not main window | `GetForegroundWindow()` returned the transient popup | Never use `GetForegroundWindow` as the reference; always `FindLargestVisibleWindow` |
| Clicks fire too fast — search hasn't completed before next click | No inter-step delay | Recorder writes `DelayBeforeMs` (delta from previous step); executor honours it. Cap at 30,000 ms |
| New `DesktopUiStep` field works on PC but disappears at replay | Docker-hosted WebApi is running older build; `System.Text.Json` silently drops unknown fields on deserialisation before persisting to SQLite | Rebuild the Docker image (`docker compose build && docker compose up -d`) whenever `DesktopUiStep` / `DesktopUiTestDefinition` schema changes. The schema lives in `AiTestCrew.Storage` which is compiled into the WebApi image |
| Recorded step shows `Name='Invoice Actions' ControlType='ToolBar'` instead of the Button the user clicked | Button was either disabled at click time (UIA hit-test skips disabled) or the click landed a few pixels off the button icon | `RefineContainerHit` in the recorder walks the ToolBar's descendants for any Button/MenuItem/Hyperlink/CheckBox/SplitButton/RadioButton/**DataItem/ListItem/TreeItem** whose `BoundingRectangle` contains the click pixel and captures that instead. If the child button doesn't exist in the UIA tree at all (Infragistics custom ribbon), the recorder can't do better — coord-first replay compensates |
| Replay hits the wrong "Open" button (name collision) | Multiple buttons share the name; `FindFirstDescendant(ByName)` returns the first one | Coord-first: `Mouse.Click` at the recorded pixel is unambiguous by definition |
| `FromPoint` hits wrong element at replay (e.g. grid row instead of menu item) | Menu wasn't open at replay because a preceding step (container click) did nothing | Inspect `[DesktopStepExecutor] FromPoint(...) hit Ct='X' Name='Y' (recorded Name='Z')` in the logs. If hit differs from recorded, the UI state upstream is wrong — usually a missing `DelayBeforeMs` or a recorded click on a generic container — the fix is at the recording, not the executor |
| Recording test on a smaller monitor than where it was recorded fails with "Element not found" or wrong-control clicks; recorded coords like `5079`, `3901` show in the editor | Bravo's `Form.WindowState` was `Maximized` on the original 5K monitor, so the captured window-relative coords belong to a 5120-wide window. The harness doesn't force any size. | Enable `WinFormsNormalizeWindow` (default `true`) and set `WinFormsWindowWidth` / `WinFormsWindowHeight` to a size that fits *every* target monitor. Re-record — coords are then captured against the normalized rect and round-trip everywhere. |
| Click on PowerShell / recorder console / Explorer gets recorded as a step | Original mouse hook only checked `GetForegroundWindow()`'s process — but `GetForegroundWindow` returns the *previous* foreground during the click that activates a new window (focus-change race) | Filter chain now requires BOTH `fgProcId == targetProcessId` AND `IsPointInsideProcessWindow(targetProcessId, x, y)`. Geometry check closes the focus-race gap. Look for `DROP click — not on target` log line for diagnostics. |
| Grid row click on owner-drawn grids (Infragistics, DevExpress, Bravo's custom grid) silently missing — no step between fill and the next button click | The mouse hook fires, but `automation.FromPoint` returns null OR `BuildSelector` throws `NotSupportedException` reading properties on the row element. Pre-fix outer hook catch swallowed the entire click without logging. | Coord-only fallback path always captures the click with empty selectors but valid coords. Replay clicks the recorded pixel via `Mouse.Click`. Look for `Coord-only capture at (X,Y) — UIA property access threw: ...` or `... — element not exposed to UIA` in the recorder log. **Do not add filters that reject coord-only steps** — they're load-bearing. |
| Replay log shows `Coords=(null,null)` on every step but storage clearly has coords like `(387,195)` | `StepParameterSubstituter.Apply` clones `DesktopUiStep` field-by-field for `{{Token}}` substitution and forgot to copy `WindowRelativeX` / `WindowRelativeY` / `DelayBeforeMs`. Triggers whenever the test set has any environment parameters set. | Add new `DesktopUiStep` fields to **both** Apply methods in `StepParameterSubstituter.cs` (one for `DesktopUiTestDefinition`, one for `DesktopUiTestCase`). This file is now in the schema-change checklist. |
| User reports "still broken" after a fix; latest run timestamp is *before* the latest DLL build timestamp | Long-running Runner / agent process holds an old DLL in memory. `dotnet build` doesn't restart already-running processes. | Kill the process (`taskkill /F /IM AiTestCrew.Runner.exe`) and re-launch. Verify with `ls -la --time-style=full-iso bin/Debug/net8.0-windows/AiTestCrew.Agents.dll` that the DLL is newer than the source change. |
| Mouse hook log says `Mouse hook error: Specified method is not supported.` and the click is missing | `NotSupportedException` thrown inside the hook callback (most often from `BuildSelector`'s `Properties.X.ValueOrDefault` access on owner-drawn elements). Outer try/catch swallowed it before a step could be added. | Each property-access block inside the hook is now wrapped in its own try/catch; failure falls through to coord-only capture. If a *new* hook-internal exception starts swallowing clicks, add another fine-grained catch around the throwing operation rather than fixing the symptom downstream. |

---

## Debugging playbook — recording/replay failures

Use this before making any code change when a test fails. It prevents whack-a-mole patching.

### Step 0 — verify the deployed binary is current

Before believing *any* user report of "still broken":

```bash
ls -la --time-style=full-iso \
  C:/MyCode/github/AITestCrew/src/AiTestCrew.Runner/bin/Debug/net8.0-windows/AiTestCrew.Agents.dll \
  C:/MyCode/github/AITestCrew/src/AiTestCrew.Agents/DesktopUiBase/DesktopRecorder.cs
```

DLL mtime must be ≥ source mtime, AND the process running the test must have started *after* that DLL was built. Long-running agent processes (`--agent` mode) hold the old DLL in memory until killed:

```bash
taskkill /F /IM AiTestCrew.Runner.exe
```

If you skip this step you will burn an hour debugging a fix that's already deployed.

### Step 1 — recording-time logs (the recorder console)

Every click on the target app produces exactly one `Information`-level log line. Find the line for the missing/broken click:

| Log line | Meaning |
|---|---|
| `Captured click: <id> at rel=(X,Y)` | OK — element resolved cleanly |
| `Coord-only capture at (X,Y) — element not exposed to UIA` | OK — `FromPoint` returned null, click captured with coords only |
| `Coord-only capture at (X,Y) — UIA property access threw: ...` | OK — owner-drawn element threw `NotSupportedException`, click captured with coords only |
| `FromPoint threw at (X,Y): ... — falling back to coord-only capture` | OK — UIA exception, fell through to coord-only |
| `DROP click — not on target (fgMatches=False, insideRect=...)` | Filter chain rejected the click. Usually means user clicked outside the app. |
| `DROP click — chrome/system element (TitleBar)` | Click was on TitleBar / ScrollBar / Thumb / shell tray |
| **No log line at all for that click** | OS-level mouse hook didn't fire. Different problem — message pump issue, or click happened before/after the recording window |

### Step 2 — replay-time logs (the test run console)

For each failing step, two log lines tell the whole story:

1. **`[DesktopStepExecutor] click step — Name='X' Aid='Y' Coords=(N,M)`**
   - `Coords=(null,null)` → either the recording pre-dates coord capture, OR `StepParameterSubstituter` is dropping fields (when the test set has env params set). **Check storage directly via the WebApi** — if storage shows coords but the executor sees null, the substituter is the bug.
   - `Coords=(huge_X, ...)` → the recording was captured against a much wider window. Either re-record on the target monitor, or enable `WinFormsNormalizeWindow` in config (default `true` on new builds — but old recordings need re-recording).

2. **`FromPoint(X,Y) hit Ct='C' Name='N' (recorded Name='R') match=yes/no`**
   - `match=yes` → right element under the click pixel at replay. If the click doesn't fire the expected action, the step captured the wrong element at record time (typically a `ToolBar`/`Pane` container when the user intended a child button). Inspect the step's `ControlType`: if it's `ToolBar`/`Pane`/`Group`/`Custom`, re-record clicking squarely on the intended control.
   - `match=no` (timeout) → the UI isn't in the expected state. Something upstream didn't happen: a menu didn't open, a search didn't complete, a dialog didn't appear. Usually fixed by increasing `DelayBeforeMs` on the preceding step or adding an intermediate step that was missed during recording.

### Step 3 — confirm storage matches the user's experience

If you're not sure whether the user re-recorded with the latest build, query the WebApi directly and inspect the steps:

```bash
KEY="<api-key-from-appsettings.json>"
curl -s -H "X-Api-Key: $KEY" \
  http://localhost:5050/api/modules/<moduleId>/testsets/<testSetId> \
  | python -c "import json,sys; d=json.load(sys.stdin); ..."
```

Look for: are coord values 5K-monitor-sized (recording is stale)? Is there a step between `fill X` and `click <button>` (row-select captured)? Does the post-step's `delayBeforeMs` between the suspected actions account for the missing step?

### Step 4 — check recording quality in the UI editor

(`EditDesktopUiTestCaseDialog`):
- Every click should have `Coords` populated.
- `ControlType` should ideally be a specific actionable type (`Button`/`Hyperlink`/`MenuItem`/`CheckBox`/`DataItem`), not a container. Empty `ControlType` is fine for legitimate coord-only captures.
- `DelayBeforeMs` should reflect reality — if a step fires right after a search button click, expect 5,000–15,000 ms here; if less, re-record with a natural pause.

### Step 5 — check the Docker image version

If you added a field to `DesktopUiStep` and values are arriving as `null` at the agent despite being captured correctly, the Docker-hosted WebApi is an older build stripping them. Rebuild the image.

---

## Changing the `DesktopUiStep` schema

A checklist — forgetting any one of these causes silent round-trip data loss, which takes hours to diagnose:

1. Add the field to `src/AiTestCrew.Storage/Shared/DesktopUiTestCase.cs` (the C# model).
2. Add it to `ui/src/types/index.ts` (TypeScript interface).
3. Update `emptyStep()` in `ui/src/components/EditDesktopUiTestCaseDialog.tsx` so newly-added steps include the field.
4. Surface it in the dialog UI if it's human-meaningful.
5. Update `DesktopRecorder.cs` to populate it at capture time.
6. Update `DesktopStepExecutor.cs` to honour it at replay time.
7. **Update both `Apply` overloads in `src/AiTestCrew.Agents/Environment/StepParameterSubstituter.cs`** — one clones `DesktopUiTestDefinition`, one clones `DesktopUiTestCase`. They build a fresh `DesktopUiStep` field-by-field; **any field you forget here gets nulled on every test where the test set has env params set**. The symptom is replay-time `Coords=(null,null)` while storage clearly shows the field populated. This file has caused multi-hour debugging sessions — always update it.
8. **Rebuild the Docker image** (`cd ui && npx vite build && cd .. && docker compose build && docker compose up -d`) — the WebApi hosted there holds the server-side copy of the schema, and `System.Text.Json` will silently drop unknown fields on the way into the SQLite DB without this rebuild.
9. **Rebuild AND restart the local Runner** — `dotnet build` updates the DLL but does not restart already-running processes. `taskkill /F /IM AiTestCrew.Runner.exe` then re-launch.
10. Re-record any tests whose saved JSON was written by the old WebApi — the field will be `null` on those steps and cannot be back-filled.

---

## Adding new desktop step actions

1. Add the action string to `DesktopUiStep.Action` XML doc comment
2. Add the case to `DesktopStepExecutor.ExecuteStep` switch statement
3. Add the action to `ACTIONS` array in `EditDesktopUiTestCaseDialog.tsx`
4. If the action needs new fields, add properties to `DesktopUiStep` and `DesktopUiStep` TypeScript interface
5. Update `NO_ELEMENT`, `USES_MENU_PATH`, or `USES_WINDOW_TITLE` sets in the dialog if applicable
6. Update `docs/functional.md` step actions table

---

## Adding new element selector strategies

1. Add the property to `DesktopUiStep` model (C# and TypeScript)
2. Update `DesktopElementResolver.BuildSelector` to populate it during recording
3. Update `DesktopElementResolver.TryFindElement` with the lookup logic and correct priority
4. Update `DesktopStepExecutor.TryFindInScope` with the same logic (used by quick search)
5. Add the field to `EditDesktopUiTestCaseDialog.tsx` selector row
6. Consider: should this selector take priority over existing ones? Update the cascade order.
