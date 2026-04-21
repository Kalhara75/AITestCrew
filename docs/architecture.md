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
    ChatEndpoints.cs               — POST /api/chat/message — LLM-backed natural-language → structured ChatResponse (reply + actions)
    RecordingEndpoints.cs          — POST /api/recordings — enqueues a recording/auth-setup job for a local agent (JobKind = Record | RecordSetup | RecordVerification | AuthSetup)
  Models/
    Chat/ChatModels.cs             — ChatMessage / ChatRequest / ChatResponse / ChatAction DTOs for the /api/chat/message contract
  Services/
    IRunTracker.cs                 — Interface for individual run tracking (HasActiveRunForTestSet)
    IModuleRunTracker.cs           — Interface for module-level composite run tracking (HasActiveModuleRunForModule)
    RunTracker.cs                  — In-memory IRunTracker implementation (ConcurrentDictionary)
    ModuleRunTracker.cs            — In-memory IModuleRunTracker implementation (ConcurrentDictionary)
    AgentHeartbeatMonitor.cs       — BackgroundService that marks agents Offline when heartbeat goes stale
    RunDispatchHelper.cs           — Decides whether a run must be enqueued for a local agent (UI targets) or run in-process
    ChatIntentService.cs           — Builds live catalog (modules, test sets, envs, stacks, endpoints, agents, current-test-set objectives) + system prompt, calls IChatCompletionService, deserializes ChatResponse via LlmJsonHelper
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
| `GET` | `/api/config/environments` | List configured customer environments (key, display name, default flag, data-teardown opt-in) |
| `GET` | `/api/config/endpoints` | List Bravo `EndPointCode`s via `IEndpointResolver.ListCodesAsync()`. Returns `{ endpoints, error? }` — the `error` field is populated (and `endpoints` is empty) when the Bravo DB is unreachable, so the chat catalog degrades gracefully instead of 500ing. |
| `POST` | `/api/chat/message` | LLM-backed chat turn. Body: `{ messages[], context? }`. Returns `{ reply, actions[] }` where each action is `navigate` / `showData` / `confirmRun` / `confirmCreate` / `confirmRecord`. |
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
    ModuleDetailPage.tsx           — Test sets within a module + search/sort/status-filter toolbar,
                                     progressive card loading (IntersectionObserver), create/run dialogs
    TestSetDetailPage.tsx          — Test cases table + run history + trigger button (module-aware)
    ExecutionDetailPage.tsx        — Objective results with expandable step details (module-aware)
    LoginPage.tsx                  — API key login form (shown when auth is enabled and no key stored)
  components/
    Layout.tsx                     — Header, nav, content area, user name display + logout button
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
| SQLite | `"Sqlite"` | `SqliteModuleRepository`, `SqliteTestSetRepository`, `SqliteExecutionHistoryRepository`, `SqliteUserRepository` | Single DB file; WAL mode for concurrent reads; also enables user auth |

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

**SQLite schema** — Seven tables:

| Table | Purpose |
|---|---|
| `modules` | Module metadata + full JSON in `data TEXT` column |
| `test_sets` | Test set metadata (module_id FK) + full JSON in `data TEXT` |
| `execution_runs` | Run metadata (test_set_id, status, timestamps) + full JSON in `data TEXT` |
| `users` | Id, Name, ApiKey (unique, `atc_` prefixed), Role, timestamps |
| `active_runs` | In-progress individual runs (for crash recovery) |
| `active_module_runs` | In-progress module-level runs (for crash recovery) |
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

- `PersistedModule` gained `CreatedBy` and `LastModifiedBy` fields — populated from the authenticated user.
- `PersistedTestSet` gained `CreatedBy` and `LastModifiedBy` fields.
- `PersistedExecutionRun` gained `StartedBy` (user ID) and `StartedByName` (display name) fields.
- File-based backend: fields are present but always null (no auth in file mode).

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
| `POST` | `/api/agents/register` | Register or re-register a Runner as an agent |
| `POST` | `/api/agents/{id}/heartbeat` | Keep-alive; returns the job the server thinks this agent is running |
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

### Screenshot forwarding

When an agent captures a Playwright or FlaUI failure screenshot, the file lives on the agent's local disk — the server serving the dashboard can't see it. `RemoteScreenshotUploader.TryUploadAsync` (in `AiTestCrew.Agents/Shared/`) is invoked from `BaseWebUiTestAgent.CaptureScreenshotAsync` and `BaseDesktopUiTestAgent.CaptureScreenshot`. When `TestEnvironmentConfig.ServerUrl` is set (agent mode), it POSTs the file to `/api/screenshots`, which saves it into the server's `PlaywrightScreenshotDir`. The step detail line still reads `"...| Screenshot: <filename>"`, and the existing `/screenshots/{filename}` static handler resolves to the uploaded copy. When `ServerUrl` is empty (legacy local mode), the uploader is a no-op — the screenshot is served directly from the local dir.

The endpoint strips directory components from the uploaded filename (`Path.GetFileName`) and rejects paths containing `..` to prevent traversal.

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

## Chat Assistant

The Assistant is a right-edge drawer in the React UI that translates natural-language test-engineering requests into structured actions the user confirms with a click. It reuses the existing LLM wrapper, endpoints, and dispatch infrastructure — it is a routing + presentation layer, not a new execution path.

### Round trip

1. User types a message in the drawer. The client maintains the conversation history in a `ChatContext` (in-memory only, cleared on refresh).
2. `ChatDrawer` POSTs `{ messages[], context: { moduleId?, testSetId? } }` to `/api/chat/message`. The URL-derived `context` lets phrases like "run this" resolve without explicit IDs.
3. `ChatIntentService` builds a **catalog snapshot** from the existing repositories / resolvers: modules + their test sets (with stack/env hints), configured environments, API stacks and modules, Bravo endpoint codes (via `IEndpointResolver.ListCodesAsync` — best-effort), registered agents + capabilities, and — if the request is scoped to a test-set page — that test set's `TestObjectives`. The snapshot is injected into the system prompt alongside the action schema.
4. The LLM is asked to return a single `ChatResponse { reply, actions[] }` JSON. The prompt enumerates every action shape (see below) and forbids inventing IDs/keys that aren't in the catalog. `LlmJsonHelper.DeserializeLlmResponse<ChatResponse>` (shared with `BaseTestAgent`) strips markdown fences and tolerates JSON-with-preamble.
5. The UI renders the reply as an assistant bubble and each action below it as a card.

### Action kinds

| Kind | Payload | Executed by |
|---|---|---|
| `navigate` | `{ path }` | Client — React Router `useNavigate(path)`; drawer closes |
| `showData` | `{ title, data }` | Client — data is already resolved server-side; renders as a table (array of homogeneous objects), bulleted list (array of primitives), or JSON pretty-print |
| `confirmRun` | `{ summary, data: RunRequest }` | Client — Execute button calls `POST /api/runs`, then pushes the returned `runId` into `ActiveRunContext` so the existing run banner + polling pick up the run |
| `confirmCreate` | `{ summary, data: { target: "module"\|"testSet", name, moduleId?, description? } }` | Client — `POST /api/modules` or `POST /api/modules/{id}/testsets` then auto-navigate to the new entity |
| `confirmRecord` | `{ summary, data: { recordingKind, target, moduleId?, testSetId?, caseName?, objectiveId?, verificationName?, waitBeforeSeconds?, deliveryStepIndex?, environmentKey? } }` | Client — dropdown of online agents with matching capability → `POST /api/recordings` → card morphs into a live progress view polling `/api/queue` |

Discovery/read-only intents are resolved server-side during intent parsing (the catalog already has everything), so the action is `showData` with the pre-computed payload. Mutations never auto-execute; every create/run/record requires a card click.

### Why the LLM doesn't call the DB/APIs directly

There is intentionally no tool-calling layer. The server pre-computes the full catalog, prompt-injects it, and relies on the LLM to map the user's phrase to catalog entries. This keeps the loop a single round-trip (no follow-ups), bounds LLM authority (it can only emit actions from a small discriminated union), and avoids needing a separate MCP/function-calling path for every repository shape. The catalog is small — tens of KB in typical deployments — and is rebuilt per request, so it never goes stale.

### Scope boundaries

- **Normal-mode generation is not exposed via chat** — creating test cases from a free-form objective requires an orchestrator run that the chat doesn't trigger. Users still use `Run Objective` on the module page or the CLI.
- **Chat history is client-only** — there is no server-side transcript store. Refreshing the page or clicking the drawer's "clear" button resets the conversation.
- **Recording is dispatch-only** — the chat doesn't execute recording sessions itself; it enqueues them via `/api/recordings` for a user-selected agent. Running a Runner in `--agent` mode is still required.
- **No tool calling / streaming** — the endpoint returns a single JSON response. Streaming and server-side persistence are deferred.

### File map

| File | Purpose |
|---|---|
| `src/AiTestCrew.WebApi/Endpoints/ChatEndpoints.cs` | `POST /api/chat/message` wrapper |
| `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` | Catalog build + system prompt + LLM call + response parse |
| `src/AiTestCrew.WebApi/Models/Chat/ChatModels.cs` | Request/response/action DTOs |
| `src/AiTestCrew.WebApi/Endpoints/RecordingEndpoints.cs` | `POST /api/recordings` — thin wrapper that validates + enqueues |
| `ui/src/contexts/ChatContext.tsx` | Client-held message history + send function |
| `ui/src/components/chat/ChatDrawer.tsx` | Drawer, message list, action-card renderers |
| `ui/src/api/chat.ts` + `ui/src/api/recordings.ts` | API clients |

---

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
