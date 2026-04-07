Reference guide for the Bravo Web (BraveEnergy) application stack. Consult this before modifying the Playwright recorder, replay logic, or selector generation.

This is a **read-only reference** — do not modify this file as part of a task. Use the information here to inform your implementation decisions.

---

## Application stack

Bravo Web is an **ASP.NET MVC** application using:
- **Kendo UI** (jQuery-based) — PanelBar, Window, Menu, Grid, Notification widgets
- **jQuery** + **jQuery UI**
- **Bootstrap** (CSS only, not JS modals — Kendo Windows are used instead)
- Standard **forms authentication** (username/password POST to `/Account/Login`)

Base URL pattern: `https://{host}/Bes.WEB.{Module}/{Feature}?m=Y`

---

## Kendo PanelBar — sidebar menu (`#featureMenu`)

### DOM structure

```html
<ul id="featureMenu" class="k-widget k-panelbar" role="menu">
  <!-- Group header (expand/collapse) -->
  <li class="k-item k-state-highlight k-state-active" role="menuitem">
    <span class="k-link k-header">
      <img class="k-image" src="...">
      <span>Standing Data</span>                <!-- text label user clicks -->
      <span class="k-icon k-panelbar-collapse k-i-arrow-60-up"></span>
    </span>
    <ul class="k-group k-panel" style="display: block;" role="group">
      <!-- Child feature link -->
      <li class="k-item" title="NMI Search" role="menuitem">
        <a class="k-link" href="https://host/Bes.WEB.SDR/NMISearch?m=Y">
          <img class="k-image" src="...">NMI Search
        </a>
      </li>
    </ul>
  </li>
</ul>
```

### Initialisation

```javascript
jQuery("#featureMenu").kendoPanelBar({
    expand: layout.onMenuPanelExpand,
    collapse: layout.onMenuPanelCollapse,
    activate: layout.onMenuActivate,
    animation: false,
    expandMode: "single"
});
```

### Selector rules

| What | Correct selector | Why |
|---|---|---|
| Group header click | `text="Standing Data"` | No `<a>` or `<button>` wrapping the text; only `<span>` elements inside `<span class="k-link k-header">` |
| Child feature link | `li[title="NMI Search"]` or `a[href*="/Bes.WEB.SDR/NMISearch"]` | `title` attribute is stable; `href*=` (contains) matches absolute URLs with `?m=Y` |
| **Never** use | `#featureMenu_pb_active` | Dynamic ID assigned by Kendo to the active panel — changes with user interaction |
| **Never** use | `a[href="/Bes.WEB.SDR/NMISearch"]` (exact match) | Actual href is full absolute URL with `?m=Y` query string — exact match never finds it |
| **Avoid** | `li[role="menuitem"]` | Every item in the PanelBar has `role="menuitem"` — not unique |

### Menu search filter (`#menuSearchTerm`)

- jQuery `keyup` handler filters the PanelBar items
- `FillAsync` alone does NOT trigger filtering — must dispatch `keyup` event explicitly
- After dispatching `keyup`, wait ~500 ms for debounced JS handlers to update the DOM
- Filtered items may be inside collapsed groups that auto-expand during filtering

### Important behaviours

- `expandMode: "single"` — only one group can be expanded at a time
- `<a href="...#">` tags are used as Kendo widget wrappers inside group headers — these are NOT real navigation links; skip them in ancestor traversal
- Child `<ul class="k-group">` has `style="display:none"` when collapsed; children are NOT in the visible DOM until the parent group is expanded

---

## Kendo Window — modals

### DOM structure

```html
<!-- Modal dialog -->
<div class="k-widget k-window" style="...">
  <div class="k-window-titlebar k-header">
    &nbsp;
    <span class="k-window-title" id="someId_wnd_title">WELCOME</span>
    <div class="k-window-actions">
      <a class="k-window-action" href="#" role="button">
        <span class="k-icon k-i-close"></span>
      </a>
    </div>
  </div>
  <div id="someId" class="k-window-content k-content" role="dialog"
       aria-labelledby="someId_wnd_title">
    <!-- dialog content -->
  </div>
</div>

<!-- Backdrop (blocks interaction with page behind) -->
<div class="k-overlay"></div>
```

### Dismissal methods (in order of reliability)

1. **Escape key** — Kendo Windows respond to Escape by default
2. **Close button** — `.k-window-action` containing `.k-icon.k-i-close`
3. **JS removal** — `document.querySelectorAll('.k-overlay').forEach(el => el.remove())` + hide `.k-window`

### Known modals

| Title | When it appears | Impact |
|---|---|---|
| WELCOME | After every fresh login (no cookie) | Blocks all sidebar interaction until dismissed |
| Session Timeout | After idle period | Blocks all interaction |
| Change UI Theme | User-triggered from profile menu | Low impact |

### Replay strategy

`TryDismissOverlaysAsync` in `BaseWebUiTestAgent.cs` handles modal dismissal with three tiers:
1. Press Escape
2. Try close-button selectors (including `.k-window-action`, `[role="dialog"] button`)
3. JS nuclear option — remove `.k-overlay` backdrop, hide `.k-window` and `[role="dialog"]` elements

---

## Login page

| Element | Selector |
|---|---|
| Username field | `#username` |
| Password field | `input[name="Password"]` |
| Submit button | `button[type="submit"]` |
| Remember me | `input[type="checkbox"]` |

After successful login, the page redirects to `/Home/Index` and the WELCOME modal appears.

---

## Kendo Grid — data grids (`.k-grid`)

### DOM structure (hyperlink column)

```html
<div class="k-widget k-grid k-display-block k-reorderable" id="SearchGrid" data-role="grid">
  <div class="k-grid-content" style="height: 578px;">
    <table role="grid" data-role="selectable" class="k-selectable">
      <tbody role="rowgroup">
        <tr data-uid="..." role="row">
          <td role="gridcell">
            <span id="nmicell">
              <a href="https://host/Bes.WEB.SDR/NMISearch?m=Y">5310051118</a>
            </span>
          </td>
          <!-- more cells -->
        </tr>
      </tbody>
    </table>
  </div>
</div>
```

### Dynamic href pattern (critical for recording)

Grid hyperlinks use a **placeholder href** (typically the current page URL) in the HTML. The real navigation URL is set dynamically by a **`mousedown` handler** just before the `click` event fires:

1. Page renders: `<a href="https://host/Bes.WEB.SDR/NMISearch?m=Y">5310051118</a>`
2. User presses mouse button → Kendo `mousedown` JS changes href to `/Bes.WEB.SDR/NMIView/OpenNMI?nmi=5310051118&dsId=1&primarykey=506`
3. `click` event fires → recorder sees the **modified** href
4. Browser navigates to the dynamic URL

**Impact on recording:** The recorder's `click` handler sees the post-mousedown href, but during replay Playwright looks for the element **before** any mousedown fires — the href is still the placeholder, so the selector never matches.

### Selector rules

| What | Correct selector | Why |
|---|---|---|
| Grid hyperlink | `text="5310051118"` | Text content is stable; href is dynamic and unreliable |
| **Never** use | `a[href*="/NMIView/OpenNMI"]` | href is set by mousedown JS — doesn't exist at page load or during replay |
| **Never** use | `span#nmicell` | `id="nmicell"` is repeated on every row — not unique |
| **Avoid** | `td[role="gridcell"]` | Every cell has this role — not unique |

### Recorder implementation

`bestSelector()` in `PlaywrightRecorder.cs` skips href-based selectors for `<a>` tags inside `.k-grid` (`!el.closest('.k-grid')`). This forces fallthrough to text-based selection (`text="5310051118"`), which works for all Kendo Grid hyperlink columns.

---

## Kendo CSS class reference

### State classes (NEVER use for selectors — they change dynamically)

`k-state-active`, `k-state-selected`, `k-state-highlight`, `k-state-hover`,
`k-state-focused`, `k-state-default`, `k-state-disabled`

### Structural classes (avoid — too many matches)

`k-item`, `k-link`, `k-header`, `k-group`, `k-panel`, `k-first`, `k-last`

### Widget type classes (stable, but broad)

`k-widget`, `k-panelbar`, `k-window`, `k-menu`, `k-grid`, `k-dialog`

### Dynamic ID patterns (NEVER use for selectors)

| Pattern | Example | Source |
|---|---|---|
| `*_pb_active` | `featureMenu_pb_active` | Kendo PanelBar active item |
| `*_wnd_title` | `winWelcome_wnd_title` | Kendo Window title span |
| `*_dd_*` | `dropDown_dd_123` | Kendo DropDownList items |

---

## Key files

| File | What it does |
|---|---|
| `src/AiTestCrew.Agents/WebUiBase/PlaywrightRecorder.cs` | Recording — `bestSelector()`, `bestSelectorForPanelBarHeader()`, event listeners |
| `src/AiTestCrew.Agents/WebUiBase/BaseWebUiTestAgent.cs` | Replay — `ExecuteUiStepAsync()`, `TryDismissOverlaysAsync()` |
| `src/AiTestCrew.Agents/WebUiBase/PlaywrightBrowserTools.cs` | LLM exploration — `bestSelector()` in snapshot JS |

---

## Lessons learned

1. **`input` events, not `change`** — for recording fills. `change` fires on blur; ordering with subsequent `click` is unreliable in Chromium.
2. **`click` events, not `mousedown`** — for recording clicks. `mousedown` fires before the previous field's blur/change, causing out-of-order steps.
3. **`href*=` (contains), not `href=` (exact)** — Kendo appends `?m=Y` and uses full absolute URLs.
4. **PanelBar headers need dedicated detection** — `bestSelectorForPanelBarHeader()` walks up to `.k-link.k-header`, finds the text `<span>`, returns `text="GroupName"`.
5. **WELCOME modal after every login** — tests must dismiss it before sidebar interaction. `TryDismissOverlaysAsync` handles this automatically on click failure.
6. **`FillAsync` doesn't trigger `keyup`** — menu search filters won't activate without explicit event dispatch.
7. **Kendo Grid hrefs are dynamic** — Grid `<a>` tags have placeholder hrefs at page load; real URLs are set by `mousedown` JS handlers. The recorder must skip `href`-based selectors for links inside `.k-grid` and use `text="..."` instead. Applies to all Kendo Grid hyperlink columns, not just NMI Search.
