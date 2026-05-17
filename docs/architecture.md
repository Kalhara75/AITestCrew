# AITestCrew — Architecture Documentation

## Solution Structure

The solution (`AiTestCrew.slnx`) contains six .NET 8 projects with a strict layered dependency graph. A React frontend communicates with the WebApi over REST. In production, the frontend is co-hosted inside WebApi (served from `wwwroot/`); in development, Vite proxies API calls.

```
┌──────────────┐     ┌──────────────────┐
│  React UI    │────▶│  AiTestCrew      │
│  (co-hosted  │ HTTP│  .WebApi (REST)  │──┐
│  or Vite dev)│◀────│  Port 5050       │  │
└──────────────┘     └──────────────────┘  │
                                           │
┌──────────────────┐                       │
│  AiTestCrew      │──┐                   │
│  .Runner (CLI)   │  │                   │
└──────────────────┘  │                   │
                      ▼                   ▼
              AiTestCrew.Orchestrator ──────► AiTestCrew.Storage
                                                     │
              AiTestCrew.Agents ─────────────────────┘
                      │
              AiTestCrew.Core
```

Full dependency graph:

```
Core (net8.0)           — no deps
Storage (net8.0)        → Core
Orchestrator (net8.0)   → Core, Storage
Agents (net8.0-windows) → Core, Storage
Runner (net8.0-windows) → Core, Storage, Agents, Orchestrator
WebApi (net8.0-windows) → Core, Storage, Agents, Orchestrator
```

Key changes from the original five-project layout:
- **Storage** was extracted from Agents so that Orchestrator no longer depends on Agents.
- Core, Storage, and Orchestrator target `net8.0` (cross-platform). Agents, Runner, and WebApi target `net8.0-windows` (FlaUI / WindowsForms dependency).

Both Runner (CLI) and WebApi reference the same Storage layer. Runner can operate in **remote mode** (calling WebApi over HTTP instead of local storage) when `ServerUrl` is configured.

---

## Project Responsibilities

### AiTestCrew.Core

Pure domain layer. No NuGet dependencies beyond the .NET BCL.

| File | Purpose |
|---|---|
| `Models/TestTask.cs` | Input to an agent — a single decomposed test task |
| `Models/TestStep.cs` | Atomic test action with pass/fail/error result |
| `Models/TestResult.cs` | Aggregated output of one agent execution (has `ObjectiveId`, `ObjectiveName`) |
| `Models/TestResult.cs` (`TestSuiteResult`) | Aggregated output of the full test run (`TotalObjectives`, not TotalTasks) |
| `Models/Enums.cs` | `TestStatus`, `TestTargetType`, `TestPriority` |
| `Models/RunMode.cs` | `Normal`, `Reuse`, `Rebaseline`, `List` CLI modes |
| `Interfaces/ITestAgent.cs` | Contract all agents must implement |
| `Interfaces/ITokenProvider.cs` | Token acquisition contract (JWT login or static token) |
| `Interfaces/IApiTargetResolver.cs` | Resolves API base URLs and per-stack token providers from `ApiStacks` config |
| `Models/User.cs` | User model (Id, Name, ApiKey, Role, timestamps) |
| `Interfaces/IUserRepository.cs` | User CRUD + API key lookup contract |
| `Configuration/TestEnvironmentConfig.cs` | Strongly-typed settings binding from `appsettings.json` (includes `ApiStacks`, `StorageProvider`, `SqliteConnectionString`, `ListenUrl`, `CorsOrigins`, `ServerUrl`, `ApiKey`) |
| `Configuration/ApiStackConfig.cs` | Config models for API stacks (`ApiStackConfig`) and modules (`ApiModuleConfig`) |

---

### AiTestCrew.Agents

Contains agent implementations only. References Core and Storage. Persistence and shared definition types moved to `AiTestCrew.Storage` (see below).

```
Agents/
  ApiAgent/
    ApiTestAgent.cs               — REST/GraphQL test execution (multi-stack + multi-environment aware via IApiTargetResolver + IEnvironmentResolver)
  Auth/
    ApiTargetResolver.cs          — Resolves API base URLs and per-(env,stack) LoginTokenProviders from ApiStacks + Environments config
    LoginTokenProvider.cs         — Acquires JWTs by calling a stack's security module login endpoint
    StaticTokenProvider.cs        — Returns a pre-configured static token
  Environment/
    EnvironmentResolver.cs        — Per-env override resolver (UI URLs, creds, WinForms path, Bravo DB, per-stack BaseUrls); falls back to top-level fields when a field isn't present in the active env block
    StepParameterSubstituter.cs   — Walks every step-definition / test-case type and applies {{Token}} substitution using TokenSubstituter (lenient); returns cloned objects so persisted state is never mutated
  Recording/
    IRecordingService.cs          — Contract with four methods: RecordCaseAsync / RecordSetupAsync /
                                    RecordVerificationAsync / AuthSetupAsync. Shared by CLI flows
                                    (--record, --record-setup, --record-verification, --auth-setup) and the
                                    agent queue's recording JobKinds.
    RecordingService.cs           — Implementation. Runs PlaywrightRecorder or DesktopRecorder, persists the
                                    captured steps back into the test set, and returns a RecordingResult
                                    with structured step summaries so the CLI can still render its step table.
    RecordingRequests.cs          — Request DTOs (RecordCaseRequest etc.) serialized into
                                    RunQueueEntry.RequestJson for recording jobs, plus RecordingResult.
  Base/
    BaseTestAgent.cs              — Shared LLM communication (delegates JSON utilities to LlmJsonHelper)
    LlmJsonHelper.cs              — Static JSON cleaning/parsing utilities (shared with WebApi endpoints)
  WebUiBase/
    BaseWebUiTestAgent.cs         — Shared Playwright logic: browser lifecycle, two-phase LLM generation,
                                    step execution (with JS click fallback), screenshot capture
    PlaywrightBrowserTools.cs     — Semantic Kernel plugin exposing snapshot/navigate/click/fill as kernel
                                    functions; tracks PageObservation list (real URL+title per page visited)
    PlaywrightRecorder.cs         — Human-driven recording mode: non-headless Chromium, JS overlay panel,
                                    ExposeFunctionAsync step capture, returns WebUiTestDefinition with real selectors
  LegacyWebUiAgent/
    LegacyWebUiTestAgent.cs       — ASP.NET MVC web UI agent (forms auth, UI_Web_MVC)
  BraveCloudUiAgent/
    BraveCloudUiTestAgent.cs      — Blazor web UI agent (Azure SSO + storage state, UI_Web_Blazor)
  DesktopUiBase/
    BaseDesktopUiTestAgent.cs     — Base class for FlaUI desktop agents (app lifecycle, two-phase LLM generation, step execution)
    DesktopAutomationTools.cs     — Semantic Kernel plugin for LLM exploration (snapshot, click, fill, list_windows)
    DesktopElementResolver.cs     — Cascading element lookup: AutomationId → Name → ClassName+ControlType → TreePath
    DesktopStepExecutor.cs        — Action dispatcher for desktop UI steps (click, fill, select, assert-*, menu-navigate, etc.)
    DesktopRecorder.cs            — Records desktop test cases via Windows hooks (mouse + keyboard) + UI Automation
  WinFormsUiAgent/
    WinFormsUiTestAgent.cs        — Windows Forms desktop agent (FlaUI, UI_Desktop_WinForms)
  AseXmlAgent/
    AseXmlGenerationAgent.cs      — AEMO B2B aseXML payload generation (AseXml_Generate target type)
                                    LLM picks a template + extracts user fields; renderer is deterministic
    Templates/
      TemplateManifest.cs         — POCO for the per-template manifest JSON (field specs: auto/user/const)
      TemplateRegistry.cs         — Scans templates/asexml/**/*.manifest.json at startup; singleton cache
      AseXmlRenderer.cs           — Pure-function renderer: applies generators, enforces required fields,
                                    substitutes {{tokens}}, validates well-formedness via XDocument.Parse
      FieldGenerators.cs          — Auto-field generators (messageId, transactionId, nowOffset, today)
```

---

### AiTestCrew.Storage

Persistence layer extracted from Agents. References Core only. All files retain their original namespaces (e.g. `AiTestCrew.Agents.Persistence`) for backward compatibility.

```
Storage/
  Persistence/
    IModuleRepository.cs          — Module CRUD interface
    ITestSetRepository.cs         — Test set CRUD + merge + move + stats interface
    IExecutionHistoryRepository.cs — Execution run save/load/prune interface
    PersistedModule.cs            — Module manifest model (id, name, description, timestamps, CreatedBy, LastModifiedBy)
    PersistedTestSet.cs           — JSON envelope model for saved test sets (contains List<TestObjective> TestObjectives, v2 schema)
                                    Includes SetupSteps (List<WebUiStep>) and SetupStartUrl for reusable
                                    pre-test-case setup (e.g. login) — runs before every test case in the set
                                    Includes ApiStackKey and ApiModule for multi-stack API targeting
    PersistedExecutionRun.cs      — Execution history models (run, objective results with PersistedObjectiveResult, step results)
                                    Includes StartedBy and StartedByName for audit trail
    TestObjective.cs              — Test objective model with all step collections
    ModuleRepository.cs           — File-based implementation of IModuleRepository (modules/{id}/module.json)
    TestSetRepository.cs          — File-based implementation of ITestSetRepository (legacy flat + module-scoped)
                                    SaveAsync creates the module directory if it does not exist
    ExecutionHistoryRepository.cs — File-based implementation of IExecutionHistoryRepository (executions/{testSetId}/{runId}.json)
    SlugHelper.cs                 — Shared slugification logic
    MigrationHelper.cs            — Auto-migrates legacy testsets/ to modules/default/
  Shared/
    WebUiTestCase.cs              — WebUiTestCase + WebUiStep models (legacy, shared by both UI agents)
    WebUiTestDefinition.cs        — WebUiTestDefinition model (v2 step definition for TestObjective.WebUiSteps)
    DesktopUiTestCase.cs          — Desktop UI test case model
    DesktopUiTestDefinition.cs    — Desktop UI step definition model
  ApiAgent/
    ApiTestDefinition.cs          — LLM-generated API test step model + validation verdict
    ApiTestCase.cs                — API test case wrapper
  AseXmlAgent/
    AseXmlTestDefinition.cs       — aseXML generation step persistence model (templateId + user field values)
    AseXmlDeliveryTestDefinition.cs — aseXML delivery step persistence model (generation fields + EndpointCode)
    VerificationStep.cs           — UI verification step attached to a delivery case
  Sqlite/
    SqliteConnectionFactory.cs    — Creates and configures SQLite connections (WAL mode)
    DatabaseMigrator.cs           — Schema creation and versioned migrations
    SqliteModuleRepository.cs     — IModuleRepository implementation backed by SQLite
    SqliteTestSetRepository.cs    — ITestSetRepository implementation backed by SQLite
    SqliteExecutionHistoryRepository.cs — IExecutionHistoryRepository implementation backed by SQLite
    SqliteUserRepository.cs       — IUserRepository implementation backed by SQLite (atc_ prefixed API keys)
    SqliteChatConversationRepository.cs — IChatConversationRepository — per-user chat conversations + messages, with WHERE user_id on every read/write and per-user retention cap
    JsonOpts.cs                   — Shared JSON serialization options for SQLite data columns
```

#### Web UI Agents

Two Playwright-powered agents extend `BaseWebUiTestAgent`:

```
BaseTestAgent  (LLM, AskLlmAsync/AskLlmForJsonAsync)
    ├── BaseWebUiTestAgent  (Playwright: browser lifecycle, two-phase generation, step execution)
    │       ├── LegacyWebUiTestAgent   (UI_Web_MVC,    forms auth)
    │       └── BraveCloudUiTestAgent  (UI_Web_Blazor, Azure SSO + storage state)
    └── BaseDesktopUiTestAgent  (FlaUI: app lifecycle, two-phase generation, step execution)
            └── WinFormsUiTestAgent    (UI_Desktop_WinForms)
```

**Browser lifecycle** — A new `IPlaywright` + `IBrowser` instance is created at the start of each `ExecuteAsync` call and disposed in a `finally` block. Agents are registered as singletons but hold no browser state between calls.

**Two-phase LLM generation** — `BaseWebUiTestAgent.ExploreAndGenerateTestCasesAsync`:
1. **Phase 1 — Exploration**: a `Kernel.Clone()` with the `PlaywrightBrowserTools` plugin registered runs the LLM with `FunctionChoiceBehavior.Auto()`. The LLM navigates and inspects the real page. `PlaywrightBrowserTools` accumulates a `List<PageObservation>` (actual URL + title per `snapshot()` call).
2. **Phase 2 — JSON generation**: the exploration result and `PageObservation` list are injected into a new prompt that instructs the LLM to output only a JSON array. The base `Kernel` (no tools) is used so no further navigation occurs. Observed page facts are passed as authoritative ground truth to prevent hallucinated assertion values.

**Credentials** — `GetConfiguredCredentials()` is a virtual method on `BaseWebUiTestAgent`. Each subclass overrides it to return `(Username, Password)` from config. The base class injects these into the Phase 1 exploration prompt, ensuring the LLM never invents or hard-codes credential values.

**Setup steps** — `PersistedTestSet` can hold optional `SetupSteps` (`List<WebUiStep>`) and a `SetupStartUrl`. When present, the orchestrator injects them into `TestTask.Parameters["SetupSteps"]` and `["SetupStartUrl"]` alongside `PreloadedTestCases`. `ExecuteUiTestCaseAsync` runs setup steps before each test case: navigate to `SetupStartUrl`, execute setup steps (labelled `[setup N/M]`), then navigate to the test case's own `StartUrl` and execute its steps. This avoids duplicating login/auth steps in every test case. Setup steps are recorded via `--record-setup` CLI flag or edited in the web dashboard. If a setup step fails, remaining setup and all test case steps are skipped.

**Per-step reporting** — `ExecuteUiTestCaseAsync` reports each Playwright step as an individual `TestStep` in the results. Each step is labelled `"Test Case Name [N/Total] action"` (or `"Test Case Name [setup N/M] action"` for setup steps) with its own pass/fail status and duration. If a step fails, a screenshot is captured (when configured), remaining steps are marked "Skipped — previous step failed", and execution of that test case stops. Screenshots are captured for all failure types (Playwright errors, assertion failures, unexpected exceptions) and the filename is stored in the step's `Detail` field.

**Click execution** — `ExecuteUiStepAsync` uses a minimum 15 s timeout for `click` steps (regardless of the stored `timeoutMs`) because clicks frequently trigger form submissions and full-page navigations. If Playwright's actionability check stalls (e.g. a covering overlay), it auto-dismisses modal overlays via `TryDismissOverlaysAsync` (Escape key → common close-button selectors → JS DOM removal of `.modal-backdrop`, `.k-overlay`, `.mud-overlay`, `[role="dialog"]`, Kendo Windows, MudBlazor dialogs/drawers), then retries with `Force = true`, and finally falls back to a JS `el.click()` via `EvalOnSelectorAsync`. After every click, `WaitForSpaSettleAsync` checks for MudBlazor loading indicators (`.mud-progress-circular`, `.mud-skeleton`, `.mud-table-loading`) before waiting for `NetworkIdle`.

**click-icon** — For icon-only MudBlazor buttons (no text, no `aria-label`), CSS/XPath cannot query SVG `path[d]` attributes. The recorder captures the SVG icon's path prefix as a `click-icon` action with `Value = "svgPathPrefix|occurrenceIndex"`. During replay, `page.EvaluateAsync` uses JavaScript to iterate `querySelectorAll('svg path')`, match by `startsWith`, select the Nth occurrence, and click the parent `<button>`. Polls every 500 ms with a 15 s timeout to handle SPA navigation delays.

**wait-for-stable** — A `MutationObserver` injected via `page.AddInitScriptAsync` tracks DOM change timestamps. The `wait-for-stable` action uses `page.WaitForFunctionAsync` to wait until `Date.now() - lastChangeTimestamp > threshold` (default 1000 ms). Useful between SPA navigation clicks and element interactions where `NetworkIdle` resolves too early.

**Match-first assertions** — `WebUiStep.MatchFirst` (default `false`) wraps the locator with `.First` before asserting. When a selector matches multiple elements (common in data grids that accumulate rows over repeated test runs), strict mode would fail immediately even though the first element contains the expected text. Applies to `assert-text`, `assert-visible`, and `assert-hidden`. Editable via the "first" checkbox in the Web UI edit dialog.

**Fill execution** — After `FillAsync`, the step dispatcher dispatches explicit `input` and `keyup` events on the target element. Many JS-based components (e.g. jQuery `keyup` menu filters, Kendo search inputs) rely on keyboard events that `FillAsync` does not fire. A 500 ms pause follows to let debounced handlers update the DOM. A separate `type` action is available for character-by-character typing via `PressSequentiallyAsync`.

**Storage state & TOTP** — `BraveCloudUiTestAgent` saves browser cookies/localStorage to `BraveCloudUiStorageStatePath` after a successful SSO login. Subsequent calls within `BraveCloudUiStorageStateMaxAgeHours` pass this file to `browser.NewContextAsync()` via `StorageStatePath`, skipping the full Azure AD redirect flow. The path is resolved to absolute at DI startup (both Runner and WebApi share the same resolved path). When `BraveCloudUiTotpSecret` is configured (base32), the agent computes TOTP codes via OtpNet and enters them automatically during SSO. When empty and MFA is encountered: `PlaywrightHeadless=false` → waits 120 s for manual entry; `PlaywrightHeadless=true` → fails with remediation instructions. The CLI `--auth-setup` command provides a standalone way to perform SSO + 2FA manually and save the auth state.

**Test case persistence** — `TestObjective` has four step collections:
- `ApiSteps` (`List<ApiTestDefinition>`) — populated by API agents
- `WebUiSteps` (`List<WebUiTestDefinition>`) — populated by web UI agents and the Playwright recorder
- `DesktopUiSteps` (`List<DesktopUiTestDefinition>`) — populated by desktop UI agents and the desktop recorder
- `AseXmlSteps` (`List<AseXmlTestDefinition>`) — populated by `AseXmlGenerationAgent`

`TargetType` (string, default `"API_REST"`) is also stored so the orchestrator can reconstruct tasks with the correct `TestTargetType` on reuse.

`Source` (string, default `"Generated"`) tracks how the objective was created: `"Generated"` for AI/LLM-created objectives or `"Recorded"` for objectives captured via `--record`. Rebaseline is only available for generated objectives. Legacy JSON files without the field are backfilled in `MigrateLegacyObjective()` using the `recorded-` ID prefix heuristic.

#### Desktop UI Agent

`WinFormsUiTestAgent` extends `BaseDesktopUiTestAgent`, which uses FlaUI (UI Automation 3) to automate Windows Forms applications.

**App lifecycle** — The target application is launched via `Application.Launch(ProcessStartInfo)` with `WorkingDirectory` set to the exe's own directory (so sibling DLLs load correctly). Before every launch, a stale-process sweep kills any pre-existing instances whose exe path matches `TargetAppPath` (per-env, so a Sumo Bravo isn't killed during a Tesla test). Each test case starts from a fresh process — the previous case's app is closed via `app.Close()`, and if WM_CLOSE is blocked (modal dialog, unsaved-changes prompt), the executor force-kills the process tree (`Process.Kill(entireProcessTree:true)`) so child windows and any sibling processes are reaped. The final case is closed in a `finally` block. After clicks that change the window count (dialog close, new form open), the executor auto-waits 1.5s for the app to settle.

**Two-phase LLM generation** — Identical pattern to web UI agents:
1. **Phase 1 — Exploration**: `DesktopAutomationTools` SK plugin provides `snapshot()` (returns the UI Automation element tree with interactive elements), `click()`, `fill()`, `screenshot()`, and `list_windows()`.
2. **Phase 2 — JSON generation**: Observed element identifiers are injected as authoritative ground truth.

**Element resolution** — `DesktopElementResolver` uses a cascading fallback chain:
1. `AutomationId` (most stable — pure numeric IDs like window handles are auto-skipped)
2. `Name` property
3. `ClassName` + `ControlType` — only used when the match is **unambiguous** (single element). If multiple elements share the same class/type (e.g. several Edit text boxes on a form), falls through to TreePath
4. `TreePath` — positional indexed path from window root (e.g. `Pane[0]/Edit[3]`), critical for distinguishing sibling controls

During replay, elements are searched across progressively wider scopes: primary window → all top-level app windows → desktop root. This handles MDI applications (like Bravo) where controls may live inside child MDI forms. Assertions use a `QuickFindElement` (single-attempt, no retry) in a polling loop so text can be re-read every 500ms as it changes.

**Click execution** — Uses three strategies in order: (1) `InvokePattern` — most reliable for ToolStrip/ribbon buttons and toolbar items, (2) `element.Click()` — standard coordinate click, (3) `Mouse.Click(clickablePoint)` — raw mouse click fallback.

**Assertion polling** — `assert-text` polls every 500ms until the expected text appears or timeout expires (minimum 15s). This handles async operations where displayed text transitions through intermediate states (e.g. "Search is in progress" → "Search completed"). Text is extracted by searching the element itself, its children, and all descendant Text/Edit controls.

**Step model** — `DesktopUiStep` uses composite selectors (AutomationId, Name, ClassName, ControlType, TreePath) alongside **window-relative click coordinates** (`WindowRelativeX`, `WindowRelativeY`) and a **recording-paced delay** (`DelayBeforeMs`). Desktop-specific actions include `menu-navigate` (MenuBar traversal via MenuPath), `wait-for-window`/`switch-window`/`close-window` (window title matching), and `assert-enabled`/`assert-disabled`.

**Design principle — coordinates are the source of truth for clicks.** Many legacy WinForms controls (Infragistics / DevExpress ribbons, custom checked-combo popups, owner-drawn menus) render visually but don't expose themselves to UI Automation. UIA tree enumeration can't find them, but `FromPoint` hit-testing and raw `Mouse.Click` still work because WinForms processes mouse events at the pixel level. The replay engine therefore **clicks the recorded pixel via `Mouse.Click`** rather than relying on the UIA element, using UIA only as a readiness probe (poll `FromPoint` until the hit element's Name matches before clicking). This single rule dissolves a whole class of UIA-opaque-control failures at once — ribbon buttons, popup checkboxes, name collisions, disabled-at-click-time controls all work uniformly. See `.claude/commands/desktop-winui-reference.md` for the full rationale.

**Desktop recording** — `DesktopRecorder` uses low-level Windows hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) via P/Invoke with an explicit `PeekMessage`/`TranslateMessage`/`DispatchMessage` message pump (required for hook callbacks to fire). Key recording behaviours:
- **Click capture**: `automation.FromPoint()` resolves the clicked element. Window chrome (title bar, scroll bars) and system UI elements (taskbar buttons, shell tray, UWP app IDs) are automatically filtered out.
- **Container-hit refinement**: if `FromPoint` returned a generic container (`ToolBar`/`Pane`/`Group`/`Custom`), `RefineContainerHit` walks descendants looking for an actionable child (`Button`/`MenuItem`/`Hyperlink`/`CheckBox`/`SplitButton`) whose `BoundingRectangle` contains the click pixel. Captures the specific child even when UIA's hit-test returned the parent (disabled button, click a few pixels off the icon).
- **Coordinate capture**: pixel offset relative to `FindLargestVisibleWindow(processId)` — a fresh scan of visible top-level windows picking the largest-area one. Consistent with replay so offsets round-trip. **Do not use** `Process.MainWindowHandle` (picks tiny helpers at (0,0)) or `GetForegroundWindow()` (returns transient popups).
- **Inter-step delay capture**: `DelayBeforeMs` = delta from the previous captured step's timestamp, so the user's recorded pace (waiting for search, menu animation, dialog load) is preserved.
- **Keyboard capture**: Consecutive keystrokes are coalesced into `fill` steps. Special keys (Enter, Tab, Escape) are recorded as `press` steps.
- **Ctrl+V paste**: Detected via `GetKeyState(VK_CONTROL)`. The actual clipboard content is read on an STA thread and recorded as the fill value (not just the "V" keystroke). Ctrl+A/C/X are silently ignored.
- **Selector building**: `DesktopElementResolver.BuildSelector()` populates all available properties. Pure numeric AutomationIds (window handles) and default WinForms designer names (`textBox1`, `button2`) are skipped as unstable.
- **Assertion capture**: When the user presses T/V/E in the console, the next click is converted to an assertion. Text extraction uses the stored element reference directly (not a re-search from stale `mainWindow`) and searches children/descendants for text.
- **Post-recording validation**: Warns about TreePath-only selectors, consecutive clicks without waits, and missing assertions.

**Desktop replay** — `DesktopStepExecutor.ExecuteClick` gates behaviour on whether the step has coords:
- **With coords (modern recordings)**: runs UIA lookup as a readiness probe only (polls `FromPoint` until the hit element's Name matches the recorded Name or the step's `TimeoutMs` expires), then clicks via `Mouse.Click(recordedScreenX, recordedScreenY)`. Ampersand mnemonics are stripped for name comparison (`&Yes` matches `Yes`).
- **Without coords (legacy recordings)**: falls back to UIA-element click with the full strategy stack — `ExpandCollapsePattern.Expand()` for combo dropdowns, `InvokePattern` for Button/Hyperlink/MenuItem, right-edge click for combo-shaped Panes (`TryClickDropdownArrow`), `element.Click()`, and finally `Mouse.Click(clickablePoint)`. `InvokePattern` is skipped for `Pane`/`Window`/`Group`/`Custom` control types because Invoke on a container is often a silent no-op.
- **Before every step**: sleeps `step.DelayBeforeMs` (capped at 30,000 ms) so the recording's natural pauses reproduce.

**React UI** — `DesktopUiTestCaseTable` displays desktop test cases with step count and action preview. `EditDesktopUiTestCaseDialog` provides a full step editor with five cascading selector fields (AutomationId, Name, ClassName, ControlType, TreePath), action-specific context fields (Value, MenuPath, WindowTitle), and step add/remove/reorder controls. The dialog is generic: its props (`definition`, `caseName`, `onSave`, `onDelete`, `title`, `deleteLabel`, `deleteConfirmMessage`) mirror `EditWebUiTestCaseDialog`, so callers plug in their own persistence. Reused in two places: standalone desktop test cases (`DesktopUiTestCaseTable` saves via `updateObjective`) and post-delivery UI verifications (`AseXmlDeliveryTestCaseTable` saves via `updateVerification` with the definition wrapped back into the enclosing `VerificationStep.desktopUi`). This mirrors the earlier Web UI alignment where `EditWebUiTestCaseDialog` serves both standalone objectives and aseXML verifications.

> **Note:** `PersistedTaskEntry` is **deprecated** (v1 schema only). It is retained solely for deserializing legacy test set files during migration. New code should use `TestObjective` exclusively.

#### PlaywrightRecorder

`PlaywrightRecorder.RecordAsync` provides a human-driven alternative to LLM generation. It:
- Launches non-headless Chromium (`--start-maximized`, `SlowMo = 50`). MVC targets use `NoViewport` (maximized window); Blazor targets use 1920×1080 to match the replay viewport. Both MVC and Blazor replay at 1920×1080. Optionally loads a `StorageStatePath` for authenticated recording sessions.
- Calls `page.ExposeFunctionAsync("aitcRecordStep", ...)` — JS→.NET bridge, survives page navigation
- Calls `page.ExposeFunctionAsync("aitcStopRecording", ...)` — signals a `TaskCompletionSource`
- Calls `page.AddInitScriptAsync(...)` — re-injects event listeners and overlay panel on every page load (deferred via `DOMContentLoaded` so `document.body` is ready)

**Event capture:**
- **`input` events** on form fields (→ `fill`) — captured as the user types (not on blur), ensuring fills are recorded before any subsequent click. Native `change` is used only for `<select>` elements.
- **jQuery-delegated `change`** on `input[data-role="(date|datetime|time)picker"]` (→ `fill`) — Kendo commits DatePicker/DateTimePicker/TimePicker values via `$(el).trigger('change')`, which does **not** dispatch a native DOM change event. A polled `window.jQuery(document).on('change', 'input[data-role*="picker"]', …)` listener captures the formatted text (e.g. `"1/01/2026 12:00 AM"`) so Playwright's `FillAsync` can parse it back into the widget on replay.
- **`click` events** (→ `click`) — with Kendo-aware special cases: PanelBar group header clicks resolve to `text="GroupName"` via `bestSelectorForPanelBarHeader()`; Kendo Window close buttons become `press Escape`; clicks inside a DatePicker popup (`.k-animation-container[id$="_dateview"]` / `_timeview`, `.k-calendar`, `.k-calendar-container`) are **suppressed** — the jQuery picker listener above records the final committed value as a single `fill` step on the input.
- **`keydown` events** — captures Escape key presses (modal dismissal).

**Selector computation** uses `bestSelector(el)` with an extended fallback chain:
`#stableId → tag[name] → tag[type="submit"] → tag[aria-controls] → tag[aria-label] → tag[title] → a[href*="rawHref"] → MudBlazor text → tag[data-*] → tag[role] → tag.uniqueClass → text="ownText" → text="innerText" → grid row context → SVG icon fingerprint → tag`.
- IDs matching dynamic/stateful patterns (`_active`, `_pb_`, `_wnd_`, GUIDs) are skipped.
- `aria-controls` is checked at priority #4a (before `aria-label`) and required to be globally unique. Kendo widget triggers (ComboBox arrow, DatePicker calendar icon) set this to the popup's stable id — e.g. `aria-controls="Endpoint_listbox"` or `aria-controls="CreatedFromDate_dateview"` — which uniquely identifies the owning widget. Generic `aria-label` values like `"select"` or `"Open the date view"` repeat across every widget of the same type and would collide.
- `aria-label` is checked at priority #4b — MudBlazor icon buttons like "Notifications", "Sort", "Column options" use this. Labels starting with "Toggle " are skipped (nav group headers use text instead).
- Link selectors use the **raw `getAttribute('href')`** value (not resolved URL) because CSS `[href*=]` matches raw attributes. Blazor renders relative hrefs (e.g. `./Security/UserSearch`).
- MudBlazor text detection checks `.mud-nav-link-text` and `.mud-button-label` child elements.
- Priority #9 (role) skips `menuitem`, `menu`, `group`, and **`option`** — all four repeat across the page (every PanelBar entry, every ComboBox item). Letting `option` fall through to text (priority #11) combined with the listbox scope wrapper (below) gives stable unique selectors like `#Endpoint_listbox >> text="Gateway SPARQ (GatewaySPARQ)"`.
- Grid row action buttons use Playwright chained selectors: `tr:has-text("rowId") >> td[data-label="Actions"] >> button >> nth=N`.
- Icon-only buttons (no text, no label) use SVG path fingerprinting via `click-icon` action with occurrence index.
- MudBlazor state classes (`mud-ripple`, `mud-expanded`, `mud-nav-link`, `mud-icon`, `mud-svg`) and Kendo state classes (`k-state-*`) are excluded from class-based selectors.

**Kendo listbox scope wrapper** — after `bestSelector` computes a raw selector, if the element lives inside `ul[id][role="listbox"]` (e.g. `#Endpoint_listbox`) or `.k-list-container[id]` / `.k-popup[id][data-role="popup"]`, the selector is prefixed with the container id: `#Endpoint_listbox >> text="Gateway SPARQ"`. Without this scope, `li[role="option"]` and option text match items across every ComboBox/DropDownList on the page, so replay clicks the wrong option. Skipped when the raw selector is already an id (`#id`), an icon sentinel (`__icon__…`), or already chained (`>>`).

**Click walker** — walks up from the click target to the nearest meaningful interactive element: `<button>` / `<input>`, `<a>` with a real href, or any element with `[aria-controls]` (Kendo widget triggers — the inner chevron icon inside an arrow span resolves to the controlling `[aria-controls]` element so the trigger click gets captured). Falls back to the original target if no interactive ancestor is found.

**MudBlazor click shortcut** — Before the general walk-up logic, clicks on or inside `.mud-nav-link-text`, `.mud-button-label`, or `.mud-treeview-item-body` are captured immediately as `text="Label"` selectors, bypassing the ancestor traversal.

**Post-recording validation** — After recording, `ValidateRecordedSteps` warns about: weak selectors (bare tag names), duplicate `click-icon` SVG prefixes, missing assertions, and consecutive clicks without waits (SPA timing risk).

**SPA DOM stability** — A `MutationObserver` in the init script tracks the last DOM change timestamp via `window.__aitcLastDomChangeTs()`. This is used by the `wait-for-stable` replay action.

The overlay panel (fixed, bottom-right, dark theme) provides:
- **+ Assert current URL (path)** — records `assert-url-contains` with `location.pathname`
- **+ Assert page title (title)** — records `assert-title-contains` with `document.title`
- **+ Assert element…** — enters **pick mode** for element-level assertions (see below)
- **Save & Stop** — signals `aitcStopRecording()`

**Element assertion pick mode** lets the user point-and-click on any DOM element to create an assertion:
1. Clicking the button activates pick mode — the cursor changes to crosshair and a green translucent highlight follows the mouse.
2. Clicking an element opens a context menu with assertion options: **Assert text contains** (`assert-text` with `innerText`), **Assert value equals** (`assert-text` with `el.value`, form fields only), **Assert is visible** (`assert-visible`), **Assert is hidden** (`assert-hidden`).
3. Selecting an option records the step and returns to normal recording. Escape cancels pick mode.
4. All event handlers (input, change, click, keydown) are suppressed during pick mode to prevent accidental step recording.

Duplicate `fill` steps on the same selector are deduplicated (update-in-place). Session ends on Save & Stop, browser close, or 15-minute timeout.

---

#### aseXML Generation Agent

`AseXmlGenerationAgent` renders AEMO B2B aseXML payloads from templates. Unlike the other agents, which execute *against* a target system, this agent's output is a set of files on disk — the produced XML is later delivered (Phase 2) and its effects validated (Phase 3) by separate agents.

**Template catalogue** — Each transaction type gets a subfolder under `templates/asexml/{TransactionType}/` holding one or more `{templateId}.xml` bodies paired with `{templateId}.manifest.json` field specs. Both `.csproj` files (`Runner` and `WebApi`) contain a `<Content Include="..\..\templates\asexml\**\*.*" ... CopyToOutputDirectory="PreserveNewest" />` entry so the templates land in `bin/.../templates/asexml/` at build time and are discovered by `TemplateRegistry.LoadFrom` at startup.

**Manifest field semantics** — each field in the manifest has a `source`:
- `auto` — generated at render time via a named generator (`messageId`, `transactionId`, `nowOffset`, `today`). Patterns use `{rand8}` for 8-char alphanumeric substitution. Cannot be overridden.
- `user` — supplied via the `AseXmlTestDefinition.FieldValues` dictionary. `required: true` fields cause a failing step when missing. `example` is shown to the LLM and (later) used as a UI placeholder.
- `const` — hardwired in the template; surfaced for display only.

`FieldSpec` also supports two optional, additive properties:
- `description` — per-field guidance surfaced to the LLM via `AseXmlGenerationAgent.BuildCatalogueForLlm` (used for fields whose structure is non-obvious, e.g. a multi-line CSV body that carries its own sub-grammar).
- `format` — post-render validator key. Currently wired: `"nem12"` → `Nem12CsvValidator` (grammar check of the 100/200/300/400/500/900 record structure, IntervalLength-aware 300-record width, quality-flag regex, 400 range sanity). The renderer runs the validator on the resolved (pre-escape) value after the XML well-formedness check; a violation throws `AseXmlRenderException` with the template and field name in the message. New formats = one enum-like switch arm in `AseXmlRenderer.Render` plus a stateless validator class.

**LLM writes values, never XML** — `GenerateTestCasesAsync` asks the LLM to pick a `templateId` and fill `fieldValues`. The resulting `AseXmlTestCase` is passed to `AseXmlRenderer.Render(manifest, body, userValues)` which is a pure function: applies generators to `auto` fields, enforces `required` on `user` fields, substitutes `const` values, runs a single regex pass `\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}` for token substitution with proper XML escaping, and verifies well-formedness via `XDocument.Parse`. An unknown token (template references a name not in the manifest) is a hard failure — prevents silent malformed output from a typo drift between template and manifest.

**Reuse mode** — only the `AseXmlTestDefinition` (templateId + user field values) is persisted. On reuse, generators fire again, so each run gets a fresh `MessageID`/`TransactionID`/timestamps — matching how real transaction streams behave.

**Output** — Rendered XML is written to `output/asexml/{yyyyMMdd_HHmmss}_{taskId}/{NN}-{safeName}.xml`. Each file becomes one passing `TestStep` with the resolved field values and generated IDs in `Detail` for inspection. `Metadata["outputDir"]` surfaces the run directory for future Phase 2 delivery chaining.

**Extensibility** — new transaction types are content-only changes (drop a new `{template}.xml` + `.manifest.json` pair; no recompile needed to discover, just a restart of Runner/WebApi). New auto-field generators are one method in `FieldGenerators.cs` plus a `switch` arm in `Generate(spec)`.

---

#### aseXML Delivery Agent

`AseXmlDeliveryAgent` extends the generation story end-to-end: it renders an aseXML payload and uploads it to a Bravo inbound drop location resolved from the target Bravo application's database. Target type: `AseXml_Deliver`.

**Three-layer design.** The agent orchestrates three small components that can be tested and swapped independently:

1. **`IEndpointResolver`** (`BravoEndpointResolver`) — queries `mil.V2_MIL_EndPoint` by `EndPointCode` via `Microsoft.Data.SqlClient`. Returns a `BravoEndpoint` record with `FtpServer`, `UserName`, `Password`, `OutBoxUrl`, and `IsOutboundFilesZipped`. Caches per-code lookups in a `ConcurrentDictionary` for process lifetime (restart to refresh) and caches the full code list on first call to `ListCodesAsync()`.
2. **`IXmlDropTarget`** (`SftpDropTarget` / `FtpDropTarget`) — uploads a `Stream` to the resolved endpoint. The SFTP implementation wraps SSH.NET's `SftpClient`; the FTP implementation wraps FluentFTP's `AsyncFtpClient`. Both parse host/port from `FTPServer`, strip any scheme from `OutBoxUrl`, ensure the remote directory exists, upload, and verify the remote file is present. `DropTargetFactory.DetectScheme(outBoxUrl, ftpServer)` picks `sftp` vs `ftp`; defaults to SFTP when no scheme is present (matches Bravo's current convention).
3. **`XmlZipPackager`** — a static helper using `System.IO.Compression.ZipArchive` (no NuGet). When the endpoint has `IsOutboundFilesZipped = true`, the agent calls `XmlZipPackager.Package(xml, "{MessageID}.xml")` and uploads the resulting `{MessageID}.zip` instead of the raw `.xml`. Both the raw XML and the zip are written to the local debug output directory so developers can see exactly what left the machine.

**Data teardown** (sibling to the delivery pipeline). `BravoTeardownExecutor` (`ITeardownExecutor`) runs user-defined SQL `DELETE`/`UPDATE` statements attached to a test set (`PersistedTestSet.TeardownSteps`), once per objective, before the agent dispatches.

- **Mode gating.** Teardown only fires in `RunMode.Reuse`. `VerifyOnly` is deliberately excluded so re-running post-delivery verifications doesn't delete the very rows being verified. `Normal` and `Rebaseline` are also excluded (no prior data to clean on initial creation/regeneration). The `--skip-teardown` flag forces a bypass on any run.
- **Connection.** Shares the `IEnvironmentResolver.ResolveBravoDbConnectionString` path with `BravoEndpointResolver`, so each customer env hits its own DB.
- **Per-env opt-in.** Gated by `EnvironmentConfig.DataTeardownEnabled` (default `false`); attempting a run without opt-in fails fast before any connection opens.
- **Guardrails** live in `SqlGuardrails` — `WHERE` required (word-boundary check), destructive-keyword denylist (`TRUNCATE`/`DROP`/`ALTER`/`CREATE`/`EXEC`/`EXECUTE`/`SHUTDOWN`/`GRANT`/`REVOKE`/`MERGE`), with line/block comments stripped first so they can't conceal keywords. Validation runs at both save time (in `ModuleEndpoints.PUT teardown-steps`) and run time (in the executor).
- **Token resolution.** `TokenSubstituter.Substitute(..., throwOnMissing: true)` fills `{{Token}}` placeholders from a three-source merge built by `TestOrchestrator.BuildTeardownContextAsync` (in ascending precedence): (1) the **prior successful run's** delivery context pulled via `IExecutionHistoryRepository.GetLatestDeliveryContextAsync` (MessageID, TransactionID, Filename, EndpointCode, RemotePath, UploadedAs); (2) the objective's `EnvironmentParameters`; (3) the first delivery step's `FieldValues`. The history merge is what makes `{{TransactionID}}`-based cleanup work — it resolves to the previous run's value, which is exactly what the new run needs to delete before re-inserting under a fresh ID. On the first run for a brand-new objective, history-only tokens are absent and strict-mode substitution fails loudly.
- **Failure isolation.** A failed teardown marks only that one objective `Error`; subsequent objectives in the test set continue. Within an objective, the first failed step aborts the rest (no partial cleanup against an unknown DB state). Outcomes are attached to `PersistedObjectiveResult.TeardownResults` for audit and surfaced in the execution history JSON.
- **CLI flags.** `--teardown-dry-run` (log substituted SQL with no execution) and `--skip-teardown` (bypass entirely).

**ExecuteAsync pipeline (per case)**:

```
render[N]        → AseXmlRenderer (reused from generation)
resolve-endpoint → IEndpointResolver.ResolveAsync(code)
[package[N]]     → XmlZipPackager.Package(xml, "{MessageID}.xml")   — only when IsOutboundFilesZipped
upload[N]        → DropTargetFactory.Create(endpoint).UploadAsync(endpoint, remoteFileName, stream)
```

Remote filename is `{MessageID}.xml` (or `{MessageID}.zip` when zipped) — matches the AEMO sample convention (`MSRINB-MFN-49635377-DD.xml`).

**Endpoint selection**. Three sources, in precedence order:

1. `task.Parameters["EndpointCode"]` — populated by the `--endpoint` CLI flag (highest precedence).
2. `AseXmlDeliveryTestDefinition.EndpointCode` — the LLM-extracted or user-saved per-case value.
3. Saved `PersistedTestSet.EndpointCode` on reuse, flowed into the task by `TestOrchestrator.RunAsync`.

When all three are empty, the `resolve-endpoint` step fails fast with a clear message — no SQL call is attempted. When a code is supplied but not found, the step fails with an explicit "not found" message that does not leak SQL error detail.

**Security discipline**. The `Password` field on `BravoEndpoint` is never logged or surfaced in step details. Step summaries and the log stream include `EndPointCode`, `UserName`, host, `OutBoxUrl`, and the zip flag, but not the password. The Bravo DB connection string lives in `appsettings.json` (gitignored) — never in `appsettings.example.json`.

**Self-contained design choice**. The delivery agent renders the XML itself rather than consuming output from a preceding `AseXml_Generate` task. This avoids activating `TestTask.DependsOn` in the orchestrator (declared but currently unused) and keeps Phase 2 scope tight. The trade-off is that the generation agent remains available for render-only flows, while the delivery agent handles the render+deliver lifecycle as one atomic test step. Phase 3 (Wait + UI validate) will revisit the chaining question when it genuinely needs a multi-stage pipeline.

**Extensibility — new protocols**. Adding a new upload protocol (AS2, HTTP POST, SMB, etc.) is a drop-in: implement `IXmlDropTarget`, add a `switch` arm to `DropTargetFactory.Create(endpoint)` matching the appropriate scheme prefix, and register the new class in DI if it has constructor dependencies beyond a logger. The agent, renderer, resolver, and persistence layer are untouched. Keep the set of implementations small — only add one when a real endpoint needs it.

**Phase 3 — post-delivery UI verifications**. A delivery case can own `PostDeliveryVerifications: List<VerificationStep>`. After a successful upload, the delivery agent:

1. **Builds the substitution context** from the render — `MessageID`, `TransactionID`, `Filename`, `EndpointCode`, `UploadedAs`, plus every resolved manifest field (NMI, MeterSerial, DateIdentified, etc.). Dedicated delivery keys win over any name collision from the render.
2. **Waits** `VerificationStep.WaitBeforeSeconds` seconds (logged as a `wait[N.V]` step). Phase 3 ships fixed-delay only; the field stays forward-compatible for future polling strategies.
3. **Dispatches to a sibling agent** — `AseXmlDeliveryAgent` receives `IEnumerable<ITestAgent>` in its constructor and filters itself out at assignment. For each verification, it looks up the first sibling whose `CanHandleAsync` returns true for the verification's target (`UI_Web_MVC`, `UI_Web_Blazor`, `UI_Desktop_WinForms`), deep-clones the verification's `WebUiTestDefinition` or `DesktopUiTestDefinition`, runs every string field on every step through `TokenSubstituter.Substitute`, and calls `sibling.ExecuteAsync(syntheticTask)` with the substituted case pre-loaded. The sibling's returned `TestStep`s are prefix-labelled `verify[N.V]` and appended to the delivery's own step list.
4. **Token substitution surface** — every string field on `WebUiStep` (Selector, Value) and `DesktopUiStep` (AutomationId, Name, ClassName, ControlType, TreePath, Value, MenuPath, WindowTitle) as well as `WebUiTestDefinition.StartUrl` participates. Unknown `{{Tokens}}` at playback are left literal and logged as a WARN — never silently substituted with an empty string.

**Recording flow — `--record-verification`**. The CLI handler loads the target delivery objective, reads its `FieldValues` directly, and calls `ExecutionHistoryRepository.GetLatestDeliveryContextAsync(testSetId, moduleId, objectiveId)` for the latest-successful-run's MessageID / TransactionID / Filename / EndpointCode. The combined context is passed to `VerificationRecorderHelper` which post-processes the recorder's output: literal values matching any context value get replaced with `{{Key}}` placeholders, subject to the heuristics in `VerificationRecorderHelper` (min length 4, longest-match-first, exact substring, first-key-wins on collisions).

**Verify-only mode** (`RunMode.VerifyOnly`). Skips the render/upload pipeline and re-runs only the post-delivery UI verifications. The orchestrator routes VerifyOnly like Reuse (loads the saved test set, filters to the specified objective) but injects `"VerifyOnly": true`, `"TestSetId"`, `"ModuleId"`, and an optional `"VerificationWaitOverride"` into the task parameters. The delivery agent's `VerifyOnlyAsync` method resolves the verification context by merging the test case's `FieldValues` with `ExecutionHistoryRepository.GetLatestDeliveryContextAsync` (MessageID, TransactionID, Filename, EndpointCode from the latest successful delivery), then calls the existing `RunVerificationAsync` for each step. If no prior successful delivery exists, the agent returns `TestStatus.Error` with a descriptive message. The `--wait` CLI flag (or `VerificationWaitOverride` on the WebApi `RunRequest`) overrides `WaitBeforeSeconds` on all verification steps — the React UI sends `verificationWaitOverride: 0` by default (file already processed).

**Execution history persistence**. `PersistedObjectiveResult` gained a `Deliveries: List<PersistedDelivery>?` field in Phase 3 — the delivery agent's `TestResult.Metadata["deliveries"]` is projected into typed records (MessageID, TransactionID, EndpointCode, RemotePath, UploadedAs, Bytes, Status). Non-delivery objectives serialise with `Deliveries = null` so files aren't bloated with empty arrays. `GetLatestDeliveryContextAsync` reads this to seed the recorder.

**Why sibling-dispatch over `TestTask.DependsOn`**. The orchestrator's parallel-fanout execution loop doesn't honour `DependsOn` (the field is declared but currently unused). Sibling dispatch keeps the entire Generate → Deliver → Wait → multi-UI-verify flow inside a single logical task and a single `TestObjective`, which matches the user's mental model ("the verifications are steps in the same test case as the delivery") without the broader orchestrator refactor that activating `DependsOn` would require. If future phases genuinely need independent chained tasks, that's where to go.

**DI lazy sibling resolution**. `AseXmlDeliveryAgent` is registered both as its concrete type and as `ITestAgent` (so the orchestrator can discover it). An eager `IEnumerable<ITestAgent>` constructor arg would therefore recurse into the agent's own factory when DI resolves the enumerable. The agent takes `IServiceProvider` instead and caches `_services.GetServices<ITestAgent>().Where(a => a is not AseXmlDeliveryAgent)` on first use. See `AseXmlDeliveryAgent.Siblings`.

**Verification CRUD endpoints**. Recorded verifications are also editable/deletable from the web UI via:

- `DELETE /api/modules/{mod}/testsets/{ts}/objectives/{obj}/deliveries/{dIdx}/verifications/{vIdx}` — removes one verification at the given index.
- `PUT ...` — replaces the verification in place (body = `VerificationStep`). Used by the generic `EditWebUiTestCaseDialog` when saving.

Both endpoints live in `src/AiTestCrew.WebApi/Endpoints/ModuleEndpoints.cs` alongside the objective-level CRUD.

**Generic `EditWebUiTestCaseDialog`**. `ui/src/components/EditWebUiTestCaseDialog.tsx` is data-shape-agnostic — it takes `definition: WebUiTestDefinition`, `caseName: string`, `onSave({name, definition})`, and optional `onDelete()` / `title` / `deleteLabel` / `deleteConfirmMessage`. Two consumers share it:

1. **Standalone Web UI test cases** (`WebUiTestCaseTable`) — wraps save with `updateObjective`, delete with either `deleteObjective` (last step) or `updateObjective` with the step removed.
2. **Post-delivery verifications** (`AseXmlDeliveryTestCaseTable`) — wraps save with the verification `PUT`, delete with the verification `DELETE`.

Adding a third consumer (e.g. a hypothetical "edit recorded setup step" flow) is a props change, not a new dialog.

**Auth state for verification recording**. `--record-verification --target UI_Web_MVC` passes `LegacyWebUiStorageStatePath` to the recorder; `UI_Web_Blazor` passes `BraveCloudUiStorageStatePath`. This mirrors how the standalone agents use those paths at execution time, so captured verifications never include login flows (assuming the user ran `--auth-setup --target <UI_*>` first). The CLI prints a hint if neither storage state is configured.

---

### AiTestCrew.Orchestrator

Decomposes objectives, routes tasks to agents, aggregates results, manages run modes. References Core and Storage (no longer depends on Agents).

```
Orchestrator/
  TestOrchestrator.cs   — RunAsync, DecomposeObjectiveAsync, SaveTestSetAsync
```

---

### AiTestCrew.Runner

CLI entry point. Wires up DI, handles argument parsing, drives the run, renders output. References all other projects.

```
Runner/
  Program.cs                       — Top-level statements: arg parsing, DI, console output.
                                     The --record / --record-setup / --record-verification / --auth-setup
                                     short-circuits now call IRecordingService (Agents/Recording/) rather
                                     than inlining the Playwright/FlaUI logic — the service is constructed
                                     via a pre-host CreateRecordingService(loggerFactory) helper so it works
                                     before the DI host is built.
  AnthropicChatCompletionService.cs — Bridges Anthropic.SDK to Semantic Kernel's IChatCompletionService
  FileLoggerProvider.cs            — Writes all log messages to a timestamped file in logs/
  RemoteRepositories/
    RemoteHttpClient.cs            — Shared HttpClient wrapper for WebApi calls (X-Api-Key header injection)
    ApiClientModuleRepository.cs   — IModuleRepository over HTTP (remote mode)
    ApiClientTestSetRepository.cs  — ITestSetRepository over HTTP (remote mode)
    ApiClientExecutionHistoryRepository.cs — IExecutionHistoryRepository over HTTP (remote mode)
  appsettings.json                 — Runtime configuration (copied to output directory on build)
  appsettings.example.json         — Template with placeholder values for source control
```

**`--record` mode** runs before the DI host is built (no Orchestrator or agents needed). It resolves the module ID and test set ID via `SlugHelper.ToSlug` so the saved file path matches what the WebApi expects, then creates the module manifest via `ModuleRepository` if it does not exist. The actual recording + persistence logic lives in `IRecordingService.RecordCaseAsync` (`src/AiTestCrew.Agents/Recording/RecordingService.cs`), which is shared with the agent queue's `JobKind = Record` path.

**`--record-setup` mode** also runs before the DI host. It delegates to `IRecordingService.RecordSetupAsync`, which invokes `PlaywrightRecorder.RecordAsync` and saves the captured steps into `PersistedTestSet.SetupSteps` and `SetupStartUrl` instead of creating a new `TestObjective`. These setup steps (typically login) run before every test case in the test set during replay.

**`--record-verification` / `--auth-setup` modes** follow the same pattern — CLI validates args, builds the matching request record, and calls `IRecordingService`. The service is the single source of truth for all four flows, which is what lets the Phase 4 queue dispatch recording jobs to a remote agent without duplicating code.

**Remote mode** — When `ServerUrl` is configured in `TestEnvironmentConfig`, Runner registers `ApiClient*Repository` implementations instead of file-based or SQLite repositories. All persistence operations (module CRUD, test set load/save/merge, execution history) are proxied over HTTP to the WebApi. The `ApiKey` config field is injected as `X-Api-Key` on every request via `RemoteHttpClient`. This enables headless Runner instances on separate machines to share a central WebApi server.

**`--migrate-to-sqlite`** — One-shot migration CLI command that reads all file-based modules, test sets, and execution runs, and inserts them into the SQLite database. The command exits after migration.

---

### AiTestCrew.WebApi

REST API backend for the React UI. Mirrors Runner's DI wiring but exposes HTTP endpoints instead of a CLI. In production, co-hosts the React SPA from `wwwroot/` via `UseDefaultFiles()` + `UseStaticFiles()` + `MapFallbackToFile("index.html")`.

**Configuration source chain** (lowest to highest precedence):
1. Built-in `appsettings.json` (shipped with the app, minimal)
2. Runner's `appsettings.json` (for dev — `dotnet run --project src/AiTestCrew.WebApi` picks up the Runner's fuller config)
3. `C:/config/appsettings.json` or `$AITESTCREW_CONFIG_PATH` (for Docker — volume-mounted, lets you edit config without rebuilding the image)
4. Environment variables prefixed `AITESTCREW_` (e.g. `AITESTCREW_TestEnvironment__LlmApiKey`, `AITESTCREW_TestEnvironment__AseXml__BravoDb__ConnectionString`)

```
WebApi/
  Program.cs                       — DI wiring, CORS, minimal API endpoints, migration, screenshot static files,
                                     frontend co-hosting, auth middleware registration, storage provider selection
  AnthropicChatCompletionService.cs — Copy of Runner's bridge (same layer, can't reference Runner)
  Middleware/
    ApiKeyAuthMiddleware.cs        — Validates X-Api-Key header against IUserRepository.
                                     Only active when IUserRepository is registered (SQLite mode).
                                     Skips auth for: GET /api/auth/status, POST /api/users (bootstrap),
                                     static files, and health check.
  Endpoints/
    ModuleEndpoints.cs             — Module CRUD + nested test set and run access + per-objective status
    TestSetEndpoints.cs            — Legacy flat test set endpoints (backward compat)
    RunEndpoints.cs                — POST /api/runs (trigger with optional objectiveId, apiStackKey, apiModule), GET /api/runs/{id}/status (poll)
    AuthEndpoints.cs               — GET /api/auth/status, user CRUD (GET/POST/DELETE /api/users/*)
    AgentEndpoints.cs              — Agent register/heartbeat/deregister/list (Phase 4, SQLite only)
    QueueEndpoints.cs              — /api/queue/* — agent claims jobs, reports progress/result; dashboard list + cancel (Phase 4, SQLite only)
    ChatEndpoints.cs               — POST /api/chat/message + per-user conversation CRUD (GET/POST/DELETE/PATCH /api/chat/conversations[/{id}])
    RecordingEndpoints.cs          — POST /api/recordings — enqueues a recording/auth-setup job for a local agent (JobKind = Record | RecordSetup | RecordVerification | AuthSetup)
  Models/
    Chat/ChatModels.cs             — ChatMessage / ChatRequest / ChatResponse / ChatAction DTOs + ConversationSummary / ConversationDetail / PersistedChatMessage
  Services/
    IRunTracker.cs                 — Interface for individual run tracking (HasActiveRunForTestSet)
    IModuleRunTracker.cs           — Interface for module-level composite run tracking (HasActiveModuleRunForModule)
    RunTracker.cs                  — In-memory IRunTracker implementation (ConcurrentDictionary)
    ModuleRunTracker.cs            — In-memory IModuleRunTracker implementation (ConcurrentDictionary)
    AgentHeartbeatMonitor.cs       — BackgroundService that marks agents Offline when heartbeat goes stale
    RunDispatchHelper.cs           — Decides whether a run must be enqueued for a local agent (UI targets) or run in-process
    ChatIntentService.cs           — Loads/persists conversation history from IChatConversationRepository, builds catalog (modules, test sets, envs, stacks, endpoints, agents, current-test-set objectives), calls IChatCompletionService, deserialises ChatResponse via LlmJsonHelper. Auto-titles new threads from the first user message.
  appsettings.example.json         — Template config
```

**Frontend co-hosting** — WebApi serves the built React SPA from `wwwroot/` for single-binary deployment. `UseDefaultFiles()` maps `/` to `index.html`, `UseStaticFiles()` serves JS/CSS assets, and `MapFallbackToFile("index.html")` handles client-side routing. The frontend uses relative `/api` paths. During development, Vite's dev server proxy forwards `/api` requests to the WebApi.

**Auth middleware** — `ApiKeyAuthMiddleware` validates the `X-Api-Key` header against `IUserRepository` on every request (except allowlisted paths). Auth is only active when `IUserRepository` is registered in DI — this happens only in SQLite storage mode. Bootstrap: the first user can be created via `POST /api/users` without auth when no users exist.

**Concurrent runs** — The original global single-run lock was replaced with per-test-set/per-module locking. `IRunTracker.HasActiveRunForTestSet()` and `IModuleRunTracker.HasActiveModuleRunForModule()` prevent duplicate runs on the same target while allowing independent targets to run simultaneously.

**REST API endpoints:**

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/api/modules` | List all modules with test set counts |
| `POST` | `/api/modules` | Create a module |
| `GET` | `/api/modules/{id}` | Module detail |
| `PUT` | `/api/modules/{id}` | Update module name/description |
| `DELETE` | `/api/modules/{id}` | Delete module (cascades: removes every test set + its execution runs, then the module). `409` if a module run is active. |
| `GET` | `/api/modules/{id}/testsets` | List test sets in module |
| `POST` | `/api/modules/{id}/testsets` | Create empty test set |
| `GET` | `/api/modules/{id}/testsets/{tsId}` | Test set detail |
| `DELETE` | `/api/modules/{id}/testsets/{tsId}` | Delete test set (cascades to runs) |
| `GET` | `/api/modules/{id}/testsets/{tsId}/runs` | Run history |
| `GET` | `/api/modules/{id}/testsets/{tsId}/runs/{runId}` | Run detail |
| `POST` | `/api/modules/{id}/testsets/{tsId}/move-objective` | Move objective to another test set |
| `PUT` | `/api/modules/{id}/testsets/{tsId}/objectives/{objectiveId}` | Update a step within an objective (step index in body) |
| `DELETE` | `/api/modules/{id}/testsets/{tsId}/objectives/{objectiveId}` | Delete an objective from the test set |
| `PUT` | `/api/modules/{id}/testsets/{tsId}/setup-steps` | Create/update reusable setup steps (e.g. login) for a test set |
| `DELETE` | `/api/modules/{id}/testsets/{tsId}/setup-steps` | Clear setup steps from a test set |
| `POST` | `/api/modules/{id}/testsets/{tsId}/ai-patch` | Preview LLM-applied natural language patch to test cases |
| `POST` | `/api/modules/{id}/testsets/{tsId}/ai-patch/apply` | Apply a previewed AI patch to the test set |
| `GET` | `/api/testsets` | List all test sets (legacy, combined view) |
| `GET` | `/api/testsets/{id}` | Full test set detail (legacy) |
| `GET` | `/api/testsets/{id}/runs` | Execution history (legacy) |
| `GET` | `/api/testsets/{id}/runs/{runId}` | Full execution detail (legacy) |
| `POST` | `/api/modules/{id}/run` | Trigger module-level run (all test sets, parallel, Reuse mode) |
| `GET` | `/api/modules/{id}/run/status` | Poll module-level run progress (per-test-set status) |
| `POST` | `/api/runs` | Trigger a test run (supports `moduleId` + `testSetId` + optional `objectiveId` for single-objective execution) |
| `GET` | `/api/runs/{runId}/status` | Poll run progress |
| `GET` | `/api/runs/active` | Check for any active run (module-level or individual) — used for page-refresh recovery |
| `GET` | `/api/config/api-stacks` | List configured API stacks and modules (for UI dropdowns) |
| `GET` | `/api/config/environments` | List configured customer environments (key, display name, default flag, data-teardown opt-in) |
| `GET` | `/api/config/endpoints` | List Bravo `EndPointCode`s via `IEndpointResolver.ListCodesAsync()`. Returns `{ endpoints, error? }` — the `error` field is populated (and `endpoints` is empty) when the Bravo DB is unreachable, so the chat catalog degrades gracefully instead of 500ing. |
| `POST` | `/api/chat/message` | LLM-backed chat turn. Body: `{ message, conversationId?, context? }`. Returns `{ reply, actions[], conversationId }`. The server loads prior turns from SQLite by `conversationId` (creating a new thread when omitted) and persists both user and assistant turns. Each action is `navigate` / `showData` / `confirmRun` / `confirmCreate` / `confirmRecord` / `confirmCreatePostStep`. |
| `GET` | `/api/chat/conversations` | List the caller's conversations (newest first). Returns `[{ id, title, createdAt, updatedAt, messageCount }]`. SQLite + auth mode only; falls back to `[]` otherwise. |
| `POST` | `/api/chat/conversations` | Create an empty conversation. Body: `{ title? }`. Returns `201` + `ConversationSummary`. Honours the per-user retention cap. |
| `GET` | `/api/chat/conversations/{id}` | Full transcript for one conversation: `{ id, title, …, messages: [{ id, role, content, actions, createdAt }] }`. 404 if the id does not belong to the caller. |
| `DELETE` | `/api/chat/conversations/{id}` | Delete a conversation and its messages. 404 if not owned. |
| `PATCH` | `/api/chat/conversations/{id}` | Rename. Body: `{ title }`. |
| `POST` | `/api/recordings` | Enqueue a recording/auth-setup job for a local agent (SQLite-only). Kinds: `Record`, `RecordSetup`, `RecordVerification`, `AuthSetup`. Returns `202 { jobId, status: Queued, jobKind, targetType }`. |
| `GET` | `/api/health` | Health check |
| `GET` | `/screenshots/{filename}` | Serve Playwright failure screenshots (static files from `PlaywrightScreenshotDir`) |
| `GET` | `/api/auth/status` | Check if auth is enabled (returns `{ enabled, hasUsers }`) |
| `GET` | `/api/users` | List all users |
| `POST` | `/api/users` | Create a user (bootstrap: no auth required when no users exist) |
| `DELETE` | `/api/users/{id}` | Delete a user |
| `POST` | `/api/executions` | Save execution run (Runner remote mode API client) |
| `PUT` | `/api/modules/{id}/testsets/{tsId}/data` | Save full test set data (Runner remote mode) |
| `POST` | `/api/modules/{id}/testsets/{tsId}/merge` | Merge objectives into test set (Runner remote mode) |
| `POST` | `/api/modules/{id}/testsets/{tsId}/run-stats` | Update run stats (Runner remote mode) |
| `GET` | `/api/modules/{id}/testsets/{tsId}/delivery-context/{objectiveId}` | Latest delivery context for verify-only (Runner remote mode) |
| `GET` | `/api/modules/{id}/testsets/{tsId}/objective-statuses` | Latest objective statuses (Runner remote mode) |

---

### React Frontend (`ui/`)

Single-page application built with React 18, TypeScript, and Vite. Communicates with WebApi over REST.

```
ui/src/
  main.tsx                         — React root + QueryClientProvider + AuthProvider + ActiveRunProvider + BrowserRouter
  App.tsx                          — Route definitions with Layout wrapper
  api/
    client.ts                      — fetch wrapper with base URL + error handling + X-Api-Key header injection
    config.ts                      — API functions for config discovery (fetchApiStacks)
    modules.ts                     — API functions for modules and module-scoped test sets/runs (incl. triggerModuleRun, fetchModuleRunStatus)
    testSets.ts                    — API functions for legacy flat test sets and runs
    runs.ts                        — API functions for triggering and polling runs (incl. fetchActiveRun)
  contexts/
    ActiveRunContext.tsx            — Global run state: tracks module-level and individual runs, polls status,
                                     recovers active run on page refresh via GET /api/runs/active
    AuthContext.tsx                 — API key auth state: login, logout, current user name,
                                     checks GET /api/auth/status on mount to determine if auth is enabled
  pages/
    ModuleListPage.tsx             — Module card grid (root page)
    SystemHealthPage.tsx           — System Health page (`/system`) — Agents, Data Packs, Backup, Auth Health summary
    ModuleDetailPage.tsx           — Test sets within a module + search/sort/status-filter toolbar,
                                     progressive card loading (IntersectionObserver), create/run dialogs
    TestSetDetailPage.tsx          — Test cases table + run history + trigger button (module-aware)
    ExecutionDetailPage.tsx        — Objective results with expandable step details (module-aware)
    LoginPage.tsx                  — API key login form (shown when auth is enabled and no key stored)
  components/
    Layout.tsx                     — Header, nav (Modules + System links with 4-source health status dot/triangle — agents, backup, data packs, auth), content area, user name display + logout button
    StatusBadge.tsx                — Re-export shim; source of truth is execution/StatusBadge.tsx
    execution/                     — Execution design system (REQ-001)
      StatusBadge.tsx              — Canonical status pill (Passed/Failed/Running/AwaitingVerification/…); STATUS_COLORS exported
      ModeBadge.tsx                — Run mode pill (Reuse / Rebaseline / VerifyOnly)
      ExecutionModeBadge.tsx       — Inline vs Deferred post-step mode pill
      DeferredCountdownChip.tsx    — Live countdown chip (> 2 min: "~Nm"; ≤ 2 min: "Xm Ys"; overdue: "awaiting claim")
      RunningIndicator.tsx         — Canonical CSS spin ring with size prop (sm=12px / md=16px / lg=20px)
      StatsBar.tsx                 — Pass/fail stat bar; size=sm (pill row) or size=lg (grid of boxes)
      TargetBadge.tsx              — UI target type pill (Desktop/Blazor/MVC/API/DB/…) with colour palette
      index.ts                     — Barrel export for all execution components
    TestSetCard.tsx                — Test set summary card (module-scoped links)
    TestCaseTable.tsx              — API test cases: HTTP method, endpoint, expected status, Last Result badge; inline delete per step
    WebUiTestCaseTable.tsx         — Web UI test cases: name, start URL, step count, screenshot flag, Last Result badge; inline delete per step
    RunHistoryTable.tsx            — Run list with status, duration, date (module-aware links)
    StepList.tsx                   — Expandable objective/step rows with detail
    TriggerRunButton.tsx           — Mode selector + trigger (uses ActiveRunContext for global progress)
    TriggerObjectiveRunButton.tsx  — Single-objective run button (uses ActiveRunContext for global progress)
    ModuleRunBanner.tsx            — Segmented progress bar + per-test-set status during module runs
    CreateModuleDialog.tsx         — Modal form to create a module
    CreateTestSetDialog.tsx        — Modal form to create a test set within a module
    RunObjectiveDialog.tsx         — Modal to select API stack/module, test set, enter objective + optional short name, trigger run
    ConfirmDialog.tsx              — Reusable confirmation modal (used for destructive actions)
    MoveObjectiveDialog.tsx        — Modal to move an objective to another module/test set
    EditTestCaseDialog.tsx         — Modal form to edit a single API test step; "Delete Step" removes
                                    only the individual step (or the whole objective if it's the last step)
    EditWebUiTestCaseDialog.tsx    — Modal form to edit Web UI test case steps (action, selector, value,
                                    timeout per step; add/reorder/delete steps); "Delete Step" removes
                                    only the individual step (or the whole objective if it's the last step)
    SetupStepsPanel.tsx            — Collapsible panel for viewing/editing test-set-level setup steps
                                    (e.g. login); shown on TestSetDetailPage above the test cases list
    AiPatchPanel.tsx               — Panel for natural language AI patching of test cases with preview/apply flow
  types/
    index.ts                       — TypeScript interfaces matching API responses
```

---

## Key Dependencies (NuGet)

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.SemanticKernel` | 1.30.0 | LLM abstraction layer (chat history, chat completion service interface) |
| `Anthropic.SDK` | 4.7.2 | Native Anthropic API client (Claude) |
| `Microsoft.Extensions.Hosting` | 8.0.1 | .NET generic host, DI container, configuration |
| `Microsoft.Extensions.Http` | 8.0.1 | `IHttpClientFactory` for `HttpClient` lifecycle management |
| `Microsoft.Data.Sqlite` | 8.0.* | SQLite storage backend (Storage project) |
| `Spectre.Console` | 0.49.1 | Rich console output (tables, spinners, markup) |

---

## Core Interfaces

### ITestAgent

```csharp
public interface ITestAgent
{
    string Name { get; }
    string Role { get; }
    Task<bool> CanHandleAsync(TestTask task);
    Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default);
}
```

All agents implement this interface. `ExecuteAsync` returns a single `TestResult` per task (one objective), with `ObjectiveId` and `ObjectiveName` identifying which objective it corresponds to. The `Steps` list inside the result contains one `TestStep` per API call. For Web UI agents, each Playwright step within a test case produces its own `TestStep` (e.g. a 9-step test case yields 9 individual step results plus infrastructure steps for loading and browser launch). The orchestrator calls `CanHandleAsync` on each registered agent to route each task. Adding a new agent type requires only implementing this interface and registering it in DI — no changes to the orchestrator.

---

## Parallel Execution & Concurrency

Test execution is parallelized at two levels, controlled by a single `AgentConcurrencyLimiter` (a `SemaphoreSlim` wrapper registered as a DI singleton):

1. **Within a test set** — the orchestrator runs objectives in parallel, each acquiring a slot from the limiter before dispatching to an agent.
2. **Within a module "Run All"** — all test sets launch concurrently; the shared limiter gates how many agents execute at once across all test sets.

`MaxParallelAgents` (default: 4, in `TestEnvironmentConfig`) controls the semaphore capacity. Setting it to 1 restores sequential behavior.

**Thread-safety measures:**
- `TestSetRepository` uses per-file `SemaphoreSlim` locks for read-modify-write operations (merge, update stats).
- `ModuleRunTracker` uses `lock` on each `ModuleRunStatus` instance for mutation safety.
- `ExecutionHistoryRepository` needs no locking — each run writes to a unique file.
- Fail-fast 404 detection uses `Volatile.Read` / `Interlocked.Exchange` (best-effort in parallel mode).

## Execution Flow

```
User CLI args
    │
    ▼
ParseArgs()  ──── --list ──────────────────────────────────► Print test sets, exit
    │
    ├── RunMode.Normal / Rebaseline
    │       │
    │       ▼
    │   TestOrchestrator.RunAsync()
    │       │
    │       ├─ effectiveEnv = IEnvironmentResolver.ResolveKey(envArg)
    │       │       Precedence: CLI → persisted testset.EnvironmentKey → DefaultEnvironment
    │       │
    │       ├─ DecomposeObjectiveAsync()
    │       │       LLM decomposes objective → TestTask (one per objective)
    │       │       Each task.Parameters["EnvironmentKey"] = effectiveEnv
    │       │
    │       ├─ For each objective (TestTask):
    │       │       FindAgentAsync() → routes to ApiTestAgent (if API_REST/GraphQL)
    │       │       agent.ExecuteAsync(task) → returns ONE TestResult with ObjectiveId
    │       │           ├─ TryLoadOpenApiSpecAsync()       [optional]
    │       │           ├─ DiscoverEndpointAsync()         [live GET, captures real fields]
    │       │           ├─ GenerateTestCasesAsync()        [LLM → List<ApiTestDefinition>]
    │       │           └─ For each ApiTestDefinition (step):
    │       │                   StepParameterSubstituter.Apply(step, envParams)   [clones + substitutes {{Tokens}}]
    │       │                   ExecuteTestCaseAsync()
    │       │                       ├─ Build HttpRequestMessage (URL from IApiTargetResolver(stack, module, env))
    │       │                       ├─ InjectAuthAsync() → IApiTargetResolver.GetTokenProvider(stack, env)
    │       │                       ├─ HttpClient.SendAsync()
    │       │                       └─ ValidateResponseAsync()
    │       │                               ├─ Rule checks (status, contains)
    │       │                               └─ LLM validation (JSON, types, security)
    │       │       Metadata["generatedTestCases"] = list of ApiTestDefinition
    │       │
    │       ├─ SaveTestSetAsync()   [persists TestObjectives to modules/{moduleId}/{testSetId}.json]
    │       │                         New objectives stamped AllowedEnvironments=[effectiveEnv]
    │       │                         Test set EnvironmentKey set if not already persisted
    │       └─ GenerateSummaryAsync()  [LLM narrative]
    │
    └── RunMode.Reuse
            │
            ▼
        TestOrchestrator.RunAsync()
            │
            ├─ TestSetRepository.LoadAsync(reuseId)
            │       Deserialises modules/{moduleId}/{id}.json → PersistedTestSet
            │       Restores TestObjectives from saved data
            │
            ├─ effectiveEnv = IEnvironmentResolver.ResolveKey(envArg ?? saved.EnvironmentKey)
            │
            ├─ Environment filter:
            │       For each TestObjective, if AllowedEnvironments is set and doesn't
            │       include effectiveEnv, synthesize a Skipped TestResult and drop it.
            │       Empty AllowedEnvironments = "default-only" (legacy-safe default).
            │
            ├─ (Optional) Single-objective filter:
            │       If objectiveId is provided, filters tasks to only the matching objective.
            │
            ├─ For each surviving TestObjective:
            │       Injects ApiSteps/WebUiSteps into TestTask.Parameters["PreloadedTestCases"]
            │       Injects EnvironmentParameters[effectiveEnv] into task.Parameters["EnvironmentParameters"]
            │       If test set has SetupSteps, also injects SetupSteps + SetupStartUrl
            │       agent.ExecuteAsync(task) → returns ONE TestResult with ObjectiveId
            │           ├─ Detects "PreloadedTestCases" + "EnvironmentParameters" in Parameters
            │           ├─ StepParameterSubstituter.Apply(case, envParams)  [per test case, before execution]
            │           ├─ Skips spec load, discovery, and LLM generation
            │           ├─ (Web UI) Subclass TargetBaseUrl reads via IEnvironmentResolver(envKey)
            │           │       so URLs, creds, and storage-state paths come from the active env block
            │           └─ Executes saved ApiTestDefinition/WebUiTestCase steps directly
            │
            ├─ TestSetRepository.UpdateRunStatsAsync()  [bumps RunCount, LastRunAt]
            ├─ ExecutionHistoryRepository.SaveAsync()    [PersistedExecutionRun.EnvironmentKey = effectiveEnv]
            └─ GenerateSummaryAsync()  [LLM narrative]
```

---

## LLM Integration

### Semantic Kernel Abstraction

The application uses [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel) as an abstraction over the LLM. All LLM calls go through `IChatCompletionService` — a standard Semantic Kernel interface — so the underlying provider can be swapped without changing agent code.

### Anthropic Bridge

Anthropic does not have an official Semantic Kernel connector. `AnthropicChatCompletionService` is a custom implementation of `IChatCompletionService` that:
1. Accepts a `ChatHistory` object (Semantic Kernel's format)
2. Translates it to `Anthropic.SDK` message format
3. Calls the Anthropic Messages API
4. Translates the response back to a Semantic Kernel `ChatMessageContent`

This keeps all agent code provider-agnostic — switching to OpenAI requires only changing `appsettings.json`.

### LLM Prompt Pattern

All LLM calls follow this pattern in `BaseTestAgent`:

1. `AskLlmAsync(prompt, ct)` — returns raw string response
2. `AskLlmForJsonAsync<T>(prompt, ct)` — returns raw string, cleans markdown fences, extracts JSON, deserialises to `T`

JSON cleaning handles the LLM wrapping responses in ` ```json ... ``` ` blocks: the cleaner strips fences, finds the first `{` or `[`, and extracts through the last matching `}` or `]`.

### LLM Calls Per Run

| Step | LLM Used | Model Role |
|---|---|---|
| Objective decomposition | Yes | Senior QA Architect |
| Test case generation (Normal/Rebaseline) | Yes | Senior REST API Test Engineer |
| Test case generation (Reuse) | **No** | — |
| Response validation (per test case) | Yes | Reviewer |
| Suite summary | Yes | Test Results Analyst |

---

## Persistence Layer

Two storage backends are available, selected by the `StorageProvider` config key:

| Backend | Config value | Repositories | Notes |
|---|---|---|---|
| File-based (default) | `"File"` | `ModuleRepository`, `TestSetRepository`, `ExecutionHistoryRepository` | JSON files on disk; original backend |
| SQLite | `"Sqlite"` | `SqliteModuleRepository`, `SqliteTestSetRepository`, `SqliteExecutionHistoryRepository`, `SqliteUserRepository`, `SqliteChatConversationRepository` | Single DB file; WAL mode for concurrent reads; also enables user auth and per-user persisted Assistant conversations |

All three repository interfaces (`IModuleRepository`, `ITestSetRepository`, `IExecutionHistoryRepository`) live in the `AiTestCrew.Agents.Persistence` namespace inside the Storage project. Agent and orchestrator code programs against the interfaces — the active backend is selected at DI registration time.

### Module and Test Set Storage

Tests are organised in a **Module > Test Set > Test Objective > Steps** hierarchy. On disk (file-based backend):

```
{dataDir}/
  modules/
    sdr/
      module.json                          ← PersistedModule
      controlled-load-decodes.json         ← PersistedTestSet (can hold multiple objectives)
      meter-types.json                     ← PersistedTestSet
    default/
      module.json                          ← Auto-created by migration
      test-get-api-products-endpoint.json  ← Migrated from legacy testsets/
  testsets/                                ← Legacy directory (kept as read-only fallback)
  executions/                              ← Unchanged: executions/{testSetId}/{runId}.json
```

**Slug algorithm** (`SlugHelper.ToSlug`):
1. Lowercase the input string
2. Replace all non-alphanumeric characters with hyphens
3. Collapse consecutive hyphens to one
4. Trim leading/trailing hyphens
5. If ≤ 80 characters, return as-is
6. If > 80 characters, truncate to 70 characters at the last hyphen boundary and append `-{hash}` (first 8 hex chars of SHA-256 of the original input). This prevents collisions between different long strings that share a common prefix.

Examples:
- `"Standing Data Replication (SDR)"` → `"standing-data-replication-sdr"` (short, no hash)
- `"Please test the following API with the given parameters and validate it return record and NMI property set to 6305824218..."` → `"please-test-the-following-api-with-the-given-parameters-and-validate-it-a1b2c3d4"` (truncated + hash)

### Migration

On first startup, `MigrationHelper` runs two migrations automatically:

**Directory migration** (legacy `testsets/` → `modules/default/`):
1. If `modules/default/module.json` already exists, skips (idempotent)
2. Creates a "Default" module
3. Copies each `testsets/*.json` into `modules/default/`, populating `ModuleId`
4. Leaves the original `testsets/` directory intact

**Schema migration** (v1 → v2):
1. Detects test set files with `"tasks"` array (v1 schema, `PersistedTaskEntry`)
2. Converts each `PersistedTaskEntry` into a `TestObjective`: `testCases` → `apiSteps`, `webUiTestCases` → `webUiSteps`
3. Replaces `"tasks"` with `"testObjectives"` and sets `"schemaVersion": 2`
4. Writes the updated file back to disk

### PersistedTestSet Schema (v2)

```json
{
  "schemaVersion": 2,
  "id": "controlled-load-decodes",
  "name": "Controlled Load Decodes",
  "moduleId": "sdr",
  "apiStackKey": "bravecloud",
  "apiModule": "sdr",
  "environmentKey": "sumo-retail",
  "createdAt": "2026-04-04T14:30:00Z",
  "lastRunAt": "2026-04-04T15:45:00Z",
  "runCount": 3,
  "testObjectives": [
    {
      "objectiveId": "a1b2c3d4",
      "objectiveText": "Test GET /api/ControlledLoadDecodes endpoint",
      "objectiveName": "Ctrl Load GET",
      "targetType": "API_REST",
      "allowedEnvironments": ["sumo-retail"],
      "environmentParameters": {
        "sumo-retail":  { "NMI": "4103035611" },
        "ams-metering": { "NMI": "9999999999" }
      },
      "apiSteps": [
        {
          "name": "Get meter - happy path",
          "method": "GET",
          "endpoint": "/api/Meters/{{NMI}}",
          "headers": {},
          "queryParams": {},
          "body": null,
          "expectedStatus": 200,
          "expectedBodyContains": ["id", "name"],
          "expectedBodyNotContains": [],
          "isFuzzTest": false
        }
      ],
      "webUiSteps": []
    },
    {
      "objectiveId": "e5f6g7h8",
      "objectiveText": "Test POST /api/ControlledLoadDecodes endpoint",
      "objectiveName": "Ctrl Load POST",
      "targetType": "API_REST",
      "allowedEnvironments": ["sumo-retail"],
      "environmentParameters": {},
      "apiSteps": [ ... ],
      "webUiSteps": []
    }
  ]
}
```

**Per-testset fields:**
- `apiStackKey` / `apiModule` — persisted multi-stack target (see [Multi-Stack API Configuration](functional.md#multi-stack-api-configuration)).
- `endpointCode` — Bravo delivery endpoint for aseXML delivery test sets.
- `environmentKey` — default customer environment when `--environment` is omitted. Resolved at run time via `IEnvironmentResolver`; falls back to `TestEnvironmentConfig.DefaultEnvironment` when the test set has no persisted value.

**Per-objective multi-environment fields:**
- `allowedEnvironments` — list of env keys this objective runs on. Empty = "default environment only" (legacy semantics — objectives created before the feature keep running only against the default). The orchestrator skips excluded objectives as `Skipped` (not `Failed`). New/rebaselined objectives are auto-stamped with the active env.
- `environmentParameters` — per-environment `{{Token}} → value` maps. At playback, `StepParameterSubstituter.Apply(step, params)` clones every step definition with tokens resolved for the active env. Unknown tokens stay literal and log a WARN (lenient mode, shared with post-delivery verification playback via `TokenSubstituter`).

> **v1 schema (deprecated):** Older test set files use `"tasks"` with `PersistedTaskEntry` objects. On first load, `MigrationHelper` automatically migrates v1 files to v2, converting each task entry into a `TestObjective` and renaming `testCases` → `apiSteps` / `webUiTestCases` → `webUiSteps`.

### Execution History Storage

Every test run (Normal, Reuse, Rebaseline) is persisted as a JSON file in `executions/{testSetId}/{runId}.json`.

**`PersistedExecutionRun` schema:**
```json
{
  "runId": "a1b2c3d4e5f6",
  "testSetId": "test-get-api-products-endpoint",
  "objective": "Test GET /api/products endpoint",
  "mode": "Reuse",
  "status": "Passed",
  "startedAt": "2026-04-04T14:30:00Z",
  "completedAt": "2026-04-04T14:31:42Z",
  "totalDuration": "00:01:42",
  "summary": "All 7 test steps passed...",
  "totalObjectives": 1,
  "passedObjectives": 1,
  "failedObjectives": 0,
  "errorObjectives": 0,
  "objectiveResults": [
    {
      "objectiveId": "a1b2c3d4",
      "objectiveName": "Ctrl Load GET",
      "agentName": "API Agent",
      "status": "Passed",
      "summary": "...",
      "passedSteps": 7,
      "failedSteps": 0,
      "totalSteps": 7,
      "steps": [
        {
          "action": "GET /api/products",
          "summary": "Happy path - 200 OK",
          "status": "Passed",
          "detail": "Response body: {...}",
          "duration": "00:00:00.342",
          "timestamp": "2026-04-04T14:30:05Z"
        }
      ]
    }
  ]
}
```

The orchestrator saves execution history after every run (wrapped in try/catch so failures are non-fatal). `ExecutionHistoryRepository` provides `SaveAsync`, `GetRunAsync`, `ListRuns`, `GetLatestRun`, `GetLatestObjectiveStatuses`, `CountRuns`, `DeleteRunAsync`, and `DeleteRunsForTestSetAsync` (cascade-deletes all runs for a test set).

**Automatic retention** — `SaveAsync` calls `PruneOldRunsAsync` after writing the new run file. If `MaxExecutionRunsPerTestSet` (from `TestEnvironmentConfig`, default 10) is positive, runs beyond the limit are deleted oldest-first. A value of `0` disables pruning. API endpoints and the CLI use `CountRuns` (lightweight file count, no deserialization) instead of the persisted `RunCount` field to reflect the actual number of runs on disk.

**Per-objective status aggregation** — `GetLatestObjectiveStatuses(testSetId)` scans all runs for a test set in descending date order and picks the most recent `PersistedObjectiveResult` for each `ObjectiveId`. This allows individual test cases to be executed independently while the test set detail endpoint returns each objective's latest status regardless of which run produced it. The test set's `LastRunStatus` is computed as the worst-case aggregate: Error > Failed > Skipped > Passed.

The `POST /api/runs` endpoint accepts an optional `objectiveId` parameter. When provided (Reuse mode only), the orchestrator filters to only that objective before execution. The resulting execution run contains a single `PersistedObjectiveResult`. `GetLatestObjectiveStatuses` merges results across all runs, so a single-objective run updates only that objective's status without affecting others.

---

### How Reuse Works

In reuse mode, saved `ApiTestDefinition` steps (from `TestObjective.ApiSteps`) are placed into `TestTask.Parameters["PreloadedTestCases"]` before the task reaches the agent. `ApiTestAgent.ExecuteAsync` checks for this key at the very start and, if found, skips the discovery call and all LLM generation, proceeding directly to HTTP execution of the saved steps.

**Important:** `ApiTestDefinition.Body` is typed as `object?`. When deserialised from JSON by `System.Text.Json`, it becomes a `JsonElement`. This is handled correctly in `ExecuteTestCaseAsync` which serialises the body back to a string regardless of whether it is a `string` or `JsonElement`.

---

## Agent Pattern

### How Agents Are Registered and Routed

In `Program.cs`, agents are registered as both their concrete type and as `ITestAgent`:

```csharp
builder.Services.AddSingleton<ApiTestAgent>(...);
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<ApiTestAgent>());
```

The orchestrator receives `IEnumerable<ITestAgent>` via constructor injection (standard .NET DI behaviour for collections). For each task, it iterates the list and calls `CanHandleAsync(task)` on each agent, routing to the first match.

### Adding a New Agent

1. Implement `ITestAgent` in a new class under `AiTestCrew.Agents/`
2. Override `CanHandleAsync` to return `true` for the relevant `TestTargetType` values
3. Register in `Program.cs` alongside `ApiTestAgent`

No changes required to the orchestrator, models, or any existing agents.

### BaseTestAgent

Provides shared infrastructure for all agents:

- `AskLlmAsync(prompt, ct)` — sends a prompt, returns raw LLM response
- `AskLlmForJsonAsync<T>(prompt, ct)` — sends a prompt, cleans and deserialises JSON response (delegates to `LlmJsonHelper`)
- `SummariseResultsAsync(steps, ct)` — generates a one-paragraph summary of a list of test steps
- `CleanJsonResponse(raw)` — strips markdown fences, extracts JSON from LLM output (delegates to `LlmJsonHelper`)

`LlmJsonHelper` (static) provides the underlying JSON utilities (`CleanJsonResponse`, `DeserializeLlmResponse<T>`) so WebApi endpoints can also call the LLM directly without going through an agent.

---

## Discovery-Driven Test Generation

Before the LLM generates test cases, `ApiTestAgent.DiscoverEndpointAsync` makes a live GET request to the primary endpoint extracted from the task description. The regex captures the full path and query string (e.g. `NMIDiscoveryManagement/NMIDetails?ParticipantCode=SPARQ&NMI=123`), with or without a leading `/`. The response is used to:

1. Provide a real response body sample (first 1,500 characters) to the LLM
2. Extract top-level JSON field names
3. Supplement user-requested validations with real field names from the response

**Objective-driven assertions take priority over discovery.** When the objective mentions specific values to validate (e.g. *"validate NMI property set to 6305824657"*), those values are always included in `expectedBodyContains`, regardless of whether the discovery call succeeded. Discovery provides additional grounding to prevent hallucinated field names, but is not a prerequisite for assertions.

---

## Logging

### File Logger (`FileLoggerProvider`)

- Creates `logs/testrun_{yyyyMMdd_HHmmss}.log` on each run
- Captures all log levels (Trace through Critical)
- Thread-safe (lock-based writes)
- Format: `[HH:mm:ss.fff] [LEVEL] [Category,-30] Message`
- Includes full HTTP request/response details for every test case (at Debug level)

### Console Logger

- Filtered to `AiTestCrew.*` namespace only (suppresses `System.*` and `Microsoft.*` noise)
- Minimum level: `Warning` by default, `Information` when `VerboseLogging = true`
- Ensures the Spectre.Console spinner is not corrupted by framework log output

---

## Dependency Injection Wiring

All services are registered in `Program.cs` before the host is built:

```
TestEnvironmentConfig        → Singleton (bound from appsettings.json)
Kernel                       → Singleton (Semantic Kernel, with IChatCompletionService)
IChatCompletionService       → Singleton (AnthropicChatCompletionService or OpenAI)
IHttpClientFactory           → Managed by AddHttpClient()
IApiTargetResolver           → Singleton (ApiTargetResolver — resolves stack+module URLs and per-stack token providers)
ApiTestAgent                 → Singleton (concrete + ITestAgent)
IModuleRepository            → Singleton (File-based or Sqlite, selected by StorageProvider config)
ITestSetRepository           → Singleton (File-based or Sqlite, selected by StorageProvider config)
IExecutionHistoryRepository  → Singleton (File-based or Sqlite, selected by StorageProvider config)
IUserRepository              → Singleton (SqliteUserRepository — only registered in Sqlite mode)
TestOrchestrator             → Singleton (receives IEnumerable<ITestAgent> + repository interfaces)
IRunTracker                  → Singleton (WebApi only — tracks individual async run state)
IModuleRunTracker            → Singleton (WebApi only — tracks module-level composite runs)
```

**Storage provider selection** — `Program.cs` reads `StorageProvider` from config. `"Sqlite"` registers `Sqlite*Repository` implementations and runs `DatabaseMigrator.MigrateAsync` at startup. `"File"` (default) registers the file-based implementations. Runner in remote mode (`ServerUrl` configured) registers `ApiClient*Repository` implementations instead, bypassing local storage entirely.

---

## Data Flow: Normal Run

```
CLI arg: "Test the /api/products endpoint"
    │
    ▼
CliArgs { Mode = Normal, Objective = "Test the /api/products endpoint" }
    │
    ▼
TestOrchestrator.RunAsync(objective, Normal)
    │
    ├─ LLM → TestTask (one per objective)
    │         { Id="a1b2c3d4", Target=API_REST, Description="Test GET /api/products..." }
    │
    ├─ ApiTestAgent.ExecuteAsync(task) → ONE TestResult
    │       │
    │       ├─ GET /api/products → EndpointDiscovery { StatusCode=200, Fields="id,name,price" }
    │       │
    │       ├─ LLM → List<ApiTestDefinition> (7 steps)
    │       │
    │       └─ For each ApiTestDefinition → TestStep { Status=Passed/Failed, Summary="...", Detail="..." }
    │
    ├─ TestResult { ObjectiveId, ObjectiveName, Status=Passed, PassedSteps=6, Steps=[...] }
    │
    ├─ modules/default/test-the-api-products-endpoint.json  ← persisted as TestObjective
    │
    ├─ LLM → suite summary string
    │
    └─ TestSuiteResult { Objective, Results, Summary, TotalDuration, TotalObjectives }
                │
                ▼
           Console table + overall line + LLM narrative
```

---

## Security Considerations

- **API keys and tokens** in `appsettings.json` must be protected. The file is copied to the binary output directory on build. It should not be committed to source control — use `appsettings.example.json` as the template.
- **Auth credentials are never passed to the LLM.** They are injected directly into `HttpRequestMessage` by `InjectAuthAsync()` via `IApiTargetResolver.GetTokenProvider()` after the LLM-generated headers are applied. Each API stack gets its own `LoginTokenProvider` (pointing at that stack's security module login endpoint) with independent token caching. Auth credentials (`AuthUsername`, `AuthPassword`) are shared across all stacks — only the login endpoint differs.
- **LLM validation is advisory for security headers.** Missing `X-Content-Type-Options`, `X-Frame-Options`, and `Strict-Transport-Security` are noted in the validation reason text but do not cause test failures.
- **Response bodies are truncated** to 2,000 characters before being sent to the LLM for validation, and to 500 characters in the console detail view, to limit token usage and avoid leaking large payloads into logs inadvertently.
- **Bravo endpoint passwords** (resolved from `mil.V2_MIL_EndPoint.Password`) are never logged or surfaced in step details. `AseXmlDeliveryAgent` passes the `BravoEndpoint` record between layers but every log statement touches only `EndPointCode`, `UserName`, host, `OutBoxUrl`, and the zip flag.
- **Playwright storage state** files (`bravecloud-auth-state.json`, the legacy MVC equivalent) are gitignored. Re-generate via `--auth-setup --target <UI_*>` on any machine that needs to record or replay UI tests.

---

## Multi-User Architecture

### Storage backends

The persistence layer supports two backends (see Persistence Layer section above). SQLite is required for multi-user features (auth, audit trail).

**SQLite schema** — Nine tables (schema v10):

| Table | Purpose |
|---|---|
| `modules` | Module metadata + full JSON in `data TEXT` column; `version`, `created_by`, `updated_by` for optimistic concurrency |
| `test_sets` | Test set metadata (module_id FK) + full JSON in `data TEXT`; `version`, `created_by`, `updated_by`, `updated_at` |
| `execution_runs` | Run metadata (test_set_id, status, timestamps) + full JSON in `data TEXT` |
| `users` | Id, Name, ApiKey (unique, `atc_` prefixed), Role, timestamps |
| `active_runs` | In-progress individual runs (for crash recovery) |
| `active_module_runs` | In-progress module-level runs (for crash recovery) |
| `recording_locks` | Per-objective serialisation lock for active recording jobs (Queued/Claimed/Running) |
| `run_queue` | Distributed job queue for agent-mode execution |
| `schema_version` | Single-row version tracker for migrations |

Design: structured columns are used for queries and filtering; the `data TEXT` column holds the full serialized model as JSON for lossless round-tripping. WAL mode is enabled for concurrent read access.

**Migration CLI** — `--migrate-to-sqlite` reads all file-based modules, test sets, and execution runs and inserts them into the SQLite database. One-shot operation.

### User authentication

- **User model** — `Core/Models/User.cs`: Id (GUID), Name, ApiKey (`atc_` prefix + random token), Role, CreatedAt, LastLoginAt.
- **IUserRepository** — `Core/Interfaces/IUserRepository.cs`: CRUD + `GetByApiKeyAsync(key)` for auth lookup.
- **SqliteUserRepository** — generates `atc_`-prefixed API keys on user creation. Only registered when `StorageProvider = "Sqlite"`.
- **ApiKeyAuthMiddleware** — validates `X-Api-Key` header on every request. Allowlisted paths: `GET /api/auth/status`, `POST /api/users` (bootstrap only when zero users exist), static files, health check. When `IUserRepository` is not registered (file-based mode), auth is disabled — all requests pass through.
- **Bootstrap** — First user can be created without auth via `POST /api/users` when the user table is empty.
- **Frontend** — `AuthContext.tsx` checks `GET /api/auth/status` on mount. If auth is enabled and no API key is stored in `localStorage`, the user is redirected to `LoginPage.tsx`. The `client.ts` fetch wrapper injects `X-Api-Key` from `AuthContext` on every API call. `Layout.tsx` shows the current user name and a logout button.

### Concurrent runs

The original architecture used a global single-run lock — only one test set could execute at a time across the entire server. This was replaced with per-target locking:

- `IRunTracker.HasActiveRunForTestSet(testSetId)` — prevents duplicate runs on the same test set.
- `IModuleRunTracker.HasActiveModuleRunForModule(moduleId)` — prevents duplicate module-level runs.
- Independent test sets and modules can run simultaneously.

### Audit trail

- `PersistedModule` gained `CreatedBy`, `LastModifiedBy`, and `Version` fields — populated from the authenticated user.
- `PersistedTestSet` gained `CreatedBy`, `LastModifiedBy`, `UpdatedAt`, and `Version` fields.
- `PersistedExecutionRun` gained `StartedBy` (user ID) and `StartedByName` (display name) fields.
- File-based backend: fields are present but always null (no auth in file mode).
- The `LastModifiedBy` field on modules and test sets is mapped to the `updated_by` SQLite column and surfaced as `updatedBy` in API responses.
- Pre-migration rows (upgraded from schema v9) have `created_by` / `updated_by` NULL — treated as "system" in the UI.

### Multi-user collaboration: optimistic concurrency + recording locks

When multiple QA engineers share one WebApi, two safety mechanisms prevent silent data loss.

#### Optimistic concurrency on writes

Every `modules` and `test_sets` row carries a monotonic `version INTEGER` column (default 1, bumped on every successful write). Callers that want conflict detection send an `If-Match: <version>` request header with their `PUT` request.

Repository behaviour: the UPDATE includes `WHERE version = $expected`. If 0 rows are updated (version mismatch), `SqliteTestSetRepository` reads the current row and throws `ConcurrencyException` carrying `currentVersion`, `yourVersion`, `currentUpdatedBy`, and `currentUpdatedAt`. The endpoint converts this to HTTP 409 with a JSON body containing those four fields plus a human-readable `error` string.

**Legacy compatibility** — when the `If-Match` header is absent, the repository skips version checking (`null` for `expectedVersion`). All existing callers continue to work without modification.

Key types:
- `Core/Exceptions/ConcurrencyException.cs` — typed exception carrying both versions and last-writer info
- `ITestSetRepository.SaveAsync(testSet, moduleId, int? expectedVersion, string? userId)` — versioned overload
- `SqliteTestSetRepository` — conditional UPDATE path; `SqliteModuleRepository` — same pattern

#### Recording locks

While a recording job is `Queued`, `Claimed`, or `Running`, a row exists in `recording_locks` keyed by `(module_id, test_set_id, objective_id)`. `objective_id` is NULL for whole-test-set recordings (RecordSetup, full re-record). A unique index on `(module_id, test_set_id, COALESCE(objective_id, ''))` enforces one active recording per objective.

Lock lifecycle:
1. **Acquire** — `POST /api/recordings` calls `IRecordingLockRepository.AcquireAsync` before enqueuing. Unique-constraint violation → `409 Conflict { error: "Recording in progress on this objective by <user>" }`.
2. **Release** — `JobExecutor` calls `TryReleaseLockAsync(job.JobId)` after the recording finishes (Completed or Failed). `POST /api/runs/{id}/cancel` also releases the lock.
3. **Janitor sweep** — `AgentHeartbeatMonitor` calls `SqliteRecordingLockRepository.SweepStaleLocksAsync` on every 30 s tick. It deletes locks whose `job_id` is no longer present in `run_queue` with a live status — covers crash-without-deregister.

The recording lock is separate from the execution run lock (`IRunTracker.HasActiveRunForTestSet`) — both may coexist.

Key types:
- `Core/Interfaces/IRecordingLockRepository.cs` — `AcquireAsync`, `ReleaseAsync`, `GetLockAsync`, `SweepStaleLocksAsync`
- `Storage/Sqlite/SqliteRecordingLockRepository.cs` — SQLite implementation; catches `SqliteException(code=19)` for graceful 409 messages
- `WebApi/Services/AgentHeartbeatMonitor.cs` — sweep on every 30 s tick
- `Runner/AgentMode/JobExecutor.cs` — lock release on terminal recording state

#### UI awareness

- Test set cards on the dashboard show "Edited X min ago by Y" when `updatedAt` is present.
- `GET /api/modules/{id}/test-sets` and the detail endpoint surface `version`, `updatedBy`, `updatedAt`.
- The frontend caches `version` from load and sends `If-Match` on save; a 409 response triggers a "reload from server" prompt.

### Config externalization

New config fields on `TestEnvironmentConfig`:

| Field | Default | Purpose |
|---|---|---|
| `StorageProvider` | `"File"` | `"File"` or `"Sqlite"` — selects persistence backend |
| `SqliteConnectionString` | (none) | SQLite connection string (required when `StorageProvider = "Sqlite"`) |
| `ListenUrl` | (empty = `http://localhost:5050`) | WebApi bind URL |
| `CorsOrigins` | (empty = localhost dev defaults) | Allowed CORS origins (string array) |
| `ServerUrl` | (empty) | Runner remote mode: WebApi base URL |
| `ApiKey` | (empty) | Runner remote mode: API key for `X-Api-Key` header |

All fields support environment variable overrides via `AITESTCREW_TestEnvironment__<PropertyName>` (standard .NET double-underscore convention).

---

## Multi-Environment Architecture (customer-based)

A parallel axis to the existing `ApiStacks` / modules axes. Lets one test set run against multiple customer deployments (`sumo-retail`, `ams-metering`, `tasn-networks`, ...) with per-customer URLs, credentials, DB connection strings, and data values — without duplicating the tests.

### Three orthogonal axes

| Axis | Configured under | CLI flag | Selects |
|---|---|---|---|
| **Environment** | `TestEnvironment.Environments.<key>` | `--environment` | Customer deployment — UI URLs, creds, Bravo DB, WinForms app path, per-stack BaseUrls |
| **ApiStack** | `TestEnvironment.ApiStacks.<key>` | `--stack` | API platform (BraveCloud vs Legacy) — SecurityModule + LoginPath |
| **ApiModule** | `TestEnvironment.ApiStacks.<stack>.Modules.<key>` | `--api-module` | API service — PathPrefix |

The three axes compose: `--environment ams-metering --stack legacy --api-module sdr` hits AMS's `api-amsdev.braveenergy.com.au` legacy stack's SDR module. Effective URL = `Environments.ams.ApiStackBaseUrls.legacy` (if set, else `ApiStacks.legacy.BaseUrl`) + `/` + `ApiStacks.legacy.Modules.sdr.PathPrefix`.

### Key types

| Type | Purpose |
|---|---|
| `TestEnvironmentConfig.Environments: Dictionary<string, EnvironmentConfig>` | Map of env key → overrides. Empty dictionary = legacy single-env mode; all settings read from top-level fields. |
| `TestEnvironmentConfig.DefaultEnvironment: string?` | Env used when neither CLI nor test set specifies one. |
| `EnvironmentConfig` | Per-customer block. Every field is nullable/optional; the resolver falls back to the equivalent top-level field whenever an env field is null/empty. |
| `IEnvironmentResolver` | Singleton service used by every agent + auth-setup + recording. Methods: `ResolveKey(requested)`, `Resolve(key)`, `ListKeys()`, `ResolveDisplayName(key)`, `ResolveLegacyWebUiUrl(key)`, `ResolveBraveCloudUiUrl(key)`, `ResolveWinFormsAppPath(key)`, `ResolveBravoDbConnectionString(key)`, `ResolveDataTeardownEnabled(key)`, `ResolveApiStackBaseUrl(key, stackKey)`, etc. |
| `EnvironmentResolver` | Default implementation (in `AiTestCrew.Agents.Environment`). Holds the config, does the fallback resolution. |
| `StepParameterSubstituter` | Clones each step-definition / test-case type and replaces `{{Tokens}}` using `TokenSubstituter.Substitute` (lenient). Handles `ApiTestDefinition`, `ApiTestCase`, `WebUiTestDefinition`, `WebUiTestCase`, `DesktopUiTestDefinition`, `DesktopUiTestCase`, `AseXmlTestDefinition`, `AseXmlTestCase`, `AseXmlDeliveryTestDefinition`, `AseXmlDeliveryTestCase`, `VerificationStep`. Substitutes string fields, dict keys/values, list items, and JSON bodies (via round-trip through `JsonSerializer`). |

### Persistence

- `PersistedTestSet.EnvironmentKey: string?` — persisted default env for the test set. Precedence at run time: `--environment` CLI arg → this field → `DefaultEnvironment`.
- `TestObjective.AllowedEnvironments: List<string>` — env keys this objective may run on. Empty = "default env only" (legacy semantics for pre-feature objectives).
- `TestObjective.EnvironmentParameters: Dictionary<string, Dictionary<string, string>>` — outer key = env, inner key = `{{Token}}` name, inner value = substituted literal.
- `PersistedExecutionRun.EnvironmentKey: string?` — which env each historical run executed against, for audit / filtering.

### Runtime wiring

1. **Resolve effective env** (top of `TestOrchestrator.RunAsync`): CLI → `saved.EnvironmentKey` → `DefaultEnvironment`.
2. **Skip disallowed objectives** in Reuse/VerifyOnly: if `AllowedEnvironments` is non-empty and doesn't contain the effective env, emit a `Skipped` TestResult and drop the task.
    - **Exception — explicit single-objective override.** When the caller names a specific objective by Id or Name (UI `Run`/`Verify` on a row, or CLI `--objective <idOrName>`), that objective is kept in `objectivesToRun` even if the env filter would have excluded it, and a WARN is logged. Bulk "run all" behaviour is unchanged. Rationale: if the user explicitly picks the objective, env policy shouldn't silently suppress it — and without this override the single-objective matcher later fails with a misleading "not found … Available: \<same slug\>" error, because the matcher runs on the (env-filtered) `tasks` list while the error lists `saved.TestObjectives`.
3. **Auto-stamp new objectives** in Normal/Rebaseline: `BuildObjectiveFromResults(..., environmentKey)` sets `AllowedEnvironments = [effectiveEnv]` when saving.
4. **Inject into TestTask**: every surviving task gets `Parameters["EnvironmentKey"] = effectiveEnv` + `Parameters["EnvironmentParameters"] = envParams` (the per-env dict if the objective defines one).
5. **Agents read env from parameters**:
    - `ApiTestAgent` → `IApiTargetResolver.ResolveApiBaseUrl(stack, module, env)` for URL; `GetTokenProvider(stack, env)` for per-(env,stack) cached token.
    - `BaseWebUiTestAgent` — subclass `TargetBaseUrl` getter reads `_envResolver.ResolveBraveCloudUiUrl(CurrentEnvironmentKey)` / `.ResolveLegacyWebUiUrl(...)`. Same for credentials + storage-state paths.
    - `BaseDesktopUiTestAgent` — `TargetAppPath` and `TargetAppArgs` read from resolver.
    - `BravoEndpointResolver` — env key threads through `ResolveAsync(code, env)` and `ListCodesAsync(env)`; connection strings cached per env.
6. **Substitute `{{Tokens}}`**: each agent calls `StepParameterSubstituter.Apply(caseOrDef, envParams)` before executing the step. Cloned objects only — persisted definitions are never mutated.
7. **Record on history**: `PersistedExecutionRun.FromSuiteResult(..., environmentKey)` captures the active env on the run record.

### `ApiTargetResolver` changes

- Added `ResolveApiBaseUrl(stack, module, env)` overload. The env's `ApiStackBaseUrls.<stack>` overrides `ApiStacks.<stack>.BaseUrl`. The module's `PathPrefix` is appended as before.
- `GetTokenProvider(stack, env)` caches per `$"{envKey}|{stackKey}"` composite key so two environments sharing the same stack name authenticate independently against their own login URLs.
- `BuildLoginUrl` now takes the effective base URL (env-overridden) rather than reading it off the `ApiStackConfig`.

### `--auth-setup` / `--record` / `--record-setup` / `--record-verification`

All four CLI paths construct a local `EnvironmentResolver` from config, resolve `envKey = resolver.ResolveKey(cli.EnvironmentKey)`, and read URLs / storage-state paths / WinForms app path through resolver methods. Each command prints the resolved env + storage-state path up front so the operator can confirm which customer they're authenticating / recording against before the browser window opens.

### Backwards compatibility

- Configs with no `Environments` section still work. The resolver synthesises a virtual `"default"` env that reads exclusively from top-level flat fields — same behaviour as before the feature.
- Objectives with empty `AllowedEnvironments` are treated as "runs on default env only". No migration step — the orchestrator applies this interpretation on the fly. New/rebaselined objectives get stamped on save so the list grows naturally.
- Existing `--stack` / `--api-module` / `--endpoint` flags are unchanged; `--environment` is additive.

### WebApi surface

- `GET /api/config/environments` → `{ environments: [{ key, displayName, isDefault }], defaultEnvironment }`.
- `RunRequest.EnvironmentKey` + `MergeObjectivesRequest.EnvironmentKey` flow into the orchestrator and test-set merge respectively.
- Test-set list + detail responses include `apiStackKey`, `apiModule`, `endpointCode`, `environmentKey` so the UI can show + edit them.

### React UI surface

- `fetchEnvironments()` (`ui/src/api/config.ts`) calls `/api/config/environments`.
- `RunObjectiveDialog` — environment dropdown above the API stack/module dropdowns.
- `TriggerRunButton` + `TriggerObjectiveRunButton` accept an `environmentKey` prop (sourced from the test-set detail) and include it in `triggerRun` payloads.
- `EnvironmentParametersEditor` — attached to the selected objective's detail panel. Two sections: `AllowedEnvironments` checkbox list and a per-env `{{Token}} → value` grid with add/rename/delete rows. Saves via the existing objective PUT endpoint.

---

## Distributed Execution (Phase 4)

The server cannot execute Web UI (Playwright) or Desktop UI (FlaUI) tests because Windows Server Core containers lack Media Foundation and non-interactive sessions have no desktop. Phase 4 introduces a **local-agent model**: QA engineers run the Runner CLI in a long-running `--agent` mode that polls the server for queued jobs and executes them on their own machine.

### Flow

1. Dashboard (or chat assistant) triggers a run. WebApi looks at the test set's target types.
2. If the run needs a browser/desktop (`UI_Web_*`, `UI_Desktop_*`), the WebApi inserts a row into `run_queue` instead of executing in-process.
3. A local Runner with matching capabilities claims the job atomically via `UPDATE ... WHERE status='Queued'` inside a transaction.
4. The agent executes the job via the existing `TestOrchestrator.RunAsync` (for `JobKind = Run`) or `IRecordingService.Record*Async` (for recording kinds) — same code path as the CLI — and posts `/api/queue/{jobId}/progress` when it starts and `/api/queue/{jobId}/result` when done.
5. Results are written to `execution_runs` via the existing Runner API-client flow, so the dashboard shows them like any other run. Recording jobs save the captured steps directly to the shared module/test-set repository (SQLite or shared file dir) and report a success/failure outcome to the queue.

### New SQLite tables

```sql
agents        -- Registered Runner instances, updated on heartbeat (30s)
run_queue     -- Jobs pending/claimed/running/completed (target_type + capabilities match drives claim)
              -- Includes a job_kind column: "Run" | "Record" | "RecordSetup" | "RecordVerification" | "AuthSetup"
              -- Schema version 4 added job_kind via an idempotent ALTER (checks PRAGMA table_info before ADD COLUMN)
```

### New endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/agents/register` | Register or re-register a Runner as an agent. Clears any pending `force_quit_requested` flag — a fresh process must not re-fire a stale signal. |
| `POST` | `/api/agents/{id}/heartbeat` | Keep-alive; returns `{ status, activeJobId, activeJobStatus, shouldExit }`. When `shouldExit = true` the agent self-terminates via `Environment.Exit(1)`. |
| `POST` | `/api/agents/{id}/force-quit` | Dashboard-initiated kill. Sets `force_quit_requested = 1`, pins status to `Offline`, and marks any in-flight queue entry Failed so the queue doesn't wedge. |
| `DELETE` | `/api/agents/{id}` | Graceful deregister (Ctrl+C on Runner) |
| `GET` | `/api/agents` | Dashboard list — name, status, capabilities, owner, current job |
| `GET` | `/api/queue/next?agentId=&capabilities=` | Atomic claim-oldest-matching, returns `204 No Content` if none |
| `POST` | `/api/queue/{jobId}/progress` | Flip Claimed → Running |
| `POST` | `/api/queue/{jobId}/result` | Terminal state (Completed / Failed) |
| `GET` | `/api/queue` | Dashboard queue list |
| `DELETE` | `/api/queue/{jobId}` | Cancel a Queued job (fails if already claimed) |
| `POST` | `/api/screenshots` | Multipart upload — agents push failure screenshots to the server's `PlaywrightScreenshotDir` so the dashboard's `/screenshots/{file}` static handler can serve them |
| `POST` | `/api/recordings` | Enqueue a recording/auth-setup job (`JobKind = Record / RecordSetup / RecordVerification / AuthSetup`). Body discriminates on `kind`; optional `agentId` is pre-validated against `capabilities`. Returns `202 { jobId, status: Queued, jobKind, targetType }`. |

### Dispatch decision (`RunDispatchHelper`)

`RunDispatchHelper.GetAgentRequiredTarget(testSet, objectiveId)` walks the target types of the objectives that will actually run; if any are `UI_Web_MVC`, `UI_Web_Blazor`, or `UI_Desktop_WinForms`, the run is enqueued. Pure API/aseXML sets continue to execute in the server process.

### Heartbeat monitor

`AgentHeartbeatMonitor` is a `BackgroundService` that runs every 30s and marks agents Offline when their `last_seen_at` is older than `AgentHeartbeatTimeoutSeconds` (default 120). No cleanup is done for completed queue entries — they stay for audit/debug.

### Parallel heartbeat loop + force-quit

Inside the Runner, `AgentRunner.RunAsync` starts two independent loops:

- The **polling loop** (main `async` method) claims queued jobs and runs them via `JobExecutor.ExecuteAsync`. While a job is executing, this loop is blocked — recording sessions in particular block on `Playwright`/`FlaUI` waits that don't observe the cancellation token.
- The **heartbeat loop** (`Task.Run`) POSTs to `/api/agents/{id}/heartbeat` every `AgentHeartbeatIntervalSeconds` on its own task, reading the current status from a shared `volatile string _currentStatus`. Because it's not interleaved with job execution, heartbeats keep flowing even when a recording is stuck.

The heartbeat response shape is `{ status, activeJobId, activeJobStatus, shouldExit }`. When `shouldExit` is true, the agent logs the event and calls `Environment.Exit(1)` — deliberately abrupt so the OS reaps any child Playwright browser or FlaUI window.

This is what the dashboard's **Force quit** button on the Agents panel relies on. The `/api/agents/{id}/force-quit` endpoint sets `force_quit_requested = 1` and pins the agent to `Offline` in the same SQL update; the heartbeat handler then short-circuits (skips `HeartbeatAsync`, returns `shouldExit = true`) so the dying agent's final heartbeats can't bump status back up to Online / Busy. The `force_quit_requested` column is added by migration v5 (idempotent ALTER) and is cleared on the next `UpsertAsync` (re-registration), so a fresh process always starts with a clean slate.

### Screenshot forwarding

When an agent captures a Playwright or FlaUI failure screenshot, the file lives on the agent's local disk — the server serving the dashboard can't see it. `RemoteScreenshotUploader.TryUploadAsync` (in `AiTestCrew.Agents/Shared/`) is invoked from `BaseWebUiTestAgent.CaptureScreenshotAsync` and `BaseDesktopUiTestAgent.CaptureScreenshot`. When `TestEnvironmentConfig.ServerUrl` is set (agent mode), it POSTs the file to `/api/screenshots`, which saves it into the server's `PlaywrightScreenshotDir`. The step detail line still reads `"...| Screenshot: <filename>"`, and the existing `/screenshots/{filename}` static handler resolves to the uploaded copy. When `ServerUrl` is empty (legacy local mode), the uploader is a no-op — the screenshot is served directly from the local dir.

The endpoint strips directory components from the uploaded filename (`Path.GetFileName`) and rejects paths containing `..` to prevent traversal.

### Capability strings

Capabilities are free-form strings — there is no enum. An agent advertises the strings it can handle via `--capabilities <comma-separated>` (or `TestEnvironmentConfig.AgentCapabilities`); the queue's claim query matches them against `run_queue.target_type` with a literal `IN (...)` predicate. Strings are the `TestTargetType.ToString()` values; mismatching the case or hyphenation silently shifts a job out of the agent's reach.

| Capability string | Source | Notes |
|---|---|---|
| `UI_Web_MVC` | Web UI tests on the legacy ASP.NET MVC stack | Default cap on every UI agent |
| `UI_Web_Blazor` | Web UI tests on the Brave Cloud Blazor stack | Default cap on every UI agent |
| `UI_Desktop_WinForms` | Desktop UI tests via FlaUI | Default cap on every UI agent |
| `API_REST` / `API_GraphQL` | API agents | Pure API runs execute in the server process; not normally queued |
| `AseXml_Generate` / `AseXml_Deliver` | aseXML generation and delivery | Delivery is queued when post-step deferral kicks in |
| `Db_SqlServer` | DB-check post-steps (REQ-002) | Add when the agent's host has network reach to the customer's SQL Server |
| `Event_AzureServiceBus` | Service Bus event-assert post-steps (REQ-004) | Add when the agent's host has outbound reach to the customer's Service Bus namespace and the right Azure AD identity (or connection string) |

The omission default — `--capabilities` not set — is `UI_Web_Blazor,UI_Web_MVC,UI_Desktop_WinForms`. To enable a remote agent for event-assert post-steps, advertise it explicitly:

```bash
dotnet run --project src/AiTestCrew.Runner -- --agent --name "<host>" \
  --capabilities UI_Web_Blazor,UI_Web_MVC,UI_Desktop_WinForms,Event_AzureServiceBus
```

Two scenarios drive remote dispatch of event-assert post-steps:

1. The local test runner has no outbound access to the customer's Service Bus namespace (firewall) but a centralised agent does.
2. The env's Service Bus uses Azure AD auth backed by a managed identity that lives only on the centralised agent host.

In both cases the WebApi enqueues the deferred post-step with `target_type = 'Event_AzureServiceBus'`; the centralised agent's claim query picks it up; results round-trip back to the originating run via the existing `DeferredVerificationRequest.CapturedTokens` plumbing (REQ-002). No new wire shape; no new endpoint; just the new capability string on the agent's `--capabilities` list.

### Recording dispatch

Interactive recording (`--record`, `--record-setup`, `--record-verification`, `--auth-setup`) can't run on the server for the same reason Web/Desktop tests can't — it needs a live desktop session. The queue extends naturally: `RunQueueEntry.JobKind` distinguishes `"Run"` (existing path through `TestOrchestrator`) from `"Record"`, `"RecordSetup"`, `"RecordVerification"`, and `"AuthSetup"`. The WebApi's `POST /api/recordings` builds the matching request DTO, serializes it into `RequestJson`, and enqueues with `TargetType` set to a capability the agent already advertises — recording reuses the replay capability set (`UI_Web_MVC`, `UI_Web_Blazor`, `UI_Desktop_WinForms`), so no new capability strings were added.

`JobExecutor.ExecuteAsync` branches on `JobKind`: `"Run"` → `TestOrchestrator.RunAsync` (returns `TestSuiteResult`); recording kinds → `IRecordingService.Record*Async` (returns `RecordingResult`). Both are normalized into a `JobOutcome(Success, Summary, Error?)` that `AgentRunner` reports to the queue.

`IRecordingService` is the extracted form of what used to be ~600 lines of inline `--record*` code in `Runner/Program.cs`. The CLI now validates its args, opens a logger factory, and delegates to the service — so CLI and agent share exactly the same recording, persistence, and auto-parameterisation logic. The only caller-side differences are cosmetic (CLI prints a Spectre step table via `PrintRecordingResult`).

### Per-agent concurrency

`TestOrchestrator` parallelizes objective execution throttled by the global `MaxParallelAgents` (default 4). Most agents (API, aseXML, Blazor UI) cope fine. The **legacy ASP.NET MVC** backend cannot — concurrent authenticated sessions for the same user trigger single-session enforcement, shared ASP.NET session-state corruption, or dev-server overload, producing 15-second Playwright timeouts. `LegacyWebUiTestAgent` therefore wraps its own `ExecuteAsync` with a static `SemaphoreSlim(1, 1)` so its objectives run sequentially within a Runner process. Other agents continue to use the global budget unchanged.

When adding a new agent that hits a fragile backend, copy this pattern: a static semaphore + an `ExecuteAsync` override that gates on it. Reach for it only if parallel execution actually fails — API / Blazor agents don't need it.

### Single-objective heading

In Reuse/VerifyOnly mode, `TestOrchestrator.RunAsync` overwrites its `objective` parameter with the test set's stored `Objective` field (the parent objective entered when the set was first created). That flows through to `PersistedExecutionRun.Objective` and the dashboard's execution-detail heading. When the caller filters to a single objective (`--objective` on the CLI, single-case Run button in the dashboard), the orchestrator narrows `objective` back to the filtered task's `Description` (the objective's display name) so the header matches what actually ran. Test-set-level runs still show the parent objective.

### Security

Agents authenticate via the owning user's API key (the existing `X-Api-Key` middleware). The `claimed_by` column stores the agent id, and the agent must supply the same id when posting progress/result, preventing cross-agent claim theft.

### New config (`TestEnvironmentConfig`)

| Field | Default | Purpose |
|---|---|---|
| `AgentHeartbeatTimeoutSeconds` | 120 | Server-side: how long before a silent agent is marked Offline |
| `AgentName` | (empty → `$COMPUTERNAME`) | Runner-side: display name on the dashboard |
| `AgentCapabilities` | (empty → all three UI targets) | Runner-side: comma-separated target types this agent accepts |
| `AgentPollIntervalSeconds` | 10 | Runner-side: idle poll cadence |
| `AgentHeartbeatIntervalSeconds` | 30 | Runner-side: heartbeat cadence |

---

### Agent roles + run-trigger pinning (REQ-010)

By default every agent can claim any job matching its capabilities, which means a QA engineer recording on their laptop can accidentally have their browser hijacked by an execution run. REQ-010 adds two orthogonal filters to the claim path.

**Agent role** — each agent has a  column (default , back-compat with all existing agents):

| Role | Claims | Skips |
|---|---|---|
|  | , , ,  jobs |  jobs |
|  |  jobs (including deferred-verify + verify-only) | Recording kinds |
|  (default) | Everything the capabilities allow | Nothing |

Set at registration:  (CLI flag) or  in appsettings.

**Required tags** — free-form labels stored as a JSON array on the agent (). A  can carry ; the claim loop rejects any agent whose tag set is not a superset of the job's tags. Empty/null required_tags = any agent.

**Preferred-agent pin** —  locks a job to a specific agent by ID. Only that agent's claim attempt succeeds; all other agents skip the entry. The  run-trigger accepts  and  fields, with the same pre-validation as the recording endpoint (agent must be registered, have the right capability, and have role  or ).

**Claim-deadline sweep** —  sweeps jobs older than  (default 600) from  → , writing the reason (e.g. "No online agent with required role/tags claimed within 600s") into the  column.  (default) keeps the pin active until the deadline;  removes the pin so any matching agent can claim instead.

**Schema change** — migration v11 → v12 adds  and  to , and  +  to . All ALTERs are idempotent ( guard before each column add).

**UI** — the  component surfaces a dropdown in the run-trigger button group: "Any execution agent" (default) or a specific online Execution/Both agent. The agents panel shows a role chip (colour-coded: Recording=purple, Execution=blue, Both=grey) and tag chips.

### LLM Proxy for Distributed Agents

Distributed agents run from a sanitised pack that strips `LlmApiKey` along with every other secret. Without an API key the agent fails any run that calls the LLM (generated tests, run summaries, the chat assistant). REQ-011 resolves this with a server-side proxy.

**Server side — `POST /api/llm/chat`** (`src/AiTestCrew.WebApi/Endpoints/LlmEndpoints.cs`):
- Accepts `{ model?, messages[], maxTokens?, temperature? }`.
- Authenticated by the existing `X-Api-Key` middleware — no new auth surface.
- Calls the server's registered `IChatCompletionService` and returns the response.
- Logs `{userId, agentId, model, inputTokens, outputTokens, latencyMs}` at Information level for cost/usage visibility.

**Agent side — `RemoteChatCompletionService`** (`src/AiTestCrew.Agents/Llm/`):
- Implements `IChatCompletionService`; maps SK `ChatHistory` → proxy request, deserialises response.
- `GetStreamingChatMessageContentsAsync` throws `NotSupportedException` (agents don't stream today).
- Server errors surface `providerError` in the exception message so existing catch blocks log usefully.

**Mode selection — `LlmMode` in `TestEnvironmentConfig`:**
| Value | Behaviour |
|---|---|
| `Auto` (default) | Local key when `LlmApiKey` is set; `RemoteProxy` when `LlmApiKey` is blank and `ServerUrl`+`ApiKey` are set. |
| `Local` | Always use the local key — even when a `ServerUrl` is configured. |
| `RemoteProxy` | Always route through the proxy — useful for testing the proxy from a dev box. |

The `Auto` default means **the server and dev box behaviour is unchanged** (they have `LlmApiKey` populated). Sanitised agent packs have no `LlmApiKey`, so `Auto` automatically selects `RemoteProxy`.

## Deferred Post-Delivery Verification

aseXML delivery objectives frequently attach a UI verification that only becomes meaningful after Bravo has processed the uploaded file — typically a 60–180 s delay. Running that delay inline with `Task.Delay` would hold the executing agent slot open for the whole period; with `MaxParallelAgents = 4` and many concurrent deliveries, most slots would spend their time sleeping. Deferred verification decouples the wait from the executing thread: after a delivery uploads, its verifications are queued with a future claim time and the agent slot is freed immediately. When the due time arrives, any compatible agent on its existing poll loop claims and runs the verification. Failed attempts re-enqueue (instead of in-handler polling) so the agent slot is held only during an actual attempt — ~10–30 s — not across the entire wait window.

### Why a separate coordination path

The deferred flow lives alongside the Phase 4 dispatch path, but solves a different problem:

| | Phase 4 dispatch | Deferred verification |
|---|---|---|
| What's queued | A whole run the dashboard triggered | A follow-up the delivery agent itself schedules |
| When the entry becomes claimable | Immediately (`not_before_at = NULL`) | After the configured wait × early-start fraction |
| Retry on failure | Single attempt; failure is terminal | Re-enqueue with new `not_before_at` up to a deadline, then terminal |
| Parent run state while waiting | Running (agent has the slot) | `AwaitingVerification` (no slot held) |

### Retry model — early-exit polling via re-enqueue

The verification's `WaitBeforeSeconds` is the **deadline** for a green result, not the wait before a single attempt. With the defaults:

| Time (relative to delivery) | Event |
|---|---|
| `wait × VerificationEarlyStartFraction` (default 0.5) | **Attempt 1** |
| each `VerificationRetryIntervalSeconds` (default 30) after a fail | Retry (re-enqueued with new `not_before_at`) |
| `wait + VerificationGraceSeconds` (default 30) | Absolute deadline; last attempt is clamped to this boundary |
| past deadline | Pending row marked `Failed`, parent run finalises as `Failed` |

Verifications only read + assert UI state, so re-running is idempotent. Set `VerificationGraceSeconds = 0` to make `wait` a hard ceiling.

### Schema (v6)

`DatabaseMigrator.cs` v5 → v6 via idempotent `ALTER`s (reuse the `ColumnExists` helper) and one `CREATE TABLE IF NOT EXISTS`.

```sql
-- new columns on run_queue
ALTER TABLE run_queue ADD COLUMN not_before_at         TEXT;     -- UTC ISO; NULL = claim immediately
ALTER TABLE run_queue ADD COLUMN deadline_at           TEXT;     -- UTC ISO; cutoff for retries
ALTER TABLE run_queue ADD COLUMN attempt_count         INTEGER NOT NULL DEFAULT 0;
ALTER TABLE run_queue ADD COLUMN parent_queue_entry_id TEXT;     -- stable id across retries (= pending_id)
ALTER TABLE run_queue ADD COLUMN parent_run_id         TEXT;     -- links back to the originating run

CREATE INDEX idx_run_queue_claim       ON run_queue (status, target_type, not_before_at);
CREATE INDEX idx_run_queue_parent_run  ON run_queue (parent_run_id, status);

CREATE TABLE run_pending_verifications (
    pending_id              TEXT PRIMARY KEY,            -- stable across retries; first queue entry's id
    parent_run_id           TEXT NOT NULL,
    current_queue_entry_id  TEXT NOT NULL,               -- latest attempt's queue row
    module_id               TEXT NOT NULL,
    test_set_id             TEXT NOT NULL,
    delivery_objective_id   TEXT NOT NULL,
    first_due_at            TEXT NOT NULL,
    deadline_at             TEXT NOT NULL,
    attempt_count           INTEGER NOT NULL DEFAULT 0,
    status                  TEXT NOT NULL,               -- Pending | Completed | Failed | Cancelled
    result_json             TEXT,                        -- final attempt's PersistedObjectiveResult
    attempt_log_json        TEXT,                        -- append-only [{ attempt, at, passed }]
    created_at              TEXT NOT NULL,
    completed_at            TEXT
);
```

**Why a separate table rather than merging into `execution_runs.data`:** `PersistedExecutionRun` is a single JSON blob written last-writer-wins. Concurrent verifications finalising on two different agents would clobber each other. The pending table is the authoritative "is this run still outstanding?" state; the run JSON is merged + saved once, atomically, when the last pending row turns terminal.

### Claim-time filtering

`SqliteRunQueueRepository.ClaimNextAsync` extends the existing claim query with:

```
AND (not_before_at IS NULL OR not_before_at <= $nowIso)
```

`$nowIso` is `DateTime.UtcNow.ToString("O")`; stored timestamps use the same format, so string comparison is chronological. The existing FIFO `ORDER BY created_at ASC` is preserved, so ready entries still surface oldest-first.

### End-to-end flow

1. Delivery agent uploads XML successfully. If `AseXml.DeferVerifications = true` and any attached verification's `WaitBeforeSeconds > VerificationDeferThresholdSeconds`, it **does not** run the verification inline.
2. Agent builds a `DeferredVerificationRequest` carrying a full snapshot of the delivery context — `MessageID`, `TransactionID`, `EndpointCode`, `RemotePath`, `UploadedAs`, plus every resolved template field — and the list of verifications to replay. **Context is not re-read from history at claim time** — that would race with a concurrent second delivery for the same test set and use the wrong MessageID.
3. Agent enqueues one `run_queue` row (`mode=VerifyOnly`, `not_before_at=now + wait × fraction`, `deadline_at=now + wait + grace`) and inserts a matching `run_pending_verifications` row (`pending_id` = queue entry id). Synthetic `AwaitingVerification` steps are emitted onto the objective for UI countdown display (detail carries `firstDueAtUtc` + `deadlineAtUtc` as ISO strings).
4. Delivery objective returns with status `AwaitingVerification`; the orchestrator's `FromSuiteResult` detects this and sets the run-level status accordingly. Agent reports the **top-level** queue entry as success (`/api/queue/{jobId}/result`); the `QueueEndpoints` handler checks `IPendingVerificationRepository.CountPendingForRunAsync` and, if pending rows exist, calls `RunTracker.MarkAwaitingVerification` instead of `Complete`.
5. At `not_before_at`, any capable Runner agent claims the deferred entry via the normal `/api/queue/next` poll. `JobExecutor.ExecuteRunAsync` detects the payload via the `"kind": "DeferredVerification"` discriminator, deserialises to `DeferredVerificationRequest`, and passes it to `TestOrchestrator.RunAsync` through a new `deferredVerification` parameter.
6. `AseXmlDeliveryAgent.VerifyOnlyAsync` detects `task.Parameters["DeferredVerificationRequest"]`, skips `GetLatestDeliveryContextAsync`, and runs each verification against the snapshot context.
7. Outcome handling in `DeferredVerifyAsync`:
   - **All pass** → `MarkCompletedAsync(resultJson)` + `TryFinaliseParentRunAsync`.
   - **Any fail AND `UtcNow < deadline_at`** → append to `attempt_log_json`, re-enqueue a fresh queue row (`not_before_at = UtcNow + VerificationRetryIntervalSeconds`, clamped to deadline), update `run_pending_verifications.current_queue_entry_id`. Return `AwaitingVerification` so the queue entry for this attempt reports completion but the parent run stays Awaiting.
   - **Any fail AND past deadline** → `MarkFailedAsync` + `TryFinaliseParentRunAsync`.
8. `TryFinaliseParentRunAsync` transactions the merge: if `CountPendingForRunAsync(parentRunId) == 0`, load the existing `PersistedExecutionRun`, overlay each pending row's `result_json` onto the matching objective (replacing `AwaitingVerification` steps with the final attempt's steps + a rollup), recompute aggregate counts, rewrite `Status` (Passed/Failed/Error), and regenerate `Summary` with a short factual line so the provisional "Inconclusive / awaiting verification" LLM text doesn't linger in the UI.

### Distributed coordination over REST

The deferred flow is REST-first: the Runner agent may be on a different host than the Docker-hosted WebApi, so direct SQLite access from the agent is not an option. Coordination goes through dedicated endpoints:

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/queue` | Enqueue a queue entry (deferred enqueue from the agent, plus the retry re-enqueue) |
| `GET` | `/api/queue/{jobId}` | Fetch a single queue entry (includes v6 fields) |
| `POST` | `/api/pending-verifications` | Insert a pending row for a newly-deferred verification |
| `GET` | `/api/pending-verifications/{pendingId}` | Read attempt log before building the next attempt's entry |
| `POST` | `/api/pending-verifications/{pendingId}/attempt` | Update current queue entry id + attempt count on a retry |
| `POST` | `/api/pending-verifications/{pendingId}/complete` | Terminal success; caller then checks pending count and calls finalise |
| `POST` | `/api/pending-verifications/{pendingId}/fail` | Terminal deadline-exceeded failure |
| `GET` | `/api/pending-verifications/by-run/{runId}` | Server-side finalise merge + UI status endpoint |
| `GET` | `/api/pending-verifications/count?runId=` | Fast "are we done?" check |

The Runner's `ApiClientRunQueueRepository` + `ApiClientPendingVerificationRepository` implement `IRunQueueRepository` + `IPendingVerificationRepository` as thin HTTP proxies over these endpoints; the server-side `SqliteRunQueueRepository` + `SqlitePendingVerificationRepository` are the authoritative implementations. Runner DI registers the API-client versions when `ServerUrl` is set (remote mode). Methods the agent hot-path doesn't need (`ClaimNextAsync`, `ListRecentAsync`, janitor operations, cancellation sweeps) throw `NotSupportedException` in the API-client so accidental server-side calls surface loudly.

### Janitor (`AgentHeartbeatMonitor` extensions)

Two new sweeps run on the same 30 s tick as the agent-offline sweep:

1. **Stale queue claim reclaim** — `IRunQueueRepository.ListStaleClaimsAsync(2 × AgentHeartbeatTimeoutSeconds)` + `ReleaseClaimAsync(id)`: any `Claimed` entry whose claiming agent has been silent longer than two heartbeats resets to `Queued`. VerifyOnly work is idempotent (assertions only), so re-execution is safe.
2. **Deadline timeout** — `IPendingVerificationRepository.ListExpiredAsync(cutoff)` where cutoff = `UtcNow − VerificationMaxLatencySeconds` (default 3600): any `Pending` row whose deadline elapsed without a terminal attempt is marked `Failed` with a synthetic `deferred-verify-timeout` step, and the parent run is finalised with the same merge path.

Both sweeps are wrapped in individual try/catch so one failure doesn't stop the other.

### Run-status surfacing (`/api/runs/{id}/status`)

The status endpoint always attaches `pendingVerifications[]` joined from `run_pending_verifications` (Pending rows only) so the UI can render per-verification countdowns. When `pendingView.Count == 0` but there are any rows for the run AND the in-memory `RunTracker` still says `AwaitingVerification`, the endpoint reloads the finalised run from history and writes the terminal status back into the tracker — this is what bridges the gap between "background agent finished the last pending row" and "dashboard sees Passed/Failed without a refresh".

Cancellation is a two-repo sweep: `POST /api/runs/{id}/cancel` calls `CancelAsync` on the top-level job, `CancelPendingForRunAsync` on both `run_queue` and `run_pending_verifications`, and marks the tracker `Cancelled`.

### New config (`AseXmlConfig`)

| Field | Default | Purpose |
|---|---|---|
| `DeferVerifications` | `true` | Global opt-in. `false` = legacy inline `Task.Delay` behaviour (also useful for local debugging). |
| `VerificationDeferThresholdSeconds` | `30` | Short waits run inline regardless of `DeferVerifications` — queueing overhead isn't worth it. |
| `VerificationEarlyStartFraction` | `0.5` | When the first attempt runs, expressed as a fraction of `WaitBeforeSeconds`. |
| `VerificationRetryIntervalSeconds` | `30` | Gap between failed attempts. |
| `VerificationGraceSeconds` | `30` | Added to `WaitBeforeSeconds` for the absolute deadline. Set to `0` for a hard ceiling at `WaitBeforeSeconds`. |
| `VerificationMaxLatencySeconds` | `3600` | Janitor: after this long in `Queued`, give up and fail. |
| `DeferredPollCliIntervalSeconds` | `10` | CLI live-view poll cadence in `dotnet run -- --reuse ...` flows. |

CLI override: `--no-defer-verifications` forces the synchronous inline path for a single invocation.

### UI surfacing

- `TestStatus.AwaitingVerification` renders as a cyan "Awaiting" pill (`StatusBadge` + short display name).
- `StepList` renders `AwaitingVerification` step rows with a ⏳ icon and a live per-second countdown parsed from `firstDueAtUtc` in the step detail (ISO UTC, timezone-safe).
- `TriggerRunButton` + `TriggerObjectiveRunButton` replace their spinners with a quiet ⏳ cyan chip during the `AwaitingVerification` phase — a spinner would falsely imply active execution.
- `TestSetDetailPage` polls `['testSet', ...]` + `['runs', ...]` at 3 s while `ActiveRunContext.isTestSetRunning` is true, as a belt-and-braces complement to the invalidate-on-status-change path in `ActiveRunContext`.
- `ExecutionDetailPage` uses a dynamic `refetchInterval` that stops at terminal statuses (`Passed`, `Failed`, `Error`, `Skipped`, `Cancelled`).
- **Authoring-time Mode pill** — `PostStepsPanel` (and the legacy `VerificationsPanel` in `AseXmlDeliveryTestCaseTable`) renders a per-row **Inline** / **Deferred** pill in a dedicated `Mode` column. Computed across the whole post-step list via `computeIsDeferred(postSteps, cfg)` so all rows in an objective flip together — mirrors the per-objective `PostStepOrchestrator.ShouldDefer` rule. Config is fetched from `GET /api/config/asexml-verification` (a sibling of `/api/config/{environments,endpoints,api-stacks}` in `Program.cs`) using React Query with `staleTime: Infinity` so the request is deduped across rows.
- `QueueBanner` reads `notBeforeAt` on queue entries — entries with a future `notBeforeAt` show as "Deferred verification — next attempt in ~N min" rather than "waiting for agent".

---

## Seamless Authentication Recovery

Authentication failures used to be a dead-end: an expired JWT or a bumped UI session failed the test, the user noticed, dropped to a terminal, ran `--auth-setup`, and re-triggered the run. Seamless Auth turns that into: detect centrally → silently retry → if recovery isn't possible, pause the run and prompt the user once → one click resumes every paused run sharing the same scope. A complementary pre-flight panel surfaces stale cached storage state *before* the user kicks off a run so the typical case (TTL elapsed) is caught without anyone having to fail first.

### Three layers, three triggers

| Layer | Scope | What it does |
|---|---|---|
| **1 — Silent auto-recovery** | API + UI agents | Categorise auth failures via `AuthRequiredException`. API: 401/403 → invalidate cached JWT → retry once. UI: per-step URL check against `Auth.LoginRedirectUrlPatterns` → if matched → delete storage state and best-effort re-run TOTP-automated login. Recovers the common case (rotated JWT, expired cookie) with zero user interaction. |
| **2 — Pause-and-resume orchestration** | WebApi + queue + janitor | `AwaitingAuth` run/queue state mirroring `AwaitingVerification`. `run_auth_refreshes` table with dedup-by-scope. Agent catches `AuthRequiredException`, registers a refresh, re-enqueues the same work with `auth_refresh_id` + far-future `not_before_at`. Janitor releases dependent entries when the refresh completes. |
| **3 — Dashboard surfaces + local prompt** | UI + Runner CLI | Reactive `AuthRefreshBanner` polls active refreshes — surface · env · paused-run count, one-click "Refresh auth" enqueues an `AuthSetup` job to the right agent. Pre-flight `AuthHealthPanel` polls `/api/auth-health` and warns about stale storage state before a run is even triggered. CLI mode prints a Spectre hint with the exact `--auth-setup` command and exits with code 2. |

### Auth surfaces

The system is scoped to three surfaces (`AuthSurface` enum in `Core.Models.Enums`):

| Surface | Storage | Recovery flow |
|---|---|---|
| `Api` | In-memory JWT cache (per `(env, stack)` in `LoginTokenProvider`) | Re-acquire from `AuthUsername` + `AuthPassword`. Any agent — or the WebApi itself — can do this. |
| `WebBlazor` | Playwright `StorageState` JSON file (per env) | Delete file + re-run Azure SSO with TOTP-automated MFA via `BraveCloudUiTotpSecret`. Must run on the agent that owns the file. |
| `WebMvc` | Playwright `StorageState` JSON file (per env) | Delete file + re-run forms login. Must run on the agent that owns the file. |

WinForms is intentionally out of scope — desktop has no auth concept; the app is launched fresh per test case.

### Detection points

**API path** (`ApiTestAgent.ExecuteTestCaseAsync`):

1. Send the request as normal.
2. On 401/403 and `Auth.AutoRecoverApi = true`: `await tokenProvider.InvalidateAsync(ct)` clears the cache under the existing semaphore, then the request is rebuilt (an `HttpRequestMessage` can only be sent once) and resent.
3. If the retry returns 401/403 and `Auth.PauseOnAuthFailure = true`, throw `AuthRequiredException(env, AuthSurface.Api, stackKey, ...)`.
4. Other status codes preserve existing semantics (`Failed` step with detail).

**UI path** (`BaseWebUiTestAgent.CheckForLoginRedirectAsync`, run after each successful step):

1. Read `page.Url`. Match against `Auth.LoginRedirectUrlPatterns` (case-insensitive substring; defaults to `login.microsoftonline.com`, `/Account/Login`).
2. If matched and `Auth.AutoRecoverUi = true`: best-effort call `TryRecoverFromLoginRedirectAsync(browser, ct)` — subclasses delete the cached storage state and re-run their one-time auth setup. The current step's test case is still abandoned, but the storage state is now fresh for future runs.
3. If `Auth.PauseOnAuthFailure = true`, throw `AuthRequiredException(env, surface, null, ...)`.

`AuthRequiredException` propagates through every catch-all in the agent stack — each adds an explicit `catch (AuthRequiredException) { throw; }` re-throw guard before its broad catch — so the exception lands in either:

- The agent-mode `JobExecutor` (distributed runs) — registers a refresh, re-enqueues the work, returns success-with-`AuthRefreshId`.
- The local-mode Runner top-level catch in `Program.cs` (CLI runs) — prints a Spectre remediation hint and exits with code 2.

The post-step orchestrator's `RunOneInlineAsync` carries the same re-throw guard, so a Legacy MVC verification dispatched as a sub-step from an aseXML delivery flows the auth exception through to the queue dispatcher rather than swallowing it as a plain `Error` step. The `JobExecutor`'s deferred-verification branch also catches `AuthRequiredException` and parks via the same helper — both inline and deferred paths are covered.

#### `AuthRequiredException` vs `LoginFailedException` — different remediation paths

Two distinct auth-failure shapes flow through the API path, and they're caught separately on purpose:

| Exception | Thrown when | Caught where | Remediation |
|---|---|---|---|
| `AuthRequiredException` | The cached JWT was rejected and the silent retry-once didn't recover (or the surface is UI). The configured creds *do* work, but the cached session is bad. | `JobExecutor.TryParkOnAuthRefreshAsync` — parks the run as `AwaitingAuth`, surfaces in the reactive `AuthRefreshBanner`. | Dashboard "Refresh auth" click. For UI surfaces, a fresh recording. For API, the next agent attempt re-acquires from creds via `LoginTokenProvider`. |
| `LoginFailedException` | `LoginTokenProvider.LoginAsync` itself returned non-2xx (typically 401/403 with "username or password is incorrect"). The configured creds are wrong / locked / rotated. | `ApiTestAgent.ExecuteTestCaseAsync` — returned as a clean `TestStep.Err` with message "Authentication failed: SEC API rejected the configured credentials… Check TestEnvironment.AuthUsername / AuthPassword in appsettings.json…". | **Human** edit of `appsettings.json` and / or unlocking the account. The dashboard refresh banner deliberately does **not** fire — for API surface its `/start` handler would just succeed silently and the next test would fail again. |

The carrier on `LoginFailedException` (`LoginUrl`, `Username`, `HttpStatusCode`, trimmed `ResponseSnippet`) is enough for the step-detail line to point straight at the fix. Don't repurpose this for "session bumped" cases — that's `AuthRequiredException`'s job.

### Schema (v8)

```sql
CREATE TABLE run_auth_refreshes (
    id                  TEXT PRIMARY KEY,
    env_key             TEXT NOT NULL,
    surface             TEXT NOT NULL,        -- Api | WebBlazor | WebMvc
    stack_key           TEXT,                  -- only set for Api
    agent_id            TEXT,                  -- NULL for Api; set for UI surfaces
    requested_by_run_id TEXT,
    status              TEXT NOT NULL,         -- Pending | InProgress | Completed | Failed | Cancelled
    auto_attempt_count  INTEGER NOT NULL DEFAULT 0,
    last_attempt_at     TEXT,
    created_at          TEXT NOT NULL,
    completed_at        TEXT,
    error_message       TEXT
);

CREATE UNIQUE INDEX uq_auth_refresh_active_scope
    ON run_auth_refreshes (env_key, surface, COALESCE(stack_key, ''), COALESCE(agent_id, ''))
    WHERE status IN ('Pending', 'InProgress');

ALTER TABLE run_queue ADD COLUMN auth_refresh_id TEXT;
```

The unique partial index is the **dedup-by-scope** mechanism: at most one active row per `(env, surface, stack, agent)`. Concurrent failures at the same scope race the INSERT; the loser falls back to returning the existing active row. One refresh row → many paused queue entries → one-click recovery for all of them.

### Pause-and-resume state flow

```
            ┌────────────────────────────────────────┐
            │ Agent catches AuthRequiredException    │
            │ (or proactive click in AuthHealthPanel)│
            └────────────────┬───────────────────────┘
                             │
            ┌────────────────▼─────────────────┐
            │ POST /api/auth-refreshes         │
            │  → InsertOrJoinAsync(scope)      │
            │  → returns existing active row OR│
            │    creates new Pending row       │
            └────────────────┬─────────────────┘
                             │   (failure path only)
            ┌────────────────▼─────────────────┐
            │ Re-enqueue same RequestJson with:│
            │  • auth_refresh_id = id          │
            │  • not_before_at = UtcNow + 7 d  │
            │  • parent_run_id = job.JobId     │
            └────────────────┬─────────────────┘
                             │
            ┌────────────────▼─────────────────┐
            │ Report queue result with         │
            │ AuthRefreshId set                │
            │  → run_queue: Completed          │
            │  → RunTracker: AwaitingAuth      │
            └────────────────┬─────────────────┘
                             │   ◄── AuthRefreshBanner appears
                             │   ◄── User clicks "Refresh auth"
            ┌────────────────▼──────────────────┐
            │ POST /api/auth-refreshes/{id}/start│
            │  → marks InProgress                │
            │  • Api: marks Completed in-process │
            │  • UI:  enqueues AuthSetup queue   │
            │    entry for the right agent       │
            └────────────────┬──────────────────┘
                             │
            ┌────────────────▼──────────────────┐
            │ Agent runs RecordingService.       │
            │   AuthSetupAsync (browser + TOTP)  │
            │  → POST /api/auth-refreshes/{id}/  │
            │       complete (or /fail)          │
            └────────────────┬──────────────────┘
                             │
            ┌────────────────▼──────────────────┐
            │ Janitor sweep (30 s tick)          │
            │  • Completed:                      │
            │    UPDATE run_queue                │
            │    SET auth_refresh_id = NULL,     │
            │        not_before_at = NOW         │
            │    WHERE auth_refresh_id = id      │
            │  • Failed: cancel dependent entries│
            └────────────────┬──────────────────┘
                             │
            ┌────────────────▼──────────────────┐
            │ Next agent claim picks up the entry│
            │  → Run resumes from failing step   │
            │  → RunTracker → Running            │
            └───────────────────────────────────┘
```

### Janitor sweeps

`AgentHeartbeatMonitor.SweepAuthRefreshesAsync` runs on the existing 30 s tick:

1. **Time out stale `InProgress`** past `Auth.AuthRefreshMaxLatencySeconds` (default 300 s) → mark `Failed` with timeout error.
2. **For Completed refreshes since last tick** (small lookback overlap for safety) → call `SqliteRunQueueRepository.ReleaseForAuthRefreshAsync(id)` (clears `auth_refresh_id`, resets `not_before_at = now`).
3. **For Failed refreshes** → call `CancelForAuthRefreshAsync(id, error)` (marks dependent queue entries `Cancelled` with the failure error).

### AuthSetup queue payload

The `/start` endpoint enqueues a queue row with `JobKind = "AuthSetup"`, `TargetType = "UI_Web_Blazor" | "UI_Web_MVC"`, and `RequestJson` matching `AuthSetupRequest(Target, EnvironmentKey, AuthRefreshId?)`. The `target` field is the `TestTargetType` string (used by `RecordingService.AuthSetupAsync` to pick Blazor vs Legacy MVC flow); `authRefreshId` flows back so the agent's `JobExecutor.ExecuteAuthSetupAsync` can call `MarkCompletedAsync` (or `MarkFailedAsync`) on the refresh row after the recording finishes — without it the `run_auth_refreshes` row would stay `InProgress` until the janitor's 5-minute timeout.

### `AuthSetupAsync` hardening — positive proof of auth

The Blazor branch of `RecordingService.AuthSetupAsync` requires **positive proof** that the SSO flow actually fired before saving state, plus a cookie sanity check. Without these guards, a fresh browser context navigating to a public landing page (or any URL whose first response doesn't redirect before `NetworkIdle` returns) trivially passes the "URL doesn't contain `login.microsoftonline.com`" check on the first poll, and the agent silently saves an empty `{"cookies":[],"origins":[]}` storage-state file that fails on the next test:

1. `sawSsoRedirect` flag inside the wait loop flips true the first time `page.Url` contains `login.microsoftonline.com`. Blazor `isLoggedIn` requires this flag *and* the URL to be back on the configured baseUrl. Failure path: 3-minute timeout returns *"Timed out waiting for Azure SSO redirect — never saw login.microsoftonline.com."*
2. Before `StorageStateAsync`, call `browserCtx.CookiesAsync()` — refuse to save when the count is zero. Belt-and-braces against any future regression that lets the URL check fall through with no real session.

The success log line includes the cookie count so an empty-state regression is visible at a glance: *"Auth state saved → … (12 cookies, valid for 8 hours)"*.

### Local Runner mode (no agent, no WebApi)

`TestOrchestrator.RunAsync` doesn't catch `AuthRequiredException` itself — it lets the exception bubble out via `Task.WhenAll`. The Runner CLI top-level catch in `Program.cs` prints a Spectre remediation hint:

```
Authentication required for env sumo-retail surface WebBlazor.
Session expired — page redirected to login at https://login.microsoftonline.com/...
→ Re-run: dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor --environment sumo-retail
```

…and exits with code 2. CLI auto-recovery (in-process re-auth + retry) is a future extension; if added, do it at the Runner top-level, **not** in the orchestrator.

### Pre-flight health (schema v9)

The reactive flow above only fires after a run has already failed. The pre-flight panel adds a complementary signal: surface stale cached storage state in the dashboard *before* the user kicks off a run.

| Layer | What it does |
|---|---|
| **Agent** | `AuthStateScanner` walks `IEnvironmentResolver.ListKeys()` × `[WebBlazor, WebMvc]` on each heartbeat, stats the resolved storage-state file (`File.GetLastWriteTimeUtc` only — read-only, never opens or locks the file), and ships `{ envKey, surface, fileExists, fileMtimeUtc }` entries on the heartbeat payload. Envs with `EnvironmentConfig.AuthHealthEnabled = false` are skipped. |
| **WebApi** | The heartbeat handler replaces the agent's rows in `agent_auth_state` (PK `(agent_id, env_key, surface)`) wholesale per tick — authoritative for that agent's view. `DELETE /api/agents/{id}` cleans the rows when an agent deregisters. |
| **Endpoint** | `GET /api/auth-health` joins `agent_auth_state` with the `agents` table (filters Offline) and active rows in `run_auth_refreshes`, computes per-(env, surface) status, then groups by **env**. Returns one tile per environment containing the surfaces (Blazor / MVC) that need attention. Surfaces that are Fresh OR already covered by an in-flight refresh are dropped from the env's surface list; an env with no remaining surfaces disappears entirely. Envs with `AuthHealthEnabled = false` are filtered out even when historical rows exist. |
| **UI** | `AuthHealthPanel` polls every 30 s. One tile per env with the env display name + slug at the top, then a row per surface needing attention. Each row has its own status pill (`Never recorded` / `Expired` / `Expiring soon`) and an independent **Refresh** button. Clicking creates a Pending refresh at that scope (`POST /api/auth-refreshes`) then starts it (`POST /api/auth-refreshes/{id}/start`) — same dispatch path the reactive banner uses. Once dispatched, the row moves to the `AuthRefreshBanner` (the active-refresh filter on the health endpoint hides it from this panel to avoid double UI). Errors from the click round-trip are surfaced inline below the row. |

Schema v9 adds:

```sql
CREATE TABLE agent_auth_state (
    agent_id        TEXT NOT NULL,
    env_key         TEXT NOT NULL,
    surface         TEXT NOT NULL,        -- WebBlazor | WebMvc
    file_exists     INTEGER NOT NULL,     -- 0/1
    file_mtime_utc  TEXT,                 -- null when file doesn't exist
    reported_at_utc TEXT NOT NULL,
    PRIMARY KEY (agent_id, env_key, surface)
);

CREATE INDEX idx_agent_auth_state_scope ON agent_auth_state (env_key, surface);
```

**Status thresholds** — given a per-surface TTL `T` (default 8 h via `BraveCloudUiStorageStateMaxAgeHours` / `LegacyWebUiStorageStateMaxAgeHours`) and `Auth.ExpiryWarningHours = W` (default 1):

| Status | Condition |
|---|---|
| `Missing` | No agent has the file. |
| `Stale` | At least one agent has age ≥ `T`. |
| `ExpiringSoon` | At least one agent has age ≥ `T − W` but < `T`. |
| `Fresh` | All agents have age < `T − W`. (Hidden from the panel.) |

**What this catches and what it doesn't** — file mtime catches the common case (TTL elapsed, user forgot to re-record). It does **not** catch server-side cookie invalidation of a fresh-by-age file (admin reset, password change, conditional-access policy change). The reactive flow is the safety net for those — the two systems together cover both axes.

WinForms / API surfaces are deliberately out of scope. WinForms has no auth concept; API tokens live in-memory per WebApi process and have no file mtime to track. Phase 1's silent retry on 401/403 is the right tool for API.

### Configuration

Under `TestEnvironment.Auth` in `appsettings.json` (defaults shown):

```json
"Auth": {
  "AutoRecoverApi": true,
  "AutoRecoverUi": true,
  "LoginRedirectUrlPatterns": ["login.microsoftonline.com", "/Account/Login"],
  "AuthRefreshMaxLatencySeconds": 300,
  "PauseOnAuthFailure": true,
  "ExpiryWarningHours": 1
}
```

| Knob | Effect |
|---|---|
| `AutoRecoverApi` | When `true`, a 401/403 response invalidates the cached JWT and retries the request once before failing. |
| `AutoRecoverUi` | When `true`, a mid-test login redirect deletes the cached storage state and re-runs the existing TOTP-automated login. |
| `LoginRedirectUrlPatterns` | Substrings (case-insensitive) that flag a UI session as bumped to login. Add custom IDP hostnames here. |
| `AuthRefreshMaxLatencySeconds` | Janitor times out `InProgress` refreshes past this. Default 300 s — long enough for Azure SSO + TOTP, short enough that a forgotten refresh doesn't park runs forever. |
| `PauseOnAuthFailure` | Headless-CI escape hatch. When `false`, `AuthRequiredException` is suppressed and tests fail with normal Failed-step semantics. **Don't remove this knob** — CI needs it. |
| `ExpiryWarningHours` | Pre-flight warning window. The auth-health panel surfaces a file as `ExpiringSoon` when its age is within this many hours of the per-surface TTL. |

Per-env opt-out is `EnvironmentConfig.AuthHealthEnabled` (default `true`) under each `Environments.<key>` block. Setting it to `false` hides that env from the pre-flight panel and stops the agent scanner from emitting rows for it.

JSON enum binding is global: `ConfigureHttpJsonOptions` registers `JsonStringEnumConverter` so endpoints that take an enum-typed body (notably `POST /api/auth-refreshes` with `{"surface":"WebBlazor"}`) accept string values. Without this, the dashboard's "Refresh now" button silently fails — the deserializer rejects string enums on input even though `surface = r.Surface.ToString()` is used on output.

### API surface

```
POST   /api/auth-refreshes                         Insert (or join existing active row by scope)
GET    /api/auth-refreshes/active                  Reactive banner data
GET    /api/auth-refreshes/{id}                    Detail
POST   /api/auth-refreshes/{id}/start              UI button — marks InProgress + enqueues AuthSetup job
POST   /api/auth-refreshes/{id}/complete           Agent reports success
POST   /api/auth-refreshes/{id}/fail               Agent reports failure
POST   /api/auth-refreshes/{id}/cancel             User dismiss
GET    /api/auth-health                            Pre-flight panel: env-grouped tiles
```

`POST /api/queue/{jobId}/result` accepts an optional `authRefreshId`. When set, the run is marked `AwaitingAuth` instead of `Completed` / `Failed`.

### Critical files

| Layer | File | Purpose |
|---|---|---|
| Core | `src/AiTestCrew.Core/Exceptions/AuthRequiredException.cs` | Carries `(env, surface, stack?)` — session bumped / cache bad, recoverable via dashboard refresh |
| Core | `src/AiTestCrew.Core/Exceptions/LoginFailedException.cs` | Carries `(loginUrl, username, httpStatus, responseSnippet)` — configured creds are wrong / locked, requires human config fix |
| Core | `src/AiTestCrew.Core/Models/Enums.cs` | `AuthSurface`, `TestStatus.AuthRequired` |
| Core | `src/AiTestCrew.Core/Models/AuthRefreshRequest.cs` | Persisted refresh-request model |
| Core | `src/AiTestCrew.Core/Models/AgentAuthState.cs` | Pre-flight health row model |
| Core | `src/AiTestCrew.Core/Interfaces/IAuthRefreshRepository.cs` | DI contract for refreshes |
| Core | `src/AiTestCrew.Core/Interfaces/IAgentAuthStateRepository.cs` | DI contract for pre-flight scanner reports |
| Core | `src/AiTestCrew.Core/Interfaces/ITokenProvider.cs` | Has `InvalidateAsync` |
| Core | `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | `AuthRecoveryConfig` block (Auth.* knobs) |
| Core | `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` | Per-env `AuthHealthEnabled` flag |
| Auth | `src/AiTestCrew.Agents/Auth/LoginTokenProvider.cs` | `InvalidateAsync` clears cache under semaphore |
| Auth | `src/AiTestCrew.Agents/Auth/AuthStateScanner.cs` | Heartbeat scanner — file mtime per (env, surface) |
| API agent | `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` | 401-retry-once + throw `AuthRequiredException`; separate catch for `LoginFailedException` returns clean step error pointing at appsettings.json |
| Web base | `src/AiTestCrew.Agents/WebUiBase/BaseWebUiTestAgent.cs` | `CheckForLoginRedirectAsync`, virtual `TryRecoverFromLoginRedirectAsync` |
| Blazor | `src/AiTestCrew.Agents/BraveCloudUiAgent/BraveCloudUiTestAgent.cs` | Surface = WebBlazor; recovery via `PerformSsoLoginAsync` |
| MVC | `src/AiTestCrew.Agents/LegacyWebUiAgent/LegacyWebUiTestAgent.cs` | Surface = WebMvc; recovery via `PerformFormsLoginAsync` |
| Recording | `src/AiTestCrew.Agents/Recording/RecordingService.cs` | `AuthSetupAsync` — positive SSO proof + cookie sanity check |
| Recording | `src/AiTestCrew.Agents/Recording/RecordingRequests.cs` | `AuthSetupRequest(Target, EnvironmentKey, AuthRefreshId?)` |
| Post-step | `src/AiTestCrew.Agents/PostSteps/PostStepOrchestrator.cs` | Re-throw guard in `RunOneInlineAsync` |
| Storage | `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` | Schema v8 + v9 |
| Storage | `src/AiTestCrew.Storage/Sqlite/SqliteAuthRefreshRepository.cs` | Dedup-by-scope INSERT |
| Storage | `src/AiTestCrew.Storage/Sqlite/SqliteAgentAuthStateRepository.cs` | Pre-flight rows: `ReplaceForAgentAsync`, `ListForOnlineAgentsAsync` |
| Storage | `src/AiTestCrew.Storage/Sqlite/SqliteRunQueueRepository.cs` | `ReleaseForAuthRefreshAsync`, `CancelForAuthRefreshAsync` |
| Runner remote | `src/AiTestCrew.Runner/RemoteRepositories/ApiClientAuthRefreshRepository.cs` | HTTP-backed |
| Agent dispatch | `src/AiTestCrew.Runner/AgentMode/JobExecutor.cs` | `TryParkOnAuthRefreshAsync`, `ExecuteAuthSetupAsync` settles refresh row |
| Agent dispatch | `src/AiTestCrew.Runner/AgentMode/AgentRunner.cs` | Heartbeat loop invokes `AuthStateScanner` |
| Runner CLI | `src/AiTestCrew.Runner/Program.cs` | Top-level Spectre hint, exit code 2 |
| WebApi | `src/AiTestCrew.WebApi/Endpoints/AuthRefreshEndpoints.cs` | Refresh CRUD + `/start` enqueue |
| WebApi | `src/AiTestCrew.WebApi/Endpoints/AuthHealthEndpoints.cs` | Pre-flight aggregation, env-grouped |
| WebApi | `src/AiTestCrew.WebApi/Endpoints/AgentEndpoints.cs` | Heartbeat upserts auth-state rows |
| WebApi | `src/AiTestCrew.WebApi/Endpoints/QueueEndpoints.cs` | `/result` honours `authRefreshId` |
| WebApi | `src/AiTestCrew.WebApi/Services/RunTracker.cs` | `MarkAwaitingAuth` |
| WebApi | `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` | `SweepAuthRefreshesAsync` |
| UI | `ui/src/components/AuthRefreshBanner.tsx` | Reactive banner |
| UI | `ui/src/components/AuthHealthPanel.tsx` | Pre-flight panel — env-grouped tiles |
| UI | `ui/src/components/StatusBadge.tsx` | Amber `AwaitingAuth` / `AuthRequired` pills |
| UI | `ui/src/api/authRefreshes.ts` | `fetchActiveAuthRefreshes`, `createAuthRefresh`, `startAuthRefresh`, `cancelAuthRefresh` |
| UI | `ui/src/api/authHealth.ts` | `fetchAuthHealth` |

### Future extensions

- **CLI auto-recovery** — wire the Runner top-level catch to invoke `RecordingService.AuthSetupAsync` in-process, then retry the failed step. Out of scope for v1; if added, do it at the Runner top-level, not inside `TestOrchestrator`.
- **More auth surfaces** — add a value to `AuthSurface`, plumb through `LoginRedirectUrlPatterns`, override `Surface` + `TryRecoverFromLoginRedirectAsync` on the new agent. The existing pause-and-resume pipeline carries the new surface through unchanged.
- **Per-stack API creds** — today `AuthUsername` / `AuthPassword` are global. Multi-tenant API auth would move them onto `ApiStackConfig` and adjust `LoginTokenProvider` construction in `ApiTargetResolver`.
- **Loop multiple envs in one AuthSetup browser session** — when a customer shares creds across envs but uses per-env URLs, today each env requires its own `AuthSetup` (browser + SSO). A future variant could keep the browser open, navigate per-env in turn, and reuse the cached Microsoft login cookie so the user only logs in once.
- **WinForms** — only relevant if desktop apps gain authenticated workflows; would need a new surface enum value and a recovery story (probably out-of-process credential prompt).

---

## Chat Assistant

The Assistant is a right-edge drawer in the React UI that translates natural-language test-engineering requests into structured actions the user confirms with a click. It reuses the existing LLM wrapper, endpoints, and dispatch infrastructure — it is a routing + presentation layer, not a new execution path.

### Round trip

1. User types a message in the drawer. The client tracks `activeConversationId` in `ChatContext`; the message list itself is fetched from the server via React Query (key: `['chat', 'conversation', userId, conversationId]`), with an optimistic user-bubble layered on top until the server round-trip completes.
2. `ChatDrawer` POSTs `{ message, conversationId?, context: { moduleId?, testSetId? } }` to `/api/chat/message`. The URL-derived `context` lets phrases like "run this" resolve without explicit IDs. When `conversationId` is omitted the server creates a fresh thread and returns its id; the prior transcript is loaded from SQLite, so the client doesn't resend it.
3. `ChatIntentService` looks up (or creates) the conversation, loads the prior messages bounded by `Chat.MaxMessagesPerConversation`, persists the user turn, then builds a **catalog snapshot** from the existing repositories / resolvers: modules + their test sets (with stack/env hints), configured environments, API stacks and modules, Bravo endpoint codes (via `IEndpointResolver.ListCodesAsync` — best-effort), registered agents + capabilities, and — if the request is scoped to a test-set page — that test set's `TestObjectives`. The snapshot is injected into the system prompt alongside the action schema.
4. The LLM is asked to return a single `ChatResponse { reply, actions[] }` JSON. The prompt enumerates every action shape (see below) and forbids inventing IDs/keys that aren't in the catalog. `LlmJsonHelper.DeserializeLlmResponse<ChatResponse>` (shared with `BaseTestAgent`) strips markdown fences and tolerates JSON-with-preamble.
5. The service persists the assistant reply (verbatim text + serialised action cards) and returns `{ reply, actions, conversationId }`. The UI invalidates its conversation queries; the message list re-renders with the persisted turns plus any action cards.

### Action kinds

| Kind | Payload | Executed by |
|---|---|---|
| `navigate` | `{ path }` | Client — React Router `useNavigate(path)`; drawer closes |
| `showData` | `{ title, data }` | Client — data is already resolved server-side; renders as a table (array of homogeneous objects), bulleted list (array of primitives), or JSON pretty-print |
| `confirmRun` | `{ summary, data: RunRequest }` | Client — Execute button calls `POST /api/runs`, then pushes the returned `runId` into `ActiveRunContext` so the existing run banner + polling pick up the run |
| `confirmCreate` | `{ summary, data: { target: "module"\|"testSet", name, moduleId?, description? } }` | Client — `POST /api/modules` or `POST /api/modules/{id}/testsets` then auto-navigate to the new entity |
| `confirmRecord` | `{ summary, data: { recordingKind, target, moduleId?, testSetId?, caseName?, objectiveId?, verificationName?, waitBeforeSeconds?, deliveryStepIndex?, environmentKey? } }` | Client — dropdown of online agents with matching capability → `POST /api/recordings` → card morphs into a live progress view polling `/api/queue` |
| `confirmCreatePostStep` | `{ summary, data: { moduleId, testSetId, objectiveId, parentKind, parentStepIndex, postStep: VerificationStep } }` | Client — `addPostStep` POSTs through the generic post-step CRUD endpoint. The `postStep` discriminates on its non-null carrier field (`dbCheck`, `eventAssert`, `webUi`, `desktopUi`, `api`, `aseXml`, `aseXmlDeliver`); the card renders a flat summary with the right per-payload fields per kind. |
| `confirmEditPostStep` (REQ-004 §9b) | `{ summary, data: { moduleId, testSetId, objectiveId, parentKind, parentStepIndex, postStepIndex, postStep: VerificationStep } }` | Client — `updatePostStep` PUTs through the same CRUD endpoint with the FULL replacement payload (LLM emits the entire updated `VerificationStep`, not a patch — keeps the runtime simple and the diff visible). Generic across every payload kind, so REQ-002's DB asserts gain NL-edit support for free. |
| `peekServiceBusMessages` (REQ-004 §9a) | `{ summary, data: { envKey?, connectionKey, entity, max?, correlationFilter? } }` | Client — `peekServiceBusMessages` POSTs to the read-only `/api/event-assert/peek` endpoint and renders the result as an expandable per-message panel. Each peeked message exposes "+ Add as criterion" / "+ Add as capture" buttons that fire follow-up `confirmCreatePostStep` (or `confirmEditPostStep`) actions with values pre-filled from the actual message. |

Discovery/read-only intents are resolved server-side during intent parsing (the catalog already has everything), so the action is `showData` with the pre-computed payload. Mutations never auto-execute; every create/run/record/edit requires a card click.

The catalog REQ-004 added two enrichments the LLM relies on for these actions: `serviceBusConnectionsByEnv` (per-env map of configured connection keys; the LLM picks `connectionKey` only from this list) and `parentSteps[*].postSteps` (per-post-step `{ postStepIndex, description, target, role, payload }` summaries; the LLM resolves `postStepIndex` against this list when emitting `confirmEditPostStep`).

### Why the LLM doesn't call the DB/APIs directly

There is intentionally no tool-calling layer. The server pre-computes the full catalog, prompt-injects it, and relies on the LLM to map the user's phrase to catalog entries. This keeps the loop a single round-trip (no follow-ups), bounds LLM authority (it can only emit actions from a small discriminated union), and avoids needing a separate MCP/function-calling path for every repository shape. The catalog is small — tens of KB in typical deployments — and is rebuilt per request, so it never goes stale.

### Scope boundaries

- **Normal-mode is API-only in chat** — the assistant will emit `confirmRun` with `mode=Normal` when the user asks to generate/create an API test (e.g. "generate a test for SDR legacy API `api/v1/...` GET"). The LLM resolves `apiStackKey` + `apiModule` from phrases like "legacy" / "BraveCloud" / "SDR" against the catalog's `apiStacks` and refuses to invent keys. UI / aseXML Normal-mode generation stays out of scope — the prompt explicitly forbids it and routes UI intents to `confirmRecord` instead.
- **Recording is dispatch-only** — the chat doesn't execute recording sessions itself; it enqueues them via `/api/recordings` for a user-selected agent. Running a Runner in `--agent` mode is still required.
- **No tool calling / streaming** — the endpoint returns a single JSON response per turn. Streaming is still deferred; persistence has shipped (see below).

### Conversation persistence

Conversations are persisted in SQLite when the WebApi runs in SQLite + auth mode. The schema lives in `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` (v6 → v7 migration, additive — no `ALTER`s):

```sql
chat_conversations (
    id            TEXT PRIMARY KEY,
    user_id       TEXT NOT NULL,        -- FK users.id (logical; no enforced FK)
    title         TEXT NOT NULL,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    message_count INTEGER NOT NULL DEFAULT 0
)
INDEX (user_id, updated_at DESC)

chat_messages (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL,      -- FK chat_conversations.id (logical)
    role            TEXT NOT NULL,      -- 'user' | 'assistant'
    content         TEXT NOT NULL,
    actions_json    TEXT,               -- serialised List<ChatAction> for assistant turns
    created_at      TEXT NOT NULL
)
INDEX (conversation_id, created_at)
```

**Per-user ring-fencing** is double-enforced:

1. `ApiKeyAuthMiddleware` validates `X-Api-Key` and stores the resolved `User` in `HttpContext.Items["User"]`. Chat endpoints extract it and pass `user.Id` to the repository.
2. `SqliteChatConversationRepository` includes `WHERE user_id = $userId` on every read and write — a stolen conversation id alone cannot read another user's thread. Append/delete additionally re-check ownership inside the same transaction so a race with a concurrent rename can't leak data.

**Retention** — `Chat.MaxConversationsPerUser` (default 20) caps each user's thread count. Creating a new conversation when at cap deletes the oldest (and its messages) in the same transaction. `Chat.MaxMessagesPerConversation` (default 200) bounds prompt history; the DB still keeps every turn for replay.

**Auto-titling** — the first user message in a conversation derives the title (first 60 chars, single-line). Threads created via "+ New chat" start as the placeholder `"New chat"`; `ChatIntentService` rewrites the placeholder on the first message with content. Explicit user renames are preserved (`IsPlaceholderTitle` only matches the defaults).

**Action-card replay** — assistant action cards (`navigate`, `showData`, `confirmRun`, etc.) are serialised into `chat_messages.actions_json` so they re-render after a refresh. Confirm-cards are rendered in their idle state on replay; clicking Execute re-issues the underlying mutation as it would for a fresh card.

**Fallback for file-storage / no-auth mode** — when no `IChatConversationRepository` is registered, `ChatIntentService` falls through to its legacy stateless behaviour (client-resent transcript, no DB writes). The frontend detects this via the `authRequired` flag and hides the thread picker, falling back to an in-memory transcript with a `clear` button.

### File map

| File | Purpose |
|---|---|
| `src/AiTestCrew.WebApi/Endpoints/ChatEndpoints.cs` | `POST /api/chat/message` + conversation CRUD (`GET/POST/DELETE/PATCH /conversations`) — every handler scoped via `HttpContext.Items["User"]` |
| `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` | Catalog build + system prompt + LLM call + response parse + transcript load/persist + auto-title |
| `src/AiTestCrew.WebApi/Models/Chat/ChatModels.cs` | Request/response/action DTOs + `ConversationSummary` / `ConversationDetail` / `PersistedChatMessage` |
| `src/AiTestCrew.WebApi/Endpoints/RecordingEndpoints.cs` | `POST /api/recordings` — thin wrapper that validates + enqueues |
| `src/AiTestCrew.Core/Models/Chat/ChatConversation.cs` + `ChatMessageRecord.cs` | Domain models |
| `src/AiTestCrew.Core/Interfaces/IChatConversationRepository.cs` | Repository contract — every method takes `userId` for ring-fencing |
| `src/AiTestCrew.Storage/Sqlite/SqliteChatConversationRepository.cs` | SQLite implementation; `WHERE user_id = …` on every operation |
| `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` | Schema v6 → v7 migration adds `chat_conversations` + `chat_messages` |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | `ChatConfig` (MaxConversationsPerUser, MaxMessagesPerConversation) |
| `ui/src/contexts/ChatContext.tsx` | API-backed conversation list + active id via React Query; in-memory fallback for file-storage mode |
| `ui/src/components/chat/ChatDrawer.tsx` | Drawer, header thread picker, action-card renderers |
| `ui/src/api/chat.ts` + `ui/src/api/recordings.ts` | API clients (chat now exposes `listConversations` / `createConversation` / `getConversation` / `deleteConversation` / `renameConversation`) |

---

## Startup Data Packs

The WebApi runs version-controlled `.sql` scripts against per-environment Bravo databases at every startup. The user-facing guide is in `docs/data-packs.md`; this section documents the internals and design decisions.

### Pipeline

```
WebApi startup
    ↓
TestEnvironmentConfig.DataPacksPath → resolve against AppContext.BaseDirectory
    ↓
DataPackRegistry.Discover(rootAbsolute, logger)
    pure file walk: /<phase>/<envKey>/<NN.subfolder>/<NN.script>.sql
    sort by leading numeric prefix (regex `^(?<n>\d+)\.\s*(?<rest>.+)$`)
    phase order is fixed: ["datateardown", "datapreparation"]
    ↓ DataPackPlan { Envs: [DataPackEnvPlan { EnvKey, Phases: [DataPackPhasePlan] }] }
    ↓
DataPackRunner.RunAllAsync(ct)
    for each env in plan:
        skip if not in IEnvironmentResolver.ListKeys()              → SkippedNotConfigured (warn)
        skip if !envResolver.ResolveRunDataPacksOnStartup(envKey)   → SkippedOptOut (info)
        skip if envResolver.ResolveBravoDbConnectionString empty    → SkippedNoConnection (info)
        open one SqlConnection
        for each phase, each script:
            File.ReadAllTextAsync → SqlBatchSplitter.Split (on standalone GO lines)
            for each batch: new SqlCommand(batch, conn).ExecuteNonQueryAsync (CommandTimeout=0)
            on failure: mark Failed, abort remaining scripts for THIS env (continue other envs)
    log summary, store DataPackStartupReport on the runner singleton
```

The runner is a `Singleton` registered in `WebApi/Program.cs`. Its `LatestReport` property holds the most recent `DataPackStartupReport` and is exposed via `GET /api/data-packs/startup-report` for the dashboard panel.

### Trust boundary — why no SqlGuardrails

Per-test-set teardown (`BravoTeardownExecutor`) runs LLM-/user-generated SQL through `SqlGuardrails.Validate`, which requires a `WHERE` clause and bans `EXEC`/`CREATE`/`ALTER`/`DROP`/`TRUNCATE`/`MERGE`/`SHUTDOWN`/`GRANT`/`REVOKE`.

Data-pack scripts are dev-authored and version-controlled, so they intentionally bypass the guardrail. They legitimately need:
- `CREATE OR ALTER PROCEDURE` to install/refresh procs
- `EXEC usp_X` to run installed procs from preparation scripts
- Unbounded `DELETE` for full table cleanup before a test run

The guardrail still applies to objective teardown — different trust boundary, different code path.

### Per-env opt-in only

There is **no top-level `RunDataPacksOnStartup`**. A misconfigured new env with a `BravoDbConnectionString` would otherwise silently auto-run scripts; explicit per-env opt-in eliminates that footgun. `DataPacksPath` is top-level (it's a path setting, not a destructive trigger).

### `GO` batch splitter

`SqlBatchSplitter` splits on lines matching `^\s*GO\s*(--.*)?$` (case-insensitive). A `GO` token inside a `'...'` string literal, `--` line comment, or `/* ... */` block comment does not split — the splitter carries `inBlockComment` and `inString` state across line boundaries (T-SQL doubled-quote `''` escape handled inline).

Out of scope: `sqlcmd` features (`:r`, `:setvar`, `GO 5`), multi-line string literals containing a standalone `GO` line.

### Per-batch autocommit

There is no outer transaction around a script. Mirrors `sqlcmd` behaviour and avoids the SQL Server quirks where DDL statements can't run inside a user transaction (`CREATE/ALTER PROCEDURE`, `DBCC` commands). Authors who need atomicity wrap explicitly with `BEGIN TRAN ... COMMIT` inside a single batch.

### Failure policy

Within an env, scripts are interdependent (later scripts may `EXEC` procs the earlier ones installed) — so a failure aborts the env's remaining scripts. Across envs, scripts are independent — so other envs continue. The runner never throws; the WebApi starts and serves requests even with `Failures > 0`. Operators detect failures via the dashboard panel (red ✗ rows) or the structured log lines.

### Packaging

`<Content Include="..\..\data\datapacks\**\*.sql"
        Link="datapacks\%(RecursiveDir)%(Filename)%(Extension)"
        CopyToOutputDirectory="PreserveNewest" />`
in `AiTestCrew.WebApi.csproj` ships the `.sql` files into `bin/datapacks/`. This works for `dotnet build` (local dev) and `dotnet publish` (self-contained). The Dockerfile additionally `COPY`s `data/datapacks/` into the build context (otherwise the `..\..\data\datapacks` path would be missing during the in-container publish).

The Runner project does NOT ship the data folder — only the WebApi runs the runner; adding it to Runner builds would be wasted I/O.

### Files

| File | Purpose |
|---|---|
| `src/AiTestCrew.Core/Interfaces/IDataPackRunner.cs` | Contract — `RunAllAsync` + `LatestReport` |
| `src/AiTestCrew.Core/Models/DataPackStartupReport.cs` | Report DTOs (env + script reports + status enums) |
| `src/AiTestCrew.Core/Models/DataPackRunSummary.cs` | High-level numeric summary (separate from report) |
| `src/AiTestCrew.Agents/DataPack/DataPackRegistry.cs` | Pure file-walk + sort; produces `DataPackPlan` |
| `src/AiTestCrew.Agents/DataPack/DataPackPlan.cs` | Internal plan records (Envs → Phases → Scripts) |
| `src/AiTestCrew.Agents/DataPack/DataPackRunner.cs` | Connection open/close, GO split, batch execute, report build |
| `src/AiTestCrew.Agents/DataPack/SqlBatchSplitter.cs` | State-machine splitter; comment + string aware |
| `src/AiTestCrew.WebApi/Endpoints/DataPackEndpoints.cs` | `GET /api/data-packs/startup-report` |
| `ui/src/components/DataPacksPanel.tsx` | Dashboard panel; auto-refresh 30 s; expandable per-script rows |
| `ui/src/api/dataPacks.ts` | API client |
| `data/datapacks/<phase>/<envKey>/<NN.subfolder>/<NN.script>.sql` | Authored content |

---

## DB Assert Step

A DB Assert is a read-only post-step that runs a single SELECT against a configured SQL Server database and asserts the result — either a row count or a structured set of per-column rules — with optional value capture into the run context. It is post-step-only by design: a database assertion without a preceding write has no signal. The agent (`DbCheckAgent`) documents this constraint explicitly and `TestOrchestrator` does not expose it as a standalone objective type.

### Data model

`DbCheckStepDefinition` (`src/AiTestCrew.Storage/DbAgent/DbCheckStepDefinition.cs`) carries:

- `Sql` — the SELECT to run. `{{Token}}`-substituted before execution.
- `ConnectionKey` — logical DB key (e.g. `"BravoDb"`, `"SdrReportingDb"`). Resolved at execution time via `IEnvironmentResolver.ResolveDbConnectionString`.
- `TimeoutSeconds` — per-query timeout; default 15.
- `ExpectedRowCount` — when non-null, the step passes if the query returns exactly this many rows. Mutually exclusive with `ColumnAssertions`: if both are present, the assertion list takes precedence.
- `ColumnAssertions: List<ColumnAssertion>` — per-column rules evaluated against the **first row** of the result set. Each `ColumnAssertion` carries:
  - `Column` — result-set column name. `{{Token}}`-substituted.
  - `JsonPath` (optional) — JSONPath inside the column's value (e.g. `$.OrderId`, `$.Items[0].Code`). `{{Token}}`-substituted. When set, the column value is parsed as JSON and the path resolved before the operator runs.
  - `Operator` — one of 14 values from `AssertionOperator` (`src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs`): `Equals`, `NotEquals`, `Contains`, `NotContains`, `StartsWith`, `EndsWith`, `Regex`, `GreaterThan`, `LessThan`, `Between`, `IsNull`, `IsNotNull`, `EqualsNumeric`, `EqualsDate`.
  - `Expected` / `Expected2` — expected value(s); `Expected2` is used as the upper bound by `Between`. Both are `{{Token}}`-substituted.
  - `IgnoreCase` — defaults true for string operators; ignored by numeric/date operators.
  - `ToleranceSeconds` / `ToleranceDelta` — tolerance windows for `EqualsDate` and `EqualsNumeric` respectively.
- `Captures: List<ColumnCapture>` — values to bind into the run context after all assertions pass. Each `ColumnCapture` carries:
  - `Column` — result-set column name. `{{Token}}`-substituted.
  - `JsonPath` (optional) — path inside a JSON column. `{{Token}}`-substituted.
  - `As` — bare token name to bind (e.g. `"JobId"`); sibling post-steps reference it as `{{JobId}}`. **Not** `{{Token}}`-substituted — substituting the target name would let parent context silently redirect captures.
  - `Required` — defaults true. When false, a null/missing value leaves the token undefined (literal `{{As}}` survives lenient substitution; a WARN is logged via the existing `unknownTokens` collector).

**Legacy JSON shim.** Persisted test sets saved before REQ-002 may carry `expectedColumnValues: {col: "value", ...}` (flat dict). A `[JsonPropertyName("expectedColumnValues")]` setter on `DbCheckStepDefinition` promotes each entry into a `ColumnAssertions` `Equals` entry on deserialise and never serialises back out — the same pattern `TestObjective` uses for `ApiDefinitionCompat`/`WebUiDefinitionCompat`. Round-tripping the file (load → save) normalises to the new shape. No one-time migration script runs; the shim is the contract.

### Runtime path

`DbCheckAgent.ExecuteAsync` (`src/AiTestCrew.Agents/DbAgent/DbCheckAgent.cs`):

1. Reads `task.Parameters["PreloadedTestCases"]` as `List<DbCheckStepDefinition>` (reuse-mode contract). When the list is empty the step surfaces `TestStatus.Error`.
2. For each definition, resolves the connection string via `IEnvironmentResolver.ResolveDbConnectionString(check.ConnectionKey, envKey)`. An unknown key returns null → `TestStatus.Error` naming the key and env (config issue, not data issue).
3. Opens a fresh `SqlConnection` per definition — each check can target a different logical DB.
4. Validates SQL via `DbCheckSqlGuardrails.Validate` before executing.
5. **Mode 1 — row count**: when `ColumnAssertions` is empty and `ExpectedRowCount` is set. On mismatch, attaches the first row to `TestStep.Metadata["dbCheckRow"]`; if more than one row exists, also attaches the first three rows under `Metadata["dbCheckRows"]`.
6. **Mode 2 — column assertions**: reads raw `object?` + `reader.IsDBNull(ordinal)` per column and passes them to the pure `ColumnAssertionEvaluator.Evaluate`. On any failure, the full first row is attached to `Metadata["dbCheckRow"]` and the step fails with a joined human-readable reason. On all passing, captures are evaluated (see below).
7. If neither mode applies, the step surfaces `TestStatus.Error` ("nothing to assert").

Cell values in `dbCheckRow`/`dbCheckRows` are truncated to 200 characters before storage. The run-detail UI (`StepList.tsx`) extracts these from `step.metadata` and renders them as a `column→value` table under the failure reason ("Failing row" heading).

### JSONPath evaluator

`JsonValueExtractor` (`src/AiTestCrew.Agents/DbAgent/JsonValueExtractor.cs`) wraps `Json.Path.JsonPath` from the `JsonPath.Net` NuGet package (json-everything suite, MIT licence, System.Text.Json-native). It was chosen over a hand-rolled subset because it is spec-aligned, predictable on edge cases (recursive descent, union operators, filters), and carries no extra serialiser dependency.

`JsonValueExtractor.TryExtract` returns a typed status rather than throwing:

| Status | Meaning |
|---|---|
| `Found` | Path resolved to a non-null JSON value. |
| `FoundNull` | Path resolved, but to a JSON `null`. Treated like SQL NULL by `IsNull` / `IsNotNull`. |
| `NotJson` | Column value is not parseable JSON. |
| `InvalidPath` | Path expression is syntactically invalid. |
| `NotFound` | JSON parsed successfully but no node matches the path. |

`ColumnAssertionEvaluator` uses these statuses to produce typed failure reasons (`"column 'X' is not JSON"`, `"JSON path '$.Y' not found in column 'X'"`) rather than a generic exception.

**NULL vs missing path.** Both `FoundNull` (JSON `null` at the path) and `NotFound` (path absent) fail `IsNotNull`, but only `FoundNull` passes `IsNull`. This mirrors SQL NULL semantics and is documented on `AssertionOperator.IsNull`.

### NULL and type fidelity

The evaluator reads raw `object?` + `isDbNull` rather than forcing `.ToString()` upfront:

- `IsNull` / `IsNotNull` check `isDbNull` directly. SQL NULL ≠ empty string — `Equals ""` fails when the column is NULL.
- `EqualsNumeric` parses both sides as `decimal` with `CultureInfo.InvariantCulture` and compares with `ToleranceDelta`. Parse failure → typed failure message.
- `EqualsDate` parses both sides as `DateTimeOffset` with `InvariantCulture` + `DateTimeStyles.AssumeUniversal`; falls back to `DateTime.Parse` if needed. Comparison uses `ToleranceSeconds`. This handles trailing-zero, fractional-second, and UTC-offset differences that break `.ToString()` equality.
- `GreaterThan`, `LessThan`, `Between` try decimal first, then `DateTimeOffset`, so thresholds work on both numeric and date-shaped columns without the author specifying which.
- String operators (`Equals`, `Contains`, `StartsWith`, `EndsWith`, `Regex`) call `Convert.ToString(value, CultureInfo.InvariantCulture)` only at this stage.

### Capture semantics and precedence

Captures run only when the `ColumnAssertions` list passes in its entirety (or is empty). A failing assertion means the row is wrong; a value captured from a wrong row is suspect. This is documented on `ColumnCapture` and enforced in `DbCheckAgent.RunColumnAssertionsModeAsync`.

After the pass, `EvaluateCaptures` walks each `ColumnCapture` entry and calls `JsonValueExtractor.TryExtract` for entries with a `JsonPath`. Captured tokens are stored in `TestStep.Metadata["capturedTokens"]` as `Dictionary<string,string>`.

`PostStepOrchestrator` detects `capturedTokens` on each completed child step and merges them into its working context via `MergeCaptured`:

**Precedence: captured > existing context > env params.**

`MergeCaptured` overwrites an existing key and logs INFO when it does so — the log line names the token and both the old and new values so regressions are visible. The working context dict is local to the post-step chain for one parent step; it does not escape to a sibling parent-level step.

**`Required: false` on a missing capture.** The token is left unbound. Subsequent `{{As}}` references pass through lenient substitution unchanged and are collected by the existing `unknownTokens` WARN log. The step does not fail.

### Deferred path

When any post-step in an objective has `WaitBeforeSeconds` above the deferral threshold, the whole batch is enqueued for a deferred agent. REQ-002 extends `DeferredVerificationRequest` (`src/AiTestCrew.Storage/AseXmlAgent/Delivery/DeferredVerificationRequest.cs`) with `CapturedTokens: Dictionary<string,string>`. This dict carries any tokens an inline sibling (e.g. an inline DB check that ran before the batch was enqueued) has already bound. At claim time, `PostStepOrchestrator.RunDeferredAsync` merges `CapturedTokens` into the working context before the first deferred post-step runs. Precedence is the same: captured > delivery context snapshot.

On a failed deferred attempt that retries, `DeferredVerifyAsync` rolls any tokens that changed during the attempt forward into the new `DeferredVerificationRequest.CapturedTokens` so later retries see progress made by earlier ones.

### Multi-DB resolution

`IEnvironmentResolver.ResolveDbConnectionString(connectionKey, envKey)` (`src/AiTestCrew.Core/Interfaces/IEnvironmentResolver.cs`) resolves in three steps:

1. Per-env `EnvironmentConfig.DbConnections[connectionKey]` — added by REQ-002 to `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs`.
2. Top-level `TestEnvironmentConfig.DbConnections[connectionKey]` fallback — added to `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs`.
3. Legacy back-compat for the `"BravoDb"` key only: falls back to `ResolveBravoDbConnectionString(envKey)` when neither dict defines it. New keys (e.g. `"SdrReportingDb"`) must be in one of the two dicts; they do not get a legacy fallback.

Unknown key → returns `null` → `DbCheckAgent` surfaces `TestStatus.Error` naming the key and env. `IEnvironmentResolver.ListDbConnectionKeys(envKey)` returns the union of per-env keys, top-level keys, and the implicit `"BravoDb"` key; the `GET /api/db-check/connections` endpoint exposes this list to the UI editor's connection dropdown.

`appsettings.example.json` shows both a top-level entry and a per-env override under `Environments.<env>.DbConnections`.

### Security envelope

**SQL guardrails.** `DbCheckSqlGuardrails.Validate` (`src/AiTestCrew.Agents/DbAgent/DbCheckSqlGuardrails.cs`) rejects:
- Any statement that does not start with `SELECT` or `WITH` (CTEs allowed).
- Semicolons (prevents statement chaining).
- A denied-keyword list: `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `TRUNCATE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, `EXECUTE`, `SHUTDOWN`, `GRANT`, `REVOKE`, `INTO`.

CTEs (`WITH x AS (SELECT ...)`) are allowed at the top level; the keyword scan still catches write verbs smuggled inside a CTE (e.g. `WITH x AS (SELECT 1) INSERT INTO y`). The guardrail applies both at runtime (agent) and at dry-run time (endpoint) — read-only intent is enforced at both call sites.

**Dry-run endpoint.** `POST /api/db-check/dry-run` (`src/AiTestCrew.WebApi/Endpoints/DbCheckEndpoints.cs`) runs a user-supplied SELECT against the configured DB and returns columns + first 5 rows + total row count. It is gated two ways:

- **Per-env opt-in.** `EnvironmentConfig.AllowDbDryRun` (default `true`). Set to `false` on production-style envs to disable the "Try query" UI button while still allowing scheduled DB-check post-steps to run as normal. Resolved via `IEnvironmentResolver.ResolveAllowDbDryRun`; a disabled env returns HTTP 403.
- **Rate limit.** `DbDryRunRateLimiter` (`src/AiTestCrew.WebApi/Services/DbDryRunRateLimiter.cs`) — an in-memory fixed-window token bucket, 10 requests/minute/user (keyed by `User.Id` from `HttpContext.Items["User"]`, or by remote IP when auth is disabled). The 11th request in a window returns HTTP 429. `AgentHeartbeatMonitor` runs a `Sweep(nowUtc)` on a 5-minute cadence (a sub-tick of its 30 s base tick) to drop expired buckets and bound memory growth.

Cell values in the dry-run response are truncated to 500 characters; the SQL Server type name is included per column so the UI can render hints for JSON-typed columns.

---

## Event Assertion Step (Azure Service Bus)

An Event Assert is a post-step that opens a receiver against an Azure Service Bus queue or topic+subscription, evaluates per-message criteria, and resolves a verdict via a configurable match-mode. It is post-step-only by design — the same shape rule REQ-002 locked in for `DbCheckAgent`: an event assertion without a preceding action that should have caused the event has no signal. The agent (`AzureServiceBusEventAgent`) documents this constraint and `TestOrchestrator` does not expose it as a standalone objective type.

REQ-004 piggybacks aggressively on REQ-002's plumbing: the `VerificationStep` carrier, the operator surface (`AssertionOperator`), `ScalarOperatorEvaluator` (lifted from REQ-002's `ColumnAssertionEvaluator`), `PostStepOrchestrator`'s capture-token merge, and `DeferredVerificationRequest.CapturedTokens`. The capture semantics + deferred path are identical — see DB Assert above. This section focuses on what's specific to Service Bus: the data model, body-format dispatch, settlement, the pre-parent drain hook, and connection resolution.

### Data model

`EventAssertStepDefinition` (`src/AiTestCrew.Storage/EventAssertAgent/EventAssertStepDefinition.cs`) carries:

- `ConnectionKey` — logical Service Bus namespace key (e.g. `"DefaultBus"`, `"MeterEvents"`). Resolved at execution time via `IEnvironmentResolver.ResolveServiceBusConnection`.
- `Entity: ServiceBusEntity` — `Type` (Queue or Topic), `Name`, optional `SubscriptionName` (required for Topic). Both `Name` and `SubscriptionName` are `{{Token}}`-substituted.
- `BodyFormat` — `Auto` (default) / `Json` / `Xml` / `Text` / `Binary`. `Auto` sniffs `ContentType` first (case-insensitive contains check on `json`/`xml`/`octet-stream`), then falls back to the first non-whitespace byte.
- `ReceiveMode` — `PeekLock` (default, safe for shared subs) or `ReceiveAndDelete` (destructive — used by the drain hook).
- `MatchMode` — `AnyMessage` (default) / `AllMessages` / `ExactlyOne` / `ExactCount` / `MinCount` / `MaxCount` / `CountRange`. Folds the per-message pass/fail vector into a final verdict.
- `ExpectedCount` / `MaxCount` — count thresholds for count-bound modes. `MaxCount(0)` is the negative-assertion shape ("verify NO matching event was raised"); the receive loop runs the FULL timeout to actually verify zero arrived.
- `TimeoutSeconds` (default 30) — total receive window after the parent step completes.
- `MaxMessages` (default 50) — hard cap on messages drained for evaluation; protects against runaway loops on busy queues.
- `DrainBeforeParent` (default false) — when true, the orchestrator drains the entity in `ReceiveAndDelete` mode BEFORE the parent step runs (see "Pre-Parent Drain Hook" below).
- `CompleteOnPass` (default true) — on `PeekLock` + green: complete passing messages, abandon failing ones. When false, abandon all (debug mode — leaves messages in place).
- `CorrelationFilter` (optional) — pre-filter on `CorrelationId`; messages whose CorrelationId doesn't equal this value (after token substitution) are skipped without evaluating criteria. Useful for narrowing a busy shared queue down to a specific test run.
- `SessionId` (optional) — for session-aware receivers; single-session only in v1.
- `Criteria: List<EventCriterion>` — per-message rules. Each entry carries `Field`, `Operator`, `Expected`, optional `Expected2`, `IgnoreCase`, `ToleranceSeconds`, `ToleranceDelta`. The operator surface is REQ-002's `AssertionOperator` verbatim.
- `Captures: List<EventCapture>` — values to bind into the run context after the verdict resolves to pass. `Field` is the same path syntax as criteria; `As` is the bare token name (NOT substituted); `Required` defaults true.

**Field path syntax** (used by both `EventCriterion.Field` and `EventCapture.Field`):

| Prefix | Meaning | Example |
|---|---|---|
| System property | `MessageId`, `CorrelationId`, `Subject`, `ContentType`, `ReplyTo`, `To`, `SessionId`, `EnqueuedTimeUtc`, `DeliveryCount`, `PartitionKey` | `MessageId` |
| Application property | `ApplicationProperties.<name>` | `ApplicationProperties.EventType` |
| JSON body | `Body.<jsonpath>` (when `BodyFormat` resolves to `Json`) | `Body.Order.Id`, `Body.Items[0].Sku` |
| XML body | `BodyXml.<xpath>` (when `BodyFormat` resolves to `Xml`) | `BodyXml.//Order/@Id` |
| Raw body | `BodyText` | `BodyText` |
| Body length | `BodyLength` | `BodyLength` |

Resolution is uniform — `MessageFieldResolver.Resolve(message, fieldPath, effectiveBodyFormat)` returns a tri-state `ExtractResult(Status, Value, Error)` where `Status` is `Found` / `FoundNull` / `Failed`. `Failed` carries a typed reason (`"binary body — only system / application properties and BodyLength are matchable"`, `"Body.* requires JSON body format; resolved format is Xml"`, etc.). The field label is propagated into operator-failure messages so a failing criterion surfaces "Body.MeterId: expected '12345', got '67890'" rather than a generic column reference.

### Runtime path

`AzureServiceBusEventAgent.ExecuteAsync` (`src/AiTestCrew.Agents/EventAssertAgent/AzureServiceBusEventAgent.cs`):

1. Reads `task.Parameters["PreloadedTestCases"]` as `List<EventAssertStepDefinition>` (set up by `PostStepOrchestrator.TryPreloadPayload` from the post-step's `EventAssert` carrier).
2. For each definition, resolves the connection via `IEnvironmentResolver.ResolveServiceBusConnection`; an unknown key returns null → `TestStatus.Error` (config issue, not data).
3. Validates the entity (non-empty `Name`; `SubscriptionName` required for Topic).
4. Opens a receiver via `IServiceBusReceiverFactory.OpenAsync` (passing the configured `ReceiveMode` and optional session id) — see "Receiver factory" below.
5. **Receive loop**: pulls in batches of size `MaxMessages - allMessages.Count` with a 1s per-call timeout. Each batch's messages are filtered by `CorrelationFilter` (skipped messages are abandoned in PeekLock mode so other consumers can pick them up; not counted against `MaxMessages`). Each retained message is evaluated against every criterion via `MessageFieldResolver` + `ScalarOperatorEvaluator`, then added to `perMessageResults`. The loop exits when `MaxMessages` is reached, `TimeoutSeconds` elapses, or `MatchModeEvaluator.CanShortCircuit(...)` returns true.
6. **Verdict**: `MatchModeEvaluator.Evaluate(matchMode, totalReceived, passCount, expectedCount, maxCount)` returns `(Passed, Reason)`.
7. **Captures** (only on green): the FIRST passing message's values bind into `Metadata["capturedTokens"]` as `Dictionary<string,string>` — same shape REQ-002 uses, so `PostStepOrchestrator.MergeCaptured` plumbs them into siblings unchanged.
8. **Settlement** (PeekLock only): when the verdict is green AND `CompleteOnPass=true`, every passing message is completed and every failing message is abandoned (so non-matching production traffic flows back). On `CompleteOnPass=false` OR red, every message is abandoned. Errors during settlement are swallowed — the receiver's `DisposeAsync` will abandon any remaining locks.
9. **Diagnostics**: `Metadata["serviceBusReceived"]` is populated with up to 10 received-message summaries (`messageId`, `correlationId`, `contentType`, `enqueuedTimeUtc`, `applicationProperties`, truncated `bodyPreview`, `bodyFormat`, `bodyLength`, per-message `passed`, per-criterion `(field, op, passed, reason)`). The run-detail UI (`StepList.tsx`) extracts this via `extractEventAssertDiagnostics` and renders it as an expandable per-message panel under the failure reason.

`MatchModeEvaluator.CanShortCircuit` is deliberately conservative — `MaxCount(0)` "still at zero" does NOT short-circuit, because the negative-assertion shape demands the full timeout to actually verify zero arrived. `AnyMessage` short-circuits on the first pass; `AllMessages` on the first failure; `ExactlyOne` only on overshoot (≥ 2); count-bounded modes when the upper bound is exceeded.

### Body-format dispatch

`BodyFormatDetector.Resolve(configured, contentType, body)` runs once per message before any criterion / capture references the body. Configured non-`Auto` values are honoured verbatim (the user knows their producer better than we do). `Auto` sniffs:

1. `ContentType` first — case-insensitive contains check on `json`, `xml`, `octet-stream`.
2. First non-whitespace byte — `{`/`[` → JSON, `<` → XML, else Text.
3. Empty body → Text (so subsequent `Body.*` paths fail with a typed reason at extraction time).

JSON extraction reuses REQ-002's `JsonValueExtractor` (`JsonPath.Net`); XML extraction is `System.Xml.XPath`-based with DTD processing disabled. Default-namespace handling in v1 expects users to wrap prefixed paths in `local-name()` filters (`//*[local-name()='Order']/@Id`); a first-class namespace registry is flagged as a future extension. Binary bodies fail every `Body.*` / `BodyXml.*` / `BodyText` path with a typed reason, but `BodyLength` always works.

### Pre-Parent Drain Hook

Some event-assert post-steps need the queue / subscription drained of stale messages BEFORE the parent step runs — otherwise leftovers from a prior failed run would contaminate `ExactlyOne` / `MaxCount` verdicts. REQ-002's `PostStepOrchestrator` has no pre-parent slot (post-steps run strictly after the parent), so REQ-004 adds one:

- `PostStepOrchestrator.HasDrainBeforeParent(postSteps)` — cheap pre-check returning true when any post-step has `EventAssert.DrainBeforeParent=true`. Parent agents can avoid the orchestrator dispatch entirely on the common path.
- `PostStepOrchestrator.RunPreParentDrainsAsync(postSteps, context, environmentKey, ct)` — iterates the post-step list, applies env-token substitution to the entity name + subscription, and dispatches `AzureServiceBusEventAgent.DrainAsync` for each `DrainBeforeParent=true` entry. Drain runs in `ReceiveAndDelete` mode with a 2s idle window OR a 10s hard ceiling, whichever comes first. Strict-mode contract: an unhandled drain failure throws — running the parent against a half-drained entity yields misleading verdicts.
- `BaseTestAgent.TryPreParentDrainsAsync(postSteps, tcIndex, stepSink, envKey, envParams, ct)` — the wrapper each parent agent calls. Builds the env-params context, swallows drain failures into a synthesised `pre-parent-drain[idx]` Error step on the parent's step list, returns `false` so the caller can `continue;`-skip that test case.

The hook touches every parent agent that owns a post-step list — `ApiTestAgent`, `BaseWebUiTestAgent`, `BaseDesktopUiTestAgent`, `AseXmlGenerationAgent`, `AseXmlDeliveryAgent`. Each calls `TryPreParentDrainsAsync` immediately after env-substitution and before the parent action; the call is a no-op (cheap) when no post-step requested a drain. `AseXmlDeliveryAgent` calls it at the very top of `DeliverOneAsync` so the drain runs before render + endpoint resolution + upload (the parent's "action" is the delivery, not just the upload step).

### Connection resolution and auth modes

`IEnvironmentResolver.ResolveServiceBusConnection(connectionKey, envKey)` mirrors `ResolveDbConnectionString`'s precedence: per-env `EnvironmentConfig.ServiceBusConnections[connectionKey]` → top-level `TestEnvironmentConfig.ServiceBusConnections[connectionKey]` → null. Whitespace-only / blank-namespace entries fall through to the next tier rather than masking it.

`ServiceBusConnectionConfig` (`src/AiTestCrew.Core/Configuration/ServiceBusConnectionConfig.cs`) carries:

- `AuthMode` — `ConnectionString` (default) or `AzureAd`.
- `ConnectionString` — required when `AuthMode=ConnectionString`. Standard SAS connection string.
- `FullyQualifiedNamespace` — required when `AuthMode=AzureAd` (e.g. `"my-namespace.servicebus.windows.net"`).
- `ManagedIdentityClientId` (optional) — pin a user-assigned managed identity for `DefaultAzureCredential`.

`ServiceBusReceiverFactory` (`src/AiTestCrew.Agents/EventAssertAgent/ServiceBusReceiverFactory.cs`) is the only Azure SDK consumer. It caches one `ServiceBusClient` per `(namespace, authMode, MI client id)` tuple — keyed via `BuildCacheKey` to keep `ConnectionString` instances sharing identity even when wrapped in different DTOs. Clients are `IAsyncDisposable` and disposed when the factory itself is disposed (driven by host shutdown). For `AzureAd` mode, `BuildAzureAdClient` constructs `DefaultAzureCredential` (Azure CLI locally → managed identity in prod with no code change); the optional `ManagedIdentityClientId` is set on `DefaultAzureCredentialOptions` when present.

### Receiver factory abstraction

`IServiceBusReceiverFactory` is the testability gate. The Azure SDK's `ServiceBusReceiver` / `ServiceBusReceivedMessage` / `ServiceBusSessionReceiver` are sealed and notoriously hard to fake; without an abstraction, the only path to unit-testing the agent is integration against a real namespace.

The interface exposes `OpenAsync` returning an `IServiceBusReceiverHandle` with four methods: `ReceiveBatchAsync`, `PeekBatchAsync`, `CompleteAsync`, `AbandonAsync`. Messages are projected through `ReceivedMessageView` (`src/AiTestCrew.Agents/EventAssertAgent/ReceivedMessageView.cs`) — a plain POCO carrying every system + application property + body bytes. `RawMessage` is an opaque `object?` slot used by the Azure-backed implementation to remember the SDK-side reference for settlement; it's null for fakes and for peek-mode messages (which can't be settled).

The fake (`tests/AiTestCrew.Agents.Tests/EventAssertAgent/FakeServiceBusReceiverFactory.cs`) keeps a programmable queue and records Complete/Abandon settlement calls for assertion. The Azure-backed and fake implementations are interchangeable from the agent's perspective — same handle interface, same projection.

### Capture round-trip

Captures emit into `TestStep.Metadata["capturedTokens"]` exactly as REQ-002 does, so `PostStepOrchestrator.MergeCaptured` and `DeferredVerificationRequest.CapturedTokens` plumb them into siblings — inline OR deferred — unchanged. There is no event-assert-specific code in the orchestrator's capture path; only `TryPreloadPayload`'s switch had to grow a new `Event_AzureServiceBus` case (mirrors `Db_SqlServer`).

Acceptance criteria #6 (inline) and #7 (deferred) are both proven: #6 by `tests/AiTestCrew.Agents.Tests/PostSteps/EventCaptureRoundTripTests.cs` (two event-assert post-steps, second references `{{MessageId}}` from the first's capture), #7 by `DeferredVerificationRequest.CapturedTokens` being shape-agnostic (it round-trips a `Dictionary<string,string>` through JSON, regardless of which agent populated it).

### Security envelope

There is **no SQL-style guardrail equivalent** for Service Bus. The peek endpoint physically can't drain (it uses the SDK's `PeekMessagesAsync` which never locks or consumes), and the agent's settlement is gated behind `CompleteOnPass`. Document explicitly: there is no read-vs-write check to apply.

The two surfaces that DO need gating:

- **Peek endpoint.** `POST /api/event-assert/peek` (`src/AiTestCrew.WebApi/Endpoints/EventAssertEndpoints.cs`) is gated three ways — auth (`HttpContext.Items["User"]` populated), per-env opt-in (`EnvironmentConfig.AllowEventAssertPeek`, default true), and a per-user rate limit (`EventAssertPeekRateLimiter`, 10 requests/minute/user, swept by `AgentHeartbeatMonitor` on the same 5-minute cadence as `DbDryRunRateLimiter`). The endpoint always passes `ReceiveMode.PeekLock` to the SDK and uses `PeekBatchAsync` rather than `ReceiveMessagesAsync`, so it cannot drain even by accident. Body previews are truncated to 2 KB; format is auto-sniffed.
- **Drain hook.** Drain runs in `ReceiveAndDelete` mode, which IS destructive — the messages are gone. Mitigation is opt-in per step (`DrainBeforeParent: false` by default) and the editor's "Drain before parent" checkbox carries an explanatory tooltip. Operator culture is the gate: don't enable drain on a queue that carries production traffic.

`appsettings.example.json` shows both a top-level entry (connection-string auth) and a per-env override (Azure AD auth on the `sumo-retail` env).

### Distributed execution

Capability routing is purely string-based — see [Capability strings](#capability-strings) above. `Event_AzureServiceBus` is the new `TestTargetType` value the agent advertises via `CanHandleAsync`. A remote agent advertising it via `--capabilities` picks up deferred event-assert post-steps from the run queue, executes them, and the captured tokens round-trip back to the originating run via the existing `DeferredVerificationRequest.CapturedTokens` plumbing. Two scenarios drive this dispatch in practice: (1) the local runner has no outbound network reach to the customer's Service Bus namespace, (2) the env's Service Bus uses Azure AD auth backed by a managed identity that lives only on a centralised agent host.

---

## API Step (Captures & Assertions)

REQ-007 brings the REST API step to feature parity with DB Assert (REQ-002) and Event Assert (REQ-004): structured operator-driven assertions on the HTTP response and typed value captures into the run context.

### Data model

`ApiTestDefinition` (`src/AiTestCrew.Storage/ApiAgent/ApiTestDefinition.cs`) now carries:

- **`ApiAssertions: List<ApiAssertion>`** — operator-driven assertions on the response. Each assertion has:
  - `Source` — `ApiAssertionSource` enum: `Status` (HTTP status code), `Header` (named header value), `Body` (JSONPath into parsed JSON body), `BodyText` (raw body string).
  - `HeaderName` (used when `Source = Header`).
  - `JsonPath` (used when `Source = Body`) — e.g. `$.data.id`, `$.items[0].status`. `{{Token}}`-substituted before evaluation.
  - `Operator` — reuses the shared `AssertionOperator` enum (same 14 operators as DB Assert and Event Assert).
  - `Expected`, `Expected2` (for `Between`), `IgnoreCase`, `ToleranceSeconds`, `ToleranceDelta` — same semantics as `ColumnAssertion`.
- **`Captures: List<ApiCapture>`** — each capture binds a scalar from the response to a `{{Token}}` name. Same source enum as assertions. `As` (the token name) is NOT `{{Token}}`-substituted. `Required: true` fails the step when the path is absent.
- **Legacy back-compat.** The legacy `ExpectedStatus` / `ExpectedBodyContains` / `ExpectedBodyNotContains` fields are preserved. `NormaliseLegacyFields()` (called by the agent at load time) promotes non-default legacy values into typed `ApiAssertions` entries — idempotent, one-way. Existing test sets produce identical pass/fail results after loading.

`ApiTestCase` mirrors these fields and includes a `ToDefinition()` helper. `StepParameterSubstituter.Apply(ApiTestDefinition)` substitutes `{{Tokens}}` in assertion `JsonPath`, `HeaderName`, `Expected`, `Expected2`, and capture `JsonPath` / `HeaderName`; it does NOT substitute `Captures[*].As` (token names are authored identifiers, not template values).

### Assertion evaluation

`ApiAssertionEvaluator.Evaluate` (`src/AiTestCrew.Agents/ApiAgent/ApiAssertionEvaluator.cs`) is a pure static evaluator:

1. Resolves the source value: status code string, header value (case-insensitive lookup), JSONPath extraction via `JsonValueExtractor`, or raw body string.
2. Delegates operator evaluation to `ScalarOperatorEvaluator` — the same evaluator used by `ColumnAssertionEvaluator` (REQ-002) and `EventCriterionEvaluator` (REQ-004). No operator logic is duplicated.
3. Returns `EvaluateResult(Passed, Reason, Actual)`. Never throws — source/path failures surface as `Passed = false` with a typed reason.

**JSONPath edge cases:**
- `$.missing` → fail with `"JSON path '$.missing' not found in response body"`.
- Non-JSON body + `Source = Body` → fail with `"response body is not JSON"`.
- `Source = Body` + `Operator = IsNull` → only JSON `null` (not missing path) passes.

### LLM validation precedence

- `ApiAssertions.Count > 0` and all pass → `TestStatus.Passed`. LLM validation is **skipped** (cost saving).
- `ApiAssertions.Count > 0` and any fail → `TestStatus.Failed`. LLM validation is **skipped**. Structured failures are authoritative.
- `ApiAssertions.Count == 0` and legacy fields are defaults → **LLM hybrid validation runs** (Normal-mode behaviour preserved).
- `ApiAssertions.Count == 0` but legacy fields are non-default → shim should have promoted them; a defensive `LogWarning` fires on this branch to catch regressions.

### Capture semantics

After a successful HTTP call, `ApiTestAgent.RunCaptures` extracts each capture from status / headers / JSONPath / body text and merges the resulting `Dictionary<string, string>` into the per-objective post-step run context via `BaseTestAgent.RunPostStepsAsync(capturedTokens:)`. Sibling post-steps see captured values as `{{TokenName}}` via `StepParameterSubstituter`. Captures are also threaded through `DeferredVerificationRequest.CapturedTokens` for the deferred path.

### Automatic context tokens

`ApiTestAgent.BuildPostStepContext` publishes:

- `{{ResponseStatus}}` — HTTP status code as string.
- `{{ResponseBody}}` — raw response body, truncated to `DefaultPostStepBodyTruncationBytes` (16 384 bytes). Truncation applies to the context token only; captures and assertions see the full body.
- `{{ResponseHeader.<name>}}` — per response header, lower-case key (e.g. `{{ResponseHeader.content-type}}`).

These are automatic context tokens for immediate post-step use, separate from explicit `Captures`.

### Diagnostic metadata

When `ApiAssertions` are evaluated, the step populates:

- `TestStep.Metadata["apiAssertions"]` — `List<ApiAssertionResult>`: per-assertion `Source`, `Operator`, `Expected`, `Actual`, `Passed`, `Reason`. The run-detail UI can render this as an assertion table.
- `TestStep.Metadata["apiResponse"]` — `ApiResponseSnapshot`: status code, response headers (first 50), body (first 4 096 bytes).

The single-line `TestStep.Summary` stays human-readable: `"All 3 API assertion(s) passed"` or `"2 of 3 API assertions failed"`.

### Dry-run endpoint

`POST /api/api-step/dry-run` (`src/AiTestCrew.WebApi/Endpoints/ApiStepEndpoints.cs`):

- Resolves URL via `IApiTargetResolver.ResolveApiBaseUrl(stackKey, moduleKey, envKey)` and injects auth via the per-(env,stack) `ITokenProvider`.
- Applies `TokenSubstituter` to the endpoint + headers + query params + body using the `parameters` dict from the request.
- `HttpClient.Timeout = 10s` (shorter than the runtime default — exploratory call, not a test run).
- Truncates the response body to 32 768 bytes in the returned payload.
- **Rate limit:** reuses `DbDryRunRateLimiter` (10 / minute per user — same bucket as the DB dry-run; returns 429 above).
- **Per-env gate:** `Environments.<key>.AllowApiDryRun` (default `true`). Returns 403 when disabled.
- **Safety note:** unlike the DB dry-run (SQL guardrails enforce read-only), the API dry-run sends the request as-is — including write methods (POST/PUT/DELETE). The UI displays a warning banner when `Method != GET`. The per-env `AllowApiDryRun` flag is the production safety surface; no method allowlist is enforced server-side.

### Editor surface

`EditTestCaseDialog.tsx` gains:

- Assertions table — source dropdown, conditional `HeaderName` / `JsonPath` inputs, operator dropdown, `Expected` / `Expected2`, `IgnoreCase`. Add / remove rows.
- Captures table — source dropdown, conditional inputs, `As` token name, `Required` checkbox. Add / remove rows.
- "Try Call" button — invokes the dry-run endpoint and renders status + headers + body.
- Write-method warning banner (POST/PUT/PATCH/DELETE).

`PostStepsPanel.tsx` renders an `ApiPostStepBlock` for expanded API post-steps, showing assertion pills and capture pills.

### NL authoring

`ChatIntentService.cs` teaches the LLM the new `api` post-step shape: `apiAssertions` array, `captures` array, and authoring rules (prefer `Body + JsonPath` over `BodyText` substring; always emit captures for returned IDs; structured assertions bypass LLM validation). The `/add-api-step` skill (`.claude/commands/add-api-step.md`) mirrors `/add-db-assert` — NL → dry-run → PUT workflow with step-by-step prereq checks.


## Deployment

### Files

| File | Purpose |
|---|---|
| `Dockerfile` | Windows container image (multi-stage build) |
| `docker-compose.yml` | Single-service compose with env var passthrough |
| `.env.example` | Template for required environment variables |
| `publish.ps1` | PowerShell script for self-contained publish |
| `.dockerignore` | Excludes bin/, obj/, node_modules/, logs/, etc. |

### Frontend co-hosting

WebApi serves the built React SPA from `wwwroot/` for single-binary deployment:

```
UseDefaultFiles()              → maps / to index.html
UseStaticFiles()               → serves JS/CSS/assets from wwwroot/
MapFallbackToFile("index.html") → client-side routing fallback
```

The frontend build (`npm run build` in `ui/`) outputs to `wwwroot/`. The `publish.ps1` script builds both the .NET backend and the React frontend into a single deployable folder.

During development, Vite's dev server (`npm run dev`) proxies `/api` requests to `http://localhost:5050`.

---

## Extension map — where to reach when extending

This section complements the "Where to extend" table in `CLAUDE.md` with architectural context. Each row describes one extension axis and where the seams live.

### Adding a new aseXML transaction type

- **No code changes.** Drop a new `{templateId}.xml` + `{templateId}.manifest.json` pair under `templates/asexml/{TransactionType}/`.
- Both projects' `.csproj` files wildcard-copy the templates folder into their `bin/templates/asexml/` output.
- `TemplateRegistry.LoadFrom` scans at startup and discovers the pair.
- The same template serves both `AseXmlGenerationAgent` and `AseXmlDeliveryAgent` — there is no delivery-specific template.
- Use `/add-asexml-template` to scaffold and smoke-test.

### Adding a new auto-field generator

- `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs` (or `src/AiTestCrew.Storage/AseXmlAgent/` if the generator is persistence-related) — add a new static method.
- `Generate(FieldSpec spec)` — add a `switch` arm dispatching to the new method (case-insensitive match on `spec.Generator`).
- Document the generator name + pattern syntax in the manifest schema docs.
- Keep the set small: only add when a real manifest needs it.

### Adding a new delivery protocol (AS2, HTTP POST, SMB, etc.)

- Implement `IXmlDropTarget` — a new class under `src/AiTestCrew.Agents/AseXmlAgent/Delivery/*DropTarget.cs`.
- `DropTargetFactory.Create(endpoint)` — add a `switch` arm matching the new scheme prefix from `OutBoxUrl` (e.g. `as2://`).
- If the new protocol needs config beyond the `BravoEndpoint` fields, extend `AseXmlConfig` and thread it through the factory constructor.
- No changes to the delivery agent, resolver, or orchestrator.
- Use `/add-delivery-protocol` to scaffold.

### Adding a new UI verification target surface

- Register a new `ITestAgent` that handles the new `TestTargetType`.
- `VerificationStep.Target` already accepts any `TestTargetType` string — the delivery agent routes via `CanHandleAsync` dispatch, so no change to `AseXmlDeliveryAgent` is needed.
- Add a new step-definition shape if the existing `WebUiTestDefinition` / `DesktopUiTestDefinition` don't fit.
- `AseXmlDeliveryAgent.RunVerificationAsync` has a `if (target is UI_Web_*) / else if (UI_Desktop_*)` branch — extend it with the new branch to wrap the step list as `PreloadedTestCases`.
- Recorder — optional; without a recorder the user supplies steps via the UI edit dialog (also optional for a new surface).

### Tuning / extending deferred verification

- **Tuning retry cadence or deadline for a specific environment**: edit `TestEnvironment.AseXml` in `appsettings.json` — `VerificationEarlyStartFraction`, `VerificationRetryIntervalSeconds`, `VerificationGraceSeconds`. No code change. Hard ceiling = set `VerificationGraceSeconds = 0`.
- **Disabling deferred behaviour for a specific run**: `--no-defer-verifications` on the CLI, OR flip `AseXml.DeferVerifications = false` globally.
- **Observing state**: query `run_pending_verifications` and `run_queue` directly on the WebApi's DB. The `attempt_log_json` column on a pending row is the append-only history of every retry attempt, useful for post-mortem on an intermittently flaky verification.
- **Adding a new deferred-verification step field** (e.g. per-verification grace override): extend `VerificationStep.cs` + persist, thread through `DeferredVerificationRequest.Verifications[]`, and consume in `AseXmlDeliveryAgent.TryEnqueueDeferredVerifications`. The Runner's `ApiClientPendingVerificationRepository` and `ApiClientRunQueueRepository` don't need changes — the field rides in the opaque `request_json`.
- **Changing the retry decision policy** (e.g. cap attempts by count, not time): modify `AseXmlDeliveryAgent.DeferredVerifyAsync` retry branch. All decisions are co-located in that method — the server-side endpoints are data proxies, they don't enforce policy.
- **Adding a new surface that also wants deferred execution** (e.g. background job polling): the pattern is reusable — give your agent access to `IRunQueueRepository` + `IPendingVerificationRepository` (DI already wired in both Sqlite and remote modes), build a JSON payload carrying a `"kind"` discriminator, enqueue with a future `not_before_at` and a `parent_run_id`, and add a matching branch in `JobExecutor.TryParseDeferredRequest`.
- **Common symptom: "Awaiting" forever** — usually means no agent has a matching capability or the janitor hasn't run yet. Check the `agents` table for online agents with the verification's target type; check `run_pending_verifications` + `run_queue` rows for the parent run.

### Tuning seamless authentication recovery

- **Tuning what counts as a login redirect**: `Auth.LoginRedirectUrlPatterns` in `appsettings.json`. Add custom IDP hostnames or login paths. Case-insensitive substring match.
- **Disabling silent auto-recovery** (e.g. for diagnosing why a session is being bumped): `Auth.AutoRecoverApi = false` and/or `Auth.AutoRecoverUi = false`. The detection still runs and `AuthRequiredException` still throws — only the in-process invalidate-and-retry is suppressed.
- **Headless CI mode**: `Auth.PauseOnAuthFailure = false` suppresses `AuthRequiredException` entirely so failures fall back to a normal `Failed` step. Don't remove this knob — CI needs it.
- **Tuning the pre-flight warning window**: `Auth.ExpiryWarningHours` (default 1). Bumping this to 4 makes the auth-health panel fire `ExpiringSoon` 4 hours before the file's TTL elapses.
- **Hiding an env from the pre-flight panel**: `EnvironmentConfig.AuthHealthEnabled = false` under that env's block. The agent scanner stops emitting rows for the env on its next heartbeat, and the endpoint filters it server-side too.
- **Common symptom: "Refresh in progress" forever** — the auth-refresh row is `InProgress` but the agent never called `/complete`. Confirm `AuthSetupRequest.AuthRefreshId` is being threaded through the queue payload (the `/start` endpoint must serialise `target` + `authRefreshId`, the agent must call `_authRefreshRepo.MarkCompletedAsync` after `RecordingService.AuthSetupAsync`). The janitor will time it out as `Failed` after `AuthRefreshMaxLatencySeconds` regardless.
- **Common symptom: Blazor refresh saves an empty cookie file** — the `sawSsoRedirect` guard in `RecordingService.AuthSetupAsync` requires the URL to visit `login.microsoftonline.com` before saving; if your customer URL doesn't trigger Azure SSO at the root path you'll hit the 3-minute timeout. Adjust `BraveCloudUiUrl` to a path that requires auth, or extend the guard with an alternative positive signal.
- **Common symptom: API tests fail with "Authentication failed: SEC API rejected the configured credentials"** — the configured `AuthUsername` / `AuthPassword` (top-level or per-env) are wrong, rotated, or the account is locked. This is a `LoginFailedException`, **not** an `AuthRequiredException` — the dashboard refresh banner deliberately doesn't fire because it can't fix `appsettings.json`. Edit the creds (and / or unlock the account), restart the WebApi, re-run.

### Adding a richer wait strategy for verifications

- Extend `VerificationStep` with an optional `WaitStrategy` object (polymorphic or tagged-union).
- `AseXmlDeliveryAgent.RunVerificationAsync` already calls `Task.Delay(WaitBeforeSeconds)` — replace that with a strategy dispatch:
  - `"delay"` — current behaviour (fixed delay).
  - `"sftp-pickup"` — poll the remote path for file disappearance via the same `IXmlDropTarget`'s underlying client.
  - `"db-status"` — poll a Bravo DB table for a status transition (requires schema discovery + a new query in `BravoEndpointResolver` or a new `IWaitStrategyResolver`).
- Keep `WaitBeforeSeconds` as a fallback / minimum wait to avoid hammering on polling loops.

### Adding a new customer environment

- Add a new entry under `TestEnvironment.Environments.<key>` in `appsettings.json` with the customer's URLs, credentials, Bravo DB connection string, and per-stack BaseUrl overrides. Every field is optional — omitted fields fall back to the top-level flat fields.
- Run `--list-environments` to confirm the new env appears, then `--auth-setup --environment <key> --target UI_Web_Blazor` (and `UI_Web_MVC` if using legacy auth) to populate that env's cached auth-state file.
- No code change. The resolver, CLI, WebApi, and UI all enumerate environments from config at startup / request time.
- **Widening existing tests**: open each objective in the UI, tick the new env under "Allowed environments", and supply any per-env `{{Token}}` values that differ. Step definitions already using `{{Tokens}}` automatically pick up the new env's values; steps with literal values need a pass of manual tokenisation.

### Adding a new per-environment setting

- Example: say a customer-specific reporting service URL.
- Add the field to `EnvironmentConfig` (and optionally to the top-level `TestEnvironmentConfig` as a legacy fallback default).
- Add a `ResolveXxx(envKey)` method to `IEnvironmentResolver` + `EnvironmentResolver` that returns the env value (or falls back to the top-level field if null/empty).
- Inject `IEnvironmentResolver` into whatever agent/service consumes the setting and call `ResolveXxx(CurrentEnvironmentKey)` at execution time.
- No persistence schema change — `EnvironmentConfig` is a plain POCO; new nullable fields deserialize as `null` on older configs.

### Authoring or extending startup data packs

- **Adding a script for an existing env**: drop it under `data/datapacks/{datateardown|datapreparation}/<envKey>/<NN.subfolder>/<NN.script>.sql`. Numeric prefixes drive order. Rebuild the WebApi (`build-all.ps1` or `docker compose build` — the MSBuild Content Include packages the `.sql` files into `bin/datapacks/` automatically) and restart. Per-env opt-in flag must be `true` for that env.
- **Adding a new env**: create `data/datapacks/<phase>/<envKey>/` with at least one numbered subfolder and `.sql` file. Add a matching `Environments.<envKey>` entry with `BravoDbConnectionString` and `RunDataPacksOnStartup: true`.
- **Re-runnability is the author's job**: every script runs on every WebApi start. Use `CREATE OR ALTER PROCEDURE`, `IF NOT EXISTS ... INSERT`, or `MERGE`. A non-idempotent `CREATE PROCEDURE` will fail on the second start and abort the env's remaining scripts.
- **Adding a new phase folder** (beyond `datateardown` / `datapreparation`): edit `DataPackRegistry.PhaseOrder` to include the new name in the desired position. Phases run in declared order.
- **Tuning failure policy** (e.g. continue-on-error within an env, or abort WebApi on any failure): currently per-env-abort, never throw. Tweak `DataPackRunner.RunEnvAsync` (the `aborted = true` branch) for in-env continue, or rethrow the summary at the call site in `WebApi/Program.cs` for hard-fail. Both are conscious defaults; document any change.
- **Observability**: structured log lines (grep `DataPackRunner|DATAPACKS|datapacks:`) plus the dashboard panel at `/api/data-packs/startup-report`. The runner is a `Singleton` that retains its `LatestReport` for the panel.
- **Use `/add-data-pack-script`** to scaffold a new script with the right folder structure, idempotency template, and opt-in checklist.

### Adding orchestration chaining (activating `TestTask.DependsOn`)

- Currently declared on `TestTask` but unused by `TestOrchestrator`.
- Requires a pre-pass in `RunAsync` before the parallel fanout: topological sort by `DependsOn`, then execute in waves rather than a flat `Task.WhenAll`.
- Tasks can pass artefacts forward via `TestTask.Parameters` — a dependency resolver would inspect each predecessor's `TestResult.Metadata` and inject relevant keys into the dependent task.
- Only pursue if a future test shape truly needs independent tasks rather than step-chaining inside one agent. Phase 3's sibling-dispatch model avoided this for verifications.

### Adding a new chat action kind

- **DTO**: `src/AiTestCrew.WebApi/Models/Chat/ChatModels.cs` — `ChatAction` is a flat shape with nullable fields (`Kind`, `Path`, `Title`, `Data`, `Summary`) to keep the LLM JSON simple. Add whatever field you need or reuse `Data` as the payload blob.
- **System prompt**: `ChatIntentService.BuildSystemPrompt` — add a new "Kind — when to use" block documenting the shape and rules. The LLM follows this strictly; omitting a rule there is the most common source of misbehaviour.
- **Catalog enrichment**: if the action depends on data the LLM can't otherwise see (e.g. per-user preferences, per-test-set objectives), add it to `BuildCatalogAsync`. Keep the snapshot small — anything over a few hundred KB starts costing latency.
- **Client renderer**: `ui/src/components/chat/ChatDrawer.tsx` — extend the `ActionCard` switch and add a new card component. Mirror the existing pattern: idle → sending → done/error, with inline error display and no full-page redirects.
- **Confirmation rule**: if the action is a mutation, *always* require an Execute click. If it calls the server, surface the response (or its error) inline on the card. Don't silently succeed.
- **No new endpoint by default**: prefer reusing an existing WebApi endpoint the card calls from the client. Only add a new endpoint when the action needs server-side pre-validation (e.g. `/api/recordings` pre-validates the agent before enqueueing).

### Adding a new WebApi endpoint

- `src/AiTestCrew.WebApi/Endpoints/ModuleEndpoints.cs` (or create a sibling file if the surface is big enough to warrant it).
- Register in `MapGroup(...)` inside that file's `MapModuleEndpoints` extension.
- `Program.cs` already wires the group — new endpoints appear automatically.
- Response shape: reuse `TestSetResponse(testSet, historyRepo)` for anything returning a full test set detail (keeps the UI's polling consistent).

### Adding a new UI edit dialog

- The generic `EditWebUiTestCaseDialog.tsx` handles any `WebUiTestDefinition`. Pass custom `onSave` / `onDelete` callbacks for a new context.
- For a non-Web step shape (Desktop, API definition, etc.) build a parallel dialog following the same prop shape — `definition / caseName / onSave / onDelete / title / deleteLabel`. Keep the form-state / reorder / add-step / delete-step behaviour identical so users don't learn N different UIs.

### Adding a new CLI flag

- `src/AiTestCrew.Runner/Program.cs` — `ParseArgs` switch, `CliArgs` init property, appropriate handler.
- If it affects a run: thread into `orchestrator.RunAsync(..., yourFlag: cli.YourFlag)` and then into `TestTask.Parameters[...]` if agents need it.
- Keep the header-print block (`Module:`, `Test set:`, `Objective filter:`, etc.) up to date so users can see at a glance what flags resolved to.
- Document in `CLAUDE.md` command cheatsheet + the flag reference table, and `docs/functional.md`'s command reference.

### Adding a new test case / step JSON field

- Persistence model lives in `src/AiTestCrew.Storage/Persistence/` (TestObjective, PersistedTestSet, PersistedExecutionRun) or adjacent definition types in `Storage/Shared/`, `Storage/ApiAgent/`, `Storage/AseXmlAgent/`.
- Additive fields are backward-compatible: System.Text.Json's `PropertyNameCaseInsensitive` + default-value behaviour reads old JSON without migration.
- Update the matching TS type in `ui/src/types/index.ts`.
- Extend `FromTestCase` / `ToTestCase` if the field should survive the round-trip.
- If the field should be editable, extend the relevant edit dialog.

### Adding a new agent (entirely new target type)

- Use `/add-agent`. The skill codifies:
  1. Adding the enum value to `TestTargetType`.
  2. Creating the agent class (`TargetAgent/{Name}TestAgent.cs`) extending `BaseTestAgent`.
  3. Step-definition shape + `FromTestCase`/`ToTestCase` parity.
  4. `ExecuteAsync` flow including reuse-mode `PreloadedTestCases` handling.
  5. DI registration in both `Runner/Program.cs` and `WebApi/Program.cs`.
  6. Orchestrator's `BuildObjectiveFromResults` + reuse task-builder updates.
  7. Decomposer prompt update.


---

## Backup & Restore

### How it works

DatabaseBackupService (src/AiTestCrew.WebApi/Services/DatabaseBackupService.cs) is a BackgroundService registered alongside AgentHeartbeatMonitor. On each tick it calls the SQLite Online Backup API:

    using var src = _factory.CreateConnection();   // live DB
    using var dst = new SqliteConnection("Data Source={dest}");
    dst.Open();
    src.BackupDatabase(dst);                        // streams pages while DB stays writable

The backup file is a fully self-contained SQLite database at the point the copy started. No WAL merge is needed -- the Online Backup API handles WAL checkpointing internally. The source DB is never locked.

### Retention policy

After each backup a sweep runs in-process (RunRetentionSweep). Files are sorted newest-first by filename (the timestamp encoding makes lexicographic sort equivalent to chronological sort). Three tiers:

1. Keep the newest RetentionHourly files unconditionally (default 24).
2. Keep one file per UTC calendar day for the next RetentionDaily days beyond the hourly window (default 14).
3. Keep one file per Mon-boundary week for the next RetentionWeekly weeks beyond the daily window (default 8).

Everything else is File.Delete'd. At steady state: at most 46 files.

### Disk-space guard

Before writing, DatabaseBackupService checks DriveInfo.AvailableFreeSpace on the backup directory's drive. If free space is below MinFreeDiskMb, the backup is skipped and a warning is logged -- the service does NOT throw. _lastError is set, so the dashboard panel turns red.

### Concurrency guard

Interlocked.CompareExchange on _running prevents two concurrent backups (e.g. a scheduled tick overlapping with a manual POST /api/admin/backup). The POST endpoint returns 409 if already running; the tick silently skips.

### Admin endpoints

- POST /api/admin/backup -- trigger an out-of-cycle backup. Returns path + sizeBytes + durationMs.
- GET /api/admin/backup/status -- last success, size, error, next scheduled time, total files on disk, oldest backup.
- GET /api/admin/backup/list -- directory listing (paths only).

All three are behind ApiKeyAuthMiddleware. The users.is_admin column was added in schema v11 as a forward-compat landing. Role enforcement shipped in schema v13 (see the User roles + shared agents section below).

### Dashboard panel (BackupHealthPanel)

BackupHealthPanel polls /api/admin/backup/status every 60 s. Colour thresholds:

- Green -- last success under 90 minutes ago
- Amber -- last success between 90 min and 2 x IntervalMinutes
- Red -- last success older than 2x interval, or lastError set in the last hour
- Hidden -- if the status endpoint returns an error (e.g. service is disabled)

### Bind-mount requirement

The backup Directory config MUST be a host bind-mount in docker-compose.yml, not a path inside the named data volume. If backups live inside aitestcrew-data, they are wiped by the same volume corruption that destroys the live DB. The default docker-compose.yml includes:

    volumes:
      - ./docker-backups:c:\data-backups    # bind-mount; survives docker volume rm
      - aitestcrew-data:c:\data              # named volume -- live DB

### schema v10 -> v11 migration

Added users.is_admin INTEGER NOT NULL DEFAULT 0. All existing users are backfilled to is_admin = 1 (matching the v1 "any key holder is admin" policy). Migration is idempotent (ColumnExists guard before ALTER).

### Restore

See docs/ops/backup-restore.md for step-by-step procedures (Windows container + Linux container, sidecar swap pattern, rollback).

### Tuning backup settings

- Interval: TestEnvironment.Backup.IntervalMinutes (default 30). Minimum 1.
- Retention: RetentionHourly / RetentionDaily / RetentionWeekly (defaults 24 / 14 / 8).
- Disk guard: MinFreeDiskMb (default 500). Set to 0 to disable.
- Directory: must match the bind-mount target in docker-compose.yml.
- Disable entirely: Enabled = false. No files will be written and the panel hides.


## User roles + shared agents (REQ-012)

### Problem solved

In a multi-user deployment, the pre-flight auth-health panel showed every agent auth-state tile to every logged-in user. A QA on BRLAP110 would see "Expired 12d ago on Kalhara PC" tiles for machines they cannot access. The Refresh button for those tiles would enqueue a job that no available agent could claim.

The orthogonal problem: a central CI VM is shared by the whole team, not owned by any one person. A simple "show only your own" filter would hide it from everyone.

### Three-role model

| Role | Assigned by | What they can do |
|---|---|---|
| User (default) | Auto-assigned on create | See and manage own agents; auth-health tiles for own agents only |
| AuthSteward | Admin promotes | User rights plus shared agents in auth-health panel and Refresh on them |
| Admin | Bootstrap (first user) or promotion | Everything AuthSteward does, plus: mark agents shared, promote/demote user roles, see all agents |

Bootstrap rule: the first POST /api/users auto-promotes the new user to Admin. All subsequent creates default to User.

### Shared agents

Agent.IsShared (schema: agents.is_shared INTEGER NOT NULL DEFAULT 0) marks an agent as a shared central-execution agent (e.g. a CI VM). Shared agents are visible to admins and AuthStewards in the auth-health panel; plain Users do not see them. Only admins can register or toggle a shared agent.

is_shared is sticky: re-registration without the --shared flag does NOT clear a previously-set flag. Use PUT /api/agents/{id}/shared to explicitly unshare.

### Scoping predicate

AuthHealthEndpoints.IsVisibleToUser(agent, me):

```csharp
return me.Role switch {
    "Admin"        => true,
    "AuthSteward"  => agent.UserId == me.Id || agent.IsShared,
    _              => agent.UserId == me.Id,
};
```



Auth-state tiles are filtered through this predicate before the grouping logic runs. A User with no actionable agents sees friendly copy: "All your agents auth states are fresh."

### Defence-in-depth: 403 on refresh trigger

POST /api/auth-refreshes/{id}/start re-checks visibility before enqueueing the AuthSetup job. Without this, the panel filter is cosmetic -- a crafted curl could enqueue a refresh for a machine the caller cannot access.

### New endpoints

| Endpoint | Auth | Behaviour |
|---|---|---|
| PUT /api/agents/{id}/shared | Admin | Toggle is_shared on an existing agent |
| PUT /api/users/{id}/role | Admin | Promote / demote user role (last-admin guard) |
| GET /api/users/me | Any | Now includes role field |
| POST /api/users/validate | Public | Now returns role in the user payload |

### CLI flag

```powershell
.\AiTestCrew.Runner.exe --agent --name "CI-VM-01" --shared --role Execution
```

Or via config: TestEnvironment.AgentShared = true. Server returns 403 if the caller is not Admin.

### Schema v12 to v13

```sql
ALTER TABLE users  ADD COLUMN role      TEXT NOT NULL DEFAULT 'User';
ALTER TABLE agents ADD COLUMN is_shared INTEGER NOT NULL DEFAULT 0;
```

Bootstrap: UPDATE users SET role = 'Admin' WHERE id = (SELECT id FROM users ORDER BY created_at ASC LIMIT 1).

Both ALTERs are idempotent (ColumnExists guard). is_shared defaults to 0 so all existing agents remain personal.


## Jira Xray Import (REQ-017)

### Overview

The Xray import subsystem lets QAs bring Jira Xray test cases into AITestCrew without manual re-entry. It lives entirely in the WebApi layer — agent boxes never touch Jira credentials.

### Data flow

```
QA (UI or CLI)
  |
  v
POST /api/xray/import            ← preview only, nothing persisted
  |
  +--> IJiraXrayClient.GetTestAsync(ticketKey)
  |      |- JiraXrayCloudClient  (Xray JWT + Jira REST v3, ADF description)
  |      |- JiraXrayServerClient (Basic auth, Jira REST v2, plain-text description)
  |         XrayDescriptionParser: ADF JSON → ParsedXrayDescription (sections)
  |
  +--> XrayImportService.DecomposeAsync()
  |      LLM call: ticket summary + steps → List<ProposedObjective>
  |      (prefer 1-2; >4 sets reviewCarefullyFlag)
  |
  +--> XrayImportService.MapFragmentsAsync()  (per objective)
         LLM call with CapabilityRegistry markdown as system context
         → List<XrayMappingRow>  { kind, confidence, definition | rationale }
         kind ∈ { api, webUi, desktopUi, asexml, asexmlDelivery,
                  postStep, placeholder, unsupported }

QA reviews preview → calls POST /api/xray/import/confirm

  +--> FilterAndMerge (QA-accepted slugs + merge requests)
  +--> ITestSetRepository.LoadAsync()
  +--> foreach accepted objective:
  |      find existing by (XrayTicketKey, XrayObjectiveSlug) or create new TestObjective
  |      MapRowsToObjective: kind → ApiSteps / WebUiSteps / AseXmlSteps / etc.
  |      ITestSetRepository.SaveAsync()
  |
  +--> foreach unsupported row:
         GapRequirementWriter.Write(GapReqSpec)
         → requirements/REQ-<next>-<slug>.md  (NOT committed)
```

### Capability registry

`CapabilityRegistry` (static, `AiTestCrew.Core.Capabilities`) is the LLM's ground truth. It lists:
- **StepTypes** — every `TestTargetType` supported today with a one-line description
- **PostStepTypes** — DB Assert, Event Assert (Service Bus), API post-step, aseXML post-delivery verification
- **AssertionPrimitives** — sourced from existing enums (`ApiAssertionSource`, `AssertionOperator`, `MatchMode`, desktop action strings)
- **UnsupportedExamples** — canonical "what AITestCrew cannot do" examples that guide the LLM's `unsupported` verdict

`GET /api/capabilities` returns the registry as `CapabilityRegistryDto` (JSON). The import LLM prompt includes `CapabilityRegistry.GetMarkdown()` as inline context.

The registry must be kept in sync with new step types and post-step types. Adding a new `TestTargetType` without updating the registry causes the LLM to misclassify Xray steps for that target — update `CapabilityRegistry.cs` when extending.

### Idempotency

Re-importing the same ticket does not create duplicates. The idempotency key is `(TestObjective.XrayTicketKey, TestObjective.XrayObjectiveSlug)`. If an existing objective matches, it is updated in place. Objectives whose slugs are no longer in the proposal are flagged in the preview but not deleted (conservative posture — protects recorded steps).

### Gap-REQ stub generator

`GapRequirementWriter` scans `requirements/*.md` for the highest `REQ-NNN` number, increments, and writes a stub file matching the house frontmatter + section structure. The stub is never committed automatically — it lands on disk for the QA to review and commit when ready. The agentic development pipeline (`feature-coordinator`) can then implement it.

### Persistence schema additions (additive, no migration needed)

`TestObjective` gains five optional fields:
- `Source = "ImportedFromXray"` — new allowed value alongside `"Generated"` / `"Recorded"`
- `XrayTicketKey` — back-link to the originating Jira ticket
- `XrayObjectiveSlug` — stable slug for the decomposed slice (idempotency key with `XrayTicketKey`)
- `Preconditions: List<string>` — bullet list from the Xray Description's Preconditions section
- `TestDataNotes: string?` — from the Xray Description's Test Data section

All fields are additive; missing fields on old objectives deserialise as null/empty.

### Layer constraints

- `JiraXrayConfig` is in `AiTestCrew.Core.Configuration` (not WebApi) so `TestEnvironmentConfig` can reference it without violating the dependency direction rule.
- The client implementations are in `src/AiTestCrew.WebApi/Integrations/JiraXray/` — accessible to WebApi, not referenced from Agents or Core.
- No agent, Orchestrator, or Runner code depends on Jira types. The CLI `--import-xray` flag in the Runner calls the WebApi's `/api/xray/import` endpoint over HTTP, the same way the UI does.
