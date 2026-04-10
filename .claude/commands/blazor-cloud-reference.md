Reference guide for the Brave Cloud (BraveEnergy Blazor/MudBlazor) application stack. Consult this before modifying the Playwright recorder, replay logic, or selector generation for Blazor UI tests.

This is a **read-only reference** — do not modify this file as part of a task. Use the information here to inform your implementation decisions.

---

## Application stack

Brave Cloud is a **Blazor** application using:
- **MudBlazor** — component library (NavGroup, NavLink, DataGrid, Select, DatePicker, Dialog, etc.)
- **Azure AD OpenID SSO** with optional TOTP-based 2FA
- **StorageState** persistence for session reuse across test cases
- Viewport: **1920×1080** (both recording and replay — side menus clip at 720p)

Base URL: `https://sumo-dev.braveenergy.com.au/ui/` (trailing slash matters for subpath routing)

---

## MudNavGroup / MudNavLink — sidebar menu

### DOM structure

```html
<nav class="mud-nav-group" aria-label="Security">
  <button tabindex="0" class="mud-nav-link mud-ripple mud-expanded"
    aria-controls="aai1ro3ql" aria-expanded="true" aria-label="Toggle Security">
    <svg class="mud-icon-root mud-nav-link-icon"><!-- module icon --></svg>
    <div class="mud-nav-link-text">Security</div>
    <svg class="mud-nav-link-expand-icon mud-transform"><!-- chevron --></svg>
  </button>
  <div aria-hidden="false" class="mud-collapse-container mud-collapse-entered mud-navgroup-collapse">
    <div class="mud-collapse-wrapper"><div class="mud-collapse-wrapper-inner">
      <nav class="mud-navmenu mud-navmenu-default">
        <div class="mud-nav-item">
          <a href="./Security/UserSearch" tabindex="0" class="mud-nav-link mud-ripple">
            <div class="mud-nav-link-text">Users</div>
          </a>
        </div>
      </nav>
    </div></div>
  </div>
</nav>
```

### Selector rules

| What | Correct selector | Why |
|---|---|---|
| Group header click | `text="Security"` | Recorder shortcut detects `.mud-nav-link-text` and uses text directly |
| Child nav link | `text="Users"` | Same shortcut — text from `.mud-nav-link-text` |
| **Never** use | `button[type="button"]` | Every MudBlazor button has this — matches dozens of elements |
| **Never** use | `aria-label="Toggle Security"` | Excluded in bestSelector (prefix "Toggle") |
| **Avoid** | `a[href*="/ui/Security/UserSearch"]` | Blazor renders relative hrefs (`./Security/UserSearch`); CSS `[href*=]` matches raw attribute, not resolved URL |

---

## MudButton / MudIconButton

### DOM structure

**Button with text label:**
```html
<button type="button" class="mud-button-root mud-button mud-button-filled mud-button-filled-primary">
  <span class="mud-button-label">Save</span>
</button>
```

**Icon-only button (wrapped in tooltip):**
```html
<div class="mud-tooltip-root mud-tooltip-inline">
  <button type="button" class="mud-button-root mud-icon-button mud-button mud-button-filled mud-button-filled-dark mud-button-filled-size-small">
    <span class="mud-icon-button-label">
      <svg class="mud-icon-root mud-svg-icon"><path d="M15.5 14h-.79l-.28-.27..."></path></svg>
    </span>
  </button>
  <div id="popover-XXXXX" class="mud-popover-cascading-value"></div>
</div>
```

### Selector rules

| What | Correct selector | Why |
|---|---|---|
| Labeled button | `text="Save"` | Recorder detects `.mud-button-label` text |
| Icon button with `aria-label` | `button[aria-label="Notifications"]` | bestSelector priority #4 |
| Icon-only button (no label) | `click-icon` action with SVG path prefix | CSS/XPath can't query SVG `path[d]`; JS evaluation required |
| Same icon multiple times | `click-icon` with `value: "svgPrefix\|1"` | Pipe + index selects Nth occurrence (0-based) |
| **Never** use | `button[type="button"]` | Matches every MudBlazor button |
| **Never** use | `button.mud-button-root` | Matches every MudBlazor button |

### click-icon action format

```json
{ "action": "click-icon", "selector": null, "value": "M15.5 14h-.79l-.28-.27C15.41 1|1", "timeoutMs": 15000 }
```

The value is: `<SVG path prefix (30 chars)>|<occurrence index>`. The replay engine uses `page.EvaluateAsync` with JavaScript `querySelectorAll('svg path')` to find and click the Nth matching button. Polls every 500ms with a timeout (handles SPA navigation delays).

---

## MudDataGrid — data tables

### DOM structure

```html
<div class="mud-table mud-data-grid mud-table-dense mud-table-hover">
  <table class="mud-table-root">
    <thead class="mud-table-head">
      <tr class="mud-table-row">
        <th class="mud-table-cell">
          <span class="sortable-column-header">User Code</span>
          <button aria-label="Sort" class="sort-direction-icon mud-direction-asc"><!-- icon --></button>
          <button aria-label="Column options" class="mud-menu-icon-button-activator"><!-- icon --></button>
        </th>
      </tr>
    </thead>
    <tbody class="mud-table-body">
      <tr class="mud-table-row">
        <td data-label="User Code" class="mud-table-cell">AAnswer</td>
        <td data-label="Actions" class="mud-table-cell sticky-right">
          <div role="group" class="d-flex flex-row gap-3">
            <button class="mud-button-filled-dark"><!-- open icon --></button>
            <button class="mud-button-filled-primary"><!-- add user icon --></button>
          </div>
        </td>
      </tr>
    </tbody>
  </table>
</div>
```

### Selector rules

| What | Correct selector | Why |
|---|---|---|
| Row action button | `tr:has-text("AAnswer") >> td[data-label="Actions"] >> button >> nth=0` | Chained Playwright selector: row context + cell label + button index |
| Sort button | `button[aria-label="Sort"]` | Has aria-label |
| Column options | `button[aria-label="Column options"]` | Has aria-label |
| Cell text | `td[data-label="User Code"]` | `data-label` is stable |
| **Never** use | bare `button` inside `<td>` | Matches every action button in every row |

---

## MudSelect / MudAutocomplete

MudSelect is NOT a native `<select>` — `SelectOptionAsync` does NOT work. Must click to open dropdown, wait for popover, click item by text.

```html
<div class="mud-select mud-autocomplete">
  <input class="mud-select-input" autocomplete="off" type="text">
  <label for="mudinputXXXX">Created By</label>
  <div id="popover-XXXXX" class="mud-popover-cascading-value"></div>
</div>
```

### Interaction sequence

1. Click the `.mud-select-input` or its label
2. Wait for popover to render (DOM mutation)
3. Click the item by `text="ItemValue"` inside the popover

---

## MudDatePicker / MudTimePicker

```html
<div class="mud-picker mud-picker-inline">
  <input placeholder="dd/MM/yyyy" type="text">
  <button aria-label="Open" class="mud-input-adornment-icon-button"><!-- calendar icon --></button>
  <label>Created From</label>
</div>
```

Calendar button has `aria-label="Open"` — recorder captures this. For date entry, prefer filling the input directly over clicking the calendar picker.

---

## MudDialog / MudOverlay

```html
<div class="mud-overlay" style="..."></div>
<div class="mud-dialog-container">
  <div class="mud-dialog">
    <div class="mud-dialog-title">Dialog Title</div>
    <div class="mud-dialog-content"><!-- content --></div>
    <div class="mud-dialog-actions">
      <button>Cancel</button>
      <button>OK</button>
    </div>
  </div>
</div>
```

### Dismissal strategy (in `TryDismissOverlaysAsync`)

1. Press Escape
2. Close buttons: `.mud-dialog button[aria-label='Close']`, `.mud-dialog-actions button`
3. JS nuclear: remove `.mud-overlay`, hide `.mud-dialog-container`, close `.mud-drawer--open.mud-drawer--overlay`

---

## Authentication / StorageState

- Azure SSO flow: navigate to app → redirect to `login.microsoftonline.com` → email → password → optional TOTP → redirect back
- TOTP: `BraveCloudUiTotpSecret` (base32) for automated entry; empty = semi-automated (visible browser, 120s manual timeout)
- Auth state saved to `BraveCloudUiStorageStatePath` (resolved to absolute path at DI startup)
- Valid for `BraveCloudUiStorageStateMaxAgeHours` (default 8)
- `--auth-setup` CLI command: opens visible browser for manual SSO + 2FA, saves state
- StorageState injected into every test case context via `BuildContextOptions()`

---

## SPA navigation timing

Blazor uses client-side routing — clicking a nav link does NOT trigger a full page load. Key implications:

1. **`WaitForLoadStateAsync(NetworkIdle)` resolves too early** — no network requests have started yet when Blazor is still processing the click event
2. **After `click-icon` (JS `btn.click()`)** — Blazor needs ~1s to initiate API calls; check for loading indicators before NetworkIdle
3. **MudBlazor loading indicators**: `.mud-progress-circular`, `.mud-skeleton`, `.mud-table-loading`
4. **`wait-for-stable` action** — uses MutationObserver to wait until DOM has been stable for N ms (default 1000). Use between navigation clicks and element interactions.
5. **Init script survives navigation** — `page.AddInitScriptAsync()` runs on every page load, including Blazor's client-side route changes

---

## MudBlazor CSS class reference

### State classes (NEVER use for selectors — dynamic)

`mud-ripple`, `mud-expanded`, `mud-active`, `mud-selected`, `mud-disabled`, `mud-focused`,
`mud-transform`, `mud-collapse-entered`, `mud-drawer--open`, `mud-direction-asc`

### Structural classes (avoid — too many matches)

`mud-nav-link`, `mud-icon-root`, `mud-svg-icon`, `mud-button-root`, `mud-icon-button`,
`mud-table-cell`, `mud-table-row`

### Component type classes (stable, but broad)

`mud-nav-group`, `mud-navmenu`, `mud-data-grid`, `mud-dialog`, `mud-select`,
`mud-picker`, `mud-tooltip-root`, `mud-menu`

---

## Key files

| File | What it does |
|---|---|
| `src/AiTestCrew.Agents/WebUiBase/PlaywrightRecorder.cs` | Recording — `bestSelector()`, MudBlazor click shortcut, SVG icon fingerprint, post-recording validation |
| `src/AiTestCrew.Agents/WebUiBase/BaseWebUiTestAgent.cs` | Replay — `ExecuteUiStepAsync()`, `click-icon` handler, `wait-for-stable`, `TryDismissOverlaysAsync()` |
| `src/AiTestCrew.Agents/BraveCloudUiAgent/BraveCloudUiTestAgent.cs` | Blazor agent — SSO + TOTP, StorageState, 1920×1080 viewport |

---

## Lessons learned

1. **CSS `[href*=]` matches raw attributes** — Blazor renders relative hrefs (e.g., `./Security/UserSearch`). The recorder must use `getAttribute('href')` directly, not `new URL()` resolution.
2. **`type="button"` is useless** — every MudBlazor button has it. Skip in selector priority.
3. **SVG path attributes need JS, not CSS/XPath** — namespace issues prevent CSS `[d^=]` and XPath `starts-with(@d,...)` from working in HTML documents. Use `page.EvaluateAsync` with `querySelectorAll('svg path')`.
4. **Same SVG icon = same path** — Material Design icons reuse paths. Track occurrence index (`prefix|N`) to disambiguate.
5. **JS `btn.click()` is asynchronous to Blazor** — the click returns before Blazor processes it. Add a delay + loading indicator check before checking NetworkIdle.
6. **`click-icon` must poll** — after SPA navigation, the target button may not exist yet. Retry every 500ms with a 15s timeout.
7. **StorageState paths differ between Runner and WebApi** — always resolve relative paths to absolute at DI startup using the shared data directory.
8. **1920×1080 viewport is essential** — MudBlazor side menus clip at the default 1280×720, causing selectors to time out on elements below the fold.
9. **Trailing slash matters** — `https://host/ui/` and `https://host/ui` are different URLs for Blazor routing. Don't `TrimEnd('/')` on subpaths.
10. **NEVER put MutationObserver in the recording init script** — Playwright's `AddInitScriptAsync` runs before `document.documentElement` exists. Calling `.observe(document.documentElement, ...)` throws and silently crashes the entire IIFE, killing the overlay and all event listeners. MutationObservers belong in the replay path only — inject via a separate `page.AddInitScriptAsync` call after `context.NewPageAsync()` in `BaseWebUiTestAgent`, where the page has a real document.
11. **Recording viewport must be `NoViewport`** — a fixed viewport (e.g. 1920×1080) pushes the `position:fixed` overlay off-screen on monitors smaller than that resolution. Always use `ViewportSize.NoViewport` (maximized window) for recording. The 1920×1080 viewport is only for headless replay (`BraveCloudUiTestAgent.BuildContextOptions`).
