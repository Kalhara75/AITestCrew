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
| `src/AiTestCrew.Agents/DesktopUiBase/BaseDesktopUiTestAgent.cs` | Agent base — app lifecycle, two-phase LLM generation, test case execution loop |
| `src/AiTestCrew.Agents/DesktopUiBase/DesktopAutomationTools.cs` | Semantic Kernel plugin for LLM exploration (snapshot, click, fill, screenshot) |
| `src/AiTestCrew.Agents/WinFormsUiAgent/WinFormsUiTestAgent.cs` | Concrete agent — config wiring, `CanHandleAsync` for `UI_Desktop_WinForms` |
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
| Recorded step shows `Name='Invoice Actions' ControlType='ToolBar'` instead of the Button the user clicked | Button was either disabled at click time (UIA hit-test skips disabled) or the click landed a few pixels off the button icon | `RefineContainerHit` in the recorder walks the ToolBar's descendants for any Button/MenuItem/Hyperlink/CheckBox/SplitButton whose `BoundingRectangle` contains the click pixel and captures that instead. If the child button doesn't exist in the UIA tree at all (Infragistics custom ribbon), the recorder can't do better — coord-first replay compensates |
| Replay hits the wrong "Open" button (name collision) | Multiple buttons share the name; `FindFirstDescendant(ByName)` returns the first one | Coord-first: `Mouse.Click` at the recorded pixel is unambiguous by definition |
| `FromPoint` hits wrong element at replay (e.g. grid row instead of menu item) | Menu wasn't open at replay because a preceding step (container click) did nothing | Inspect `[DesktopStepExecutor] FromPoint(...) hit Ct='X' Name='Y' (recorded Name='Z')` in the logs. If hit differs from recorded, the UI state upstream is wrong — usually a missing `DelayBeforeMs` or a recorded click on a generic container — the fix is at the recording, not the executor |

---

## Debugging playbook — recording/replay failures

Use this before making any code change when a test fails. It prevents whack-a-mole patching.

1. **Look at the per-step `[DesktopStepExecutor] click step — Name='X' Aid='Y' Coords=(N,M)` line** for each failing step:
   - `Coords=(null,null)` → recorder didn't capture coords. Either the recording pre-dates the feature, or `FindLargestVisibleWindow` returned 0 at record time (rare — process has no visible window). Re-record.
   - `Coords=(huge_X, ...)` → the coord reference is wrong. Verify recorder and executor both use `FindLargestVisibleWindow`; check for regressions to `Process.MainWindowHandle` or `GetForegroundWindow`.
2. **Look at the `FromPoint(X,Y) hit Ct='C' Name='N' (recorded Name='R') match=yes/no` line.**
   - `match=yes` → the right element is under the click pixel at replay. If the click doesn't fire the expected action, the step captured the wrong element at record time (typically a `ToolBar`/`Pane` container when the user intended a child button). Inspect the step's `ControlType`: if it's `ToolBar`/`Pane`/`Group`/`Custom`, re-record clicking squarely on the intended control.
   - `match=no` (timeout) → the UI isn't in the expected state at this point of the test. Something upstream didn't happen: a menu didn't open, a search didn't complete, a dialog didn't appear. Usually fixed by increasing `DelayBeforeMs` on the preceding step or adding an intermediate step that was missed during recording.
3. **Check recording quality in the UI editor** (`EditDesktopUiTestCaseDialog`):
   - Every click should have `Coords` populated.
   - `ControlType` should ideally be a specific actionable type (`Button`/`Hyperlink`/`MenuItem`/`CheckBox`), not a container.
   - `DelayBeforeMs` should reflect reality — if a step fires right after a search button click, expect 5,000–15,000 ms here; if less, re-record with a natural pause.
4. **Check the Docker image version.** If you added a field to `DesktopUiStep` and values are arriving as `null` at the agent despite being captured correctly, the Docker-hosted WebApi is an older build stripping them. Rebuild the image.

---

## Changing the `DesktopUiStep` schema

A checklist — forgetting any one of these causes silent round-trip data loss, which takes hours to diagnose:

1. Add the field to `src/AiTestCrew.Storage/Shared/DesktopUiTestCase.cs` (the C# model).
2. Add it to `ui/src/types/index.ts` (TypeScript interface).
3. Update `emptyStep()` in `ui/src/components/EditDesktopUiTestCaseDialog.tsx` so newly-added steps include the field.
4. Surface it in the dialog UI if it's human-meaningful.
5. Update `DesktopRecorder.cs` to populate it at capture time.
6. Update `DesktopStepExecutor.cs` to honour it at replay time.
7. **Rebuild the Docker image** (`cd ui && npx vite build && cd .. && docker compose build && docker compose up -d`) — the WebApi hosted there holds the server-side copy of the schema, and `System.Text.Json` will silently drop unknown fields on the way into the SQLite DB without this rebuild.
8. Re-record any tests whose saved JSON was written by the old WebApi — the field will be `null` on those steps and cannot be back-filled.

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
