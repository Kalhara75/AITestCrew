# AITestCrew — Architecture Documentation

## Solution Structure

The solution (`AiTestCrew.slnx`) contains five .NET 8 projects with a strict layered dependency graph — each layer only references layers below it. A React frontend communicates with the WebApi over REST.

```
┌──────────────┐     ┌──────────────────┐
│  React UI    │────▶│  AiTestCrew      │
│  (Vite+TS)   │ HTTP│  .WebApi (REST)  │──┐
│  Port 5173   │◀────│  Port 5050       │  │
└──────────────┘     └──────────────────┘  │
                                           │
┌──────────────────┐                       │
│  AiTestCrew      │──┐                   │
│  .Runner (CLI)   │  │                   │
└──────────────────┘  │                   │
                      ▼                   ▼
              AiTestCrew.Orchestrator
                      │
              AiTestCrew.Agents
                      │
              AiTestCrew.Core
```

Both Runner (CLI) and WebApi reference the same Orchestrator/Agents/Core layers. They share the same `modules/`, `testsets/` (legacy), and `executions/` file storage.

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
| `Configuration/TestEnvironmentConfig.cs` | Strongly-typed settings binding from `appsettings.json` (includes `ApiStacks`) |
| `Configuration/ApiStackConfig.cs` | Config models for API stacks (`ApiStackConfig`) and modules (`ApiModuleConfig`) |

---

### AiTestCrew.Agents

Contains agent implementations and test set persistence. References Core only.

```
Agents/
  ApiAgent/
    ApiTestAgent.cs               — REST/GraphQL test execution (multi-stack aware via IApiTargetResolver)
    ApiTestDefinition.cs          — LLM-generated API test step model + validation verdict
  Auth/
    ApiTargetResolver.cs          — Resolves API base URLs and per-stack LoginTokenProviders from ApiStacks config
    LoginTokenProvider.cs         — Acquires JWTs by calling a stack's security module login endpoint
    StaticTokenProvider.cs        — Returns a pre-configured static token
  Base/
    BaseTestAgent.cs              — Shared LLM communication (delegates JSON utilities to LlmJsonHelper)
    LlmJsonHelper.cs              — Static JSON cleaning/parsing utilities (shared with WebApi endpoints)
  Shared/
    WebUiTestCase.cs              — WebUiTestCase + WebUiStep models (legacy, shared by both UI agents)
    WebUiTestDefinition.cs        — WebUiTestDefinition model (v2 step definition for TestObjective.WebUiSteps)
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
  Persistence/
    PersistedModule.cs            — Module manifest model (id, name, description, timestamps)
    PersistedTestSet.cs           — JSON envelope model for saved test sets (contains List<TestObjective> TestObjectives, v2 schema)
                                    Includes SetupSteps (List<WebUiStep>) and SetupStartUrl for reusable
                                    pre-test-case setup (e.g. login) — runs before every test case in the set
                                    Includes ApiStackKey and ApiModule for multi-stack API targeting
    PersistedExecutionRun.cs      — Execution history models (run, objective results with PersistedObjectiveResult, step results)
    ModuleRepository.cs           — File I/O for modules/{id}/module.json
    TestSetRepository.cs          — File I/O for test sets (legacy flat + module-scoped, incl. move objective)
                                    SaveAsync creates the module directory if it does not exist
    ExecutionHistoryRepository.cs — File I/O for executions/{testSetId}/{runId}.json
    SlugHelper.cs                 — Shared slugification logic
    MigrationHelper.cs            — Auto-migrates legacy testsets/ to modules/default/
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

**Fill execution** — After `FillAsync`, the step dispatcher dispatches explicit `input` and `keyup` events on the target element. Many JS-based components (e.g. jQuery `keyup` menu filters, Kendo search inputs) rely on keyboard events that `FillAsync` does not fire. A 500 ms pause follows to let debounced handlers update the DOM. A separate `type` action is available for character-by-character typing via `PressSequentiallyAsync`.

**Storage state & TOTP** — `BraveCloudUiTestAgent` saves browser cookies/localStorage to `BraveCloudUiStorageStatePath` after a successful SSO login. Subsequent calls within `BraveCloudUiStorageStateMaxAgeHours` pass this file to `browser.NewContextAsync()` via `StorageStatePath`, skipping the full Azure AD redirect flow. The path is resolved to absolute at DI startup (both Runner and WebApi share the same resolved path). When `BraveCloudUiTotpSecret` is configured (base32), the agent computes TOTP codes via OtpNet and enters them automatically during SSO. When empty and MFA is encountered: `PlaywrightHeadless=false` → waits 120 s for manual entry; `PlaywrightHeadless=true` → fails with remediation instructions. The CLI `--auth-setup` command provides a standalone way to perform SSO + 2FA manually and save the auth state.

**Test case persistence** — `TestObjective` has three step collections:
- `ApiSteps` (`List<ApiTestDefinition>`) — populated by API agents
- `WebUiSteps` (`List<WebUiTestDefinition>`) — populated by web UI agents and the Playwright recorder
- `DesktopUiSteps` (`List<DesktopUiTestDefinition>`) — populated by desktop UI agents and the desktop recorder

`TargetType` (string, default `"API_REST"`) is also stored so the orchestrator can reconstruct tasks with the correct `TestTargetType` on reuse.

`Source` (string, default `"Generated"`) tracks how the objective was created: `"Generated"` for AI/LLM-created objectives or `"Recorded"` for objectives captured via `--record`. Rebaseline is only available for generated objectives. Legacy JSON files without the field are backfilled in `MigrateLegacyObjective()` using the `recorded-` ID prefix heuristic.

#### Desktop UI Agent

`WinFormsUiTestAgent` extends `BaseDesktopUiTestAgent`, which uses FlaUI (UI Automation 3) to automate Windows Forms applications.

**App lifecycle** — The target application is launched via `Application.Launch(ProcessStartInfo)` with `WorkingDirectory` set to the exe's own directory (so sibling DLLs load correctly). The app is closed in a `finally` block. Between test cases, the app is optionally relaunched for clean state (`WinFormsCloseAppBetweenTests`, default true). After clicks that change the window count (dialog close, new form open), the executor auto-waits 1.5s for the app to settle.

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

**Step model** — `DesktopUiStep` uses composite selectors (AutomationId, Name, ClassName, ControlType, TreePath) instead of a single CSS selector string. Desktop-specific actions include `menu-navigate` (MenuBar traversal via MenuPath), `wait-for-window`/`switch-window`/`close-window` (window title matching), and `assert-enabled`/`assert-disabled`.

**Desktop recording** — `DesktopRecorder` uses low-level Windows hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) via P/Invoke with an explicit `PeekMessage`/`TranslateMessage`/`DispatchMessage` message pump (required for hook callbacks to fire). Key recording behaviours:
- **Click capture**: `automation.FromPoint()` resolves the clicked element. Window chrome (title bar, scroll bars) and system UI elements (taskbar buttons, shell tray, UWP app IDs) are automatically filtered out.
- **Keyboard capture**: Consecutive keystrokes are coalesced into `fill` steps. Special keys (Enter, Tab, Escape) are recorded as `press` steps.
- **Ctrl+V paste**: Detected via `GetKeyState(VK_CONTROL)`. The actual clipboard content is read on an STA thread and recorded as the fill value (not just the "V" keystroke). Ctrl+A/C/X are silently ignored.
- **Selector building**: `DesktopElementResolver.BuildSelector()` populates all available properties. Pure numeric AutomationIds (window handles) and default WinForms designer names (`textBox1`, `button2`) are skipped as unstable.
- **Assertion capture**: When the user presses T/V/E in the console, the next click is converted to an assertion. Text extraction uses the stored element reference directly (not a re-search from stale `mainWindow`) and searches children/descendants for text.
- **Post-recording validation**: Warns about TreePath-only selectors, consecutive clicks without waits, and missing assertions.

**React UI** — `DesktopUiTestCaseTable` displays desktop test cases with step count and action preview. `EditDesktopUiTestCaseDialog` provides a full step editor with five cascading selector fields (AutomationId, Name, ClassName, ControlType, TreePath), action-specific context fields (Value, MenuPath, WindowTitle), and step add/remove/reorder controls.

> **Note:** `PersistedTaskEntry` is **deprecated** (v1 schema only). It is retained solely for deserializing legacy test set files during migration. New code should use `TestObjective` exclusively.

#### PlaywrightRecorder

`PlaywrightRecorder.RecordAsync` provides a human-driven alternative to LLM generation. It:
- Launches non-headless Chromium (`--start-maximized`, `SlowMo = 50`). MVC targets use `NoViewport` (maximized window); Blazor targets use 1920×1080 to match the replay viewport. Optionally loads a `StorageStatePath` for authenticated recording sessions.
- Calls `page.ExposeFunctionAsync("aitcRecordStep", ...)` — JS→.NET bridge, survives page navigation
- Calls `page.ExposeFunctionAsync("aitcStopRecording", ...)` — signals a `TaskCompletionSource`
- Calls `page.AddInitScriptAsync(...)` — re-injects event listeners and overlay panel on every page load (deferred via `DOMContentLoaded` so `document.body` is ready)

**Event capture:**
- **`input` events** on form fields (→ `fill`) — captured as the user types (not on blur), ensuring fills are recorded before any subsequent click. `change` events are used only for `<select>` elements.
- **`click` events** (→ `click`) — with Kendo PanelBar-aware handling: group header clicks (e.g. "Standing Data") are resolved to `text="GroupName"` selectors via `bestSelectorForPanelBarHeader()`. Kendo Window close buttons are recorded as `press Escape` for simpler replay.
- **`keydown` events** — captures Escape key presses (modal dismissal).

**Selector computation** uses `bestSelector(el)` with an extended fallback chain:
`#stableId → tag[name] → tag[type="submit"] → tag[aria-label] → tag[title] → a[href*="rawHref"] → MudBlazor text → tag[data-*] → tag[role] → tag.uniqueClass → text="ownText" → text="innerText" → grid row context → SVG icon fingerprint → tag`.
- IDs matching dynamic/stateful patterns (`_active`, `_pb_`, `_wnd_`, GUIDs) are skipped.
- `aria-label` is checked early (priority #4) — MudBlazor icon buttons like "Notifications", "Sort", "Column options" use this. Labels starting with "Toggle " are skipped (nav group headers use text instead).
- Link selectors use the **raw `getAttribute('href')`** value (not resolved URL) because CSS `[href*=]` matches raw attributes. Blazor renders relative hrefs (e.g. `./Security/UserSearch`).
- MudBlazor text detection checks `.mud-nav-link-text` and `.mud-button-label` child elements.
- Grid row action buttons use Playwright chained selectors: `tr:has-text("rowId") >> td[data-label="Actions"] >> button >> nth=N`.
- Icon-only buttons (no text, no label) use SVG path fingerprinting via `click-icon` action with occurrence index.
- MudBlazor state classes (`mud-ripple`, `mud-expanded`, `mud-nav-link`, `mud-icon`, `mud-svg`) and Kendo state classes (`k-state-*`) are excluded from class-based selectors.

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

### AiTestCrew.Orchestrator

Decomposes objectives, routes tasks to agents, aggregates results, manages run modes. References Core and Agents.

```
Orchestrator/
  TestOrchestrator.cs   — RunAsync, DecomposeObjectiveAsync, SaveTestSetAsync (308 lines)
```

---

### AiTestCrew.Runner

CLI entry point. Wires up DI, handles argument parsing, drives the run, renders output. References all other projects.

```
Runner/
  Program.cs                       — Top-level statements: arg parsing, DI, console output
                                     Includes --record mode short-circuit (before DI host build):
                                     slugifies module/testset IDs, calls PlaywrightRecorder.RecordAsync,
                                     creates module manifest if missing, saves WebUiTestDefinition to test set
  AnthropicChatCompletionService.cs — Bridges Anthropic.SDK to Semantic Kernel's IChatCompletionService
  FileLoggerProvider.cs            — Writes all log messages to a timestamped file in logs/
  appsettings.json                 — Runtime configuration (copied to output directory on build)
  appsettings.example.json         — Template with placeholder values for source control
```

**`--record` mode** runs before the DI host is built (no Orchestrator or agents needed). It resolves the module ID and test set ID via `SlugHelper.ToSlug` so the saved file path matches what the WebApi expects, then creates the module manifest via `ModuleRepository` if it does not exist.

**`--record-setup` mode** also runs before the DI host. It reuses `PlaywrightRecorder.RecordAsync` but saves the captured steps into `PersistedTestSet.SetupSteps` and `SetupStartUrl` instead of creating a new `TestObjective`. These setup steps (typically login) run before every test case in the test set during replay.

---

### AiTestCrew.WebApi

REST API backend for the React UI. Mirrors Runner's DI wiring but exposes HTTP endpoints instead of a CLI.

```
WebApi/
  Program.cs                       — DI wiring, CORS, minimal API endpoints, migration, screenshot static files
  AnthropicChatCompletionService.cs — Copy of Runner's bridge (same layer, can't reference Runner)
  Endpoints/
    ModuleEndpoints.cs             — Module CRUD + nested test set and run access + per-objective status
    TestSetEndpoints.cs            — Legacy flat test set endpoints (backward compat)
    RunEndpoints.cs                — POST /api/runs (trigger with optional objectiveId, apiStackKey, apiModule), GET /api/runs/{id}/status (poll)
  Services/
    RunTracker.cs                  — ConcurrentDictionary tracking active/completed individual runs
    ModuleRunTracker.cs            — ConcurrentDictionary tracking module-level composite runs (parallel test set execution)
  appsettings.example.json         — Template config
```

**REST API endpoints:**

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/api/modules` | List all modules with test set counts |
| `POST` | `/api/modules` | Create a module |
| `GET` | `/api/modules/{id}` | Module detail |
| `PUT` | `/api/modules/{id}` | Update module name/description |
| `DELETE` | `/api/modules/{id}` | Delete empty module |
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
| `GET` | `/api/health` | Health check |
| `GET` | `/screenshots/{filename}` | Serve Playwright failure screenshots (static files from `PlaywrightScreenshotDir`) |

---

### React Frontend (`ui/`)

Single-page application built with React 18, TypeScript, and Vite. Communicates with WebApi over REST.

```
ui/src/
  main.tsx                         — React root + QueryClientProvider + ActiveRunProvider + BrowserRouter
  App.tsx                          — Route definitions with Layout wrapper
  api/
    client.ts                      — fetch wrapper with base URL + error handling
    config.ts                      — API functions for config discovery (fetchApiStacks)
    modules.ts                     — API functions for modules and module-scoped test sets/runs (incl. triggerModuleRun, fetchModuleRunStatus)
    testSets.ts                    — API functions for legacy flat test sets and runs
    runs.ts                        — API functions for triggering and polling runs (incl. fetchActiveRun)
  contexts/
    ActiveRunContext.tsx            — Global run state: tracks module-level and individual runs, polls status,
                                     recovers active run on page refresh via GET /api/runs/active
  pages/
    ModuleListPage.tsx             — Module card grid (root page)
    ModuleDetailPage.tsx           — Test sets within a module + search/sort/status-filter toolbar,
                                     progressive card loading (IntersectionObserver), create/run dialogs
    TestSetDetailPage.tsx          — Test cases table + run history + trigger button (module-aware)
    ExecutionDetailPage.tsx        — Objective results with expandable step details (module-aware)
  components/
    Layout.tsx                     — Header, nav, content area
    StatusBadge.tsx                — Color-coded Passed/Failed/Error/Running badge
    TestSetCard.tsx                — Test set summary card (module-scoped links)
    TestCaseTable.tsx              — API test cases: HTTP method, endpoint, expected status table; inline delete per step
    WebUiTestCaseTable.tsx         — Web UI test cases: name, start URL, step count, screenshot flag; inline delete per step
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
    │       ├─ DecomposeObjectiveAsync()
    │       │       LLM decomposes objective → TestTask (one per objective)
    │       │
    │       ├─ For each objective (TestTask):
    │       │       FindAgentAsync() → routes to ApiTestAgent (if API_REST/GraphQL)
    │       │       agent.ExecuteAsync(task) → returns ONE TestResult with ObjectiveId
    │       │           ├─ TryLoadOpenApiSpecAsync()       [optional]
    │       │           ├─ DiscoverEndpointAsync()         [live GET, captures real fields]
    │       │           ├─ GenerateTestCasesAsync()        [LLM → List<ApiTestDefinition>]
    │       │           └─ For each ApiTestDefinition (step):
    │       │                   ExecuteTestCaseAsync()
    │       │                       ├─ Build HttpRequestMessage (URL from IApiTargetResolver)
    │       │                       ├─ InjectAuthAsync() → IApiTargetResolver.GetTokenProvider(stackKey)
    │       │                       ├─ HttpClient.SendAsync()
    │       │                       └─ ValidateResponseAsync()
    │       │                               ├─ Rule checks (status, contains)
    │       │                               └─ LLM validation (JSON, types, security)
    │       │       Metadata["generatedTestCases"] = list of ApiTestDefinition
    │       │
    │       ├─ SaveTestSetAsync()   [persists TestObjectives to modules/{moduleId}/{testSetId}.json]
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
            ├─ (Optional) Single-objective filter:
            │       If objectiveId is provided, filters tasks to only the matching objective.
            │       Other objectives are not executed. The resulting execution run contains
            │       only the single objective's results.
            │
            ├─ For each TestObjective (or single filtered objective):
            │       Injects ApiSteps/WebUiSteps into TestTask.Parameters["PreloadedTestCases"]
            │       If test set has SetupSteps, also injects SetupSteps + SetupStartUrl
            │       agent.ExecuteAsync(task) → returns ONE TestResult with ObjectiveId
            │           ├─ Detects "PreloadedTestCases" in Parameters
            │           ├─ Skips spec load, discovery, and LLM generation
            │           ├─ (Web UI) For each test case:
            │           │       Run SetupSteps first (navigate SetupStartUrl → execute setup)
            │           │       Then navigate to test case StartUrl → execute test steps
            │           └─ Executes saved ApiTestDefinition/WebUiTestCase steps directly
            │
            ├─ TestSetRepository.UpdateRunStatsAsync()  [bumps RunCount, LastRunAt]
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

### Module and Test Set Storage

Tests are organised in a **Module > Test Set > Test Objective > Steps** hierarchy. On disk:

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
  "createdAt": "2026-04-04T14:30:00Z",
  "lastRunAt": "2026-04-04T15:45:00Z",
  "runCount": 3,
  "testObjectives": [
    {
      "objectiveId": "a1b2c3d4",
      "objectiveText": "Test GET /api/ControlledLoadDecodes endpoint",
      "objectiveName": "Ctrl Load GET",
      "targetType": "API_REST",
      "apiSteps": [
        {
          "name": "Get all products - happy path",
          "method": "GET",
          "endpoint": "/api/products",
          "headers": {},
          "queryParams": {},
          "body": null,
          "expectedStatus": 200,
          "expectedBodyContains": ["id", "name", "price"],
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
      "apiSteps": [ ... ],
      "webUiSteps": []
    }
  ]
}
```

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
TestEnvironmentConfig       → Singleton (bound from appsettings.json)
Kernel                      → Singleton (Semantic Kernel, with IChatCompletionService)
IChatCompletionService      → Singleton (AnthropicChatCompletionService or OpenAI)
IHttpClientFactory          → Managed by AddHttpClient()
IApiTargetResolver          → Singleton (ApiTargetResolver — resolves stack+module URLs and per-stack token providers)
ApiTestAgent                → Singleton (concrete + ITestAgent)
TestSetRepository           → Singleton (new instance, baseDir = AppContext.BaseDirectory)
ExecutionHistoryRepository  → Singleton (new instance, baseDir = AppContext.BaseDirectory)
ModuleRepository            → Singleton (new instance, baseDir = AppContext.BaseDirectory)
TestOrchestrator            → Singleton (receives IEnumerable<ITestAgent> + all repos)
RunTracker                  → Singleton (WebApi only — tracks individual async run state)
ModuleRunTracker            → Singleton (WebApi only — tracks module-level composite runs)
```

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
