---
id: REQ-016
title: Move informational panels off the Modules page into a dedicated System Health page
status: Proposed
created: 2026-05-17
author: Kalhara Samarasinghe
author-note: the Modules page is the de facto landing page, but right now it stacks four telemetry panels (Auth Health, Agents, Startup Data Packs, Database Backup) above the modules grid. Only Auth Health is genuinely action-required — the others are diagnostic and don't earn permanent real estate on the page every user sees first.
area: ui + information architecture
---

# REQ-016 — Move informational panels off the Modules page into a dedicated System Health page

## Goal

Strip the Modules page back to what its name promises — a grid of modules — and put the system-telemetry panels (Agents, Startup Data Packs, Database Backup) on a new `/system` page that lives behind a header nav link. Action-required content (Auth Refresh banner, Auth Health panel, Queue banner) stays on the Modules page because it blocks the work the user came to do. A small status dot on the System nav link surfaces problems on the moved panels without forcing the user to visit `/system` to notice them.

After this requirement lands, a user who opens AITestCrew at `/` sees:

- A header with `Modules` and `System` nav links. The `System` link shows a coloured dot (green / amber / red) that summarises agent + backup + data-pack health.
- Any active auth-refresh requests as a banner (unchanged).
- Any environments with missing / stale / expiring-soon auth as the existing amber/red Auth Health panel (unchanged when there's a problem; the "All fresh" green confirmation strip is removed from this page — it survives on `/system`).
- Any queued / running runs as the Queue banner (unchanged).
- A heading "Modules" and the modules grid — immediately, without scrolling past three diagnostic panels.

## Why now

The team grew the home page organically over the last six requirements: REQ-009 (backup), REQ-010 (agents panel got role/tag chips), REQ-012 (auth health panel), REQ-014 (queue banner). Each panel earned its place individually, but nobody owned the question "do all four belong here?". Today, on a clean install with one healthy customer environment, the Modules page renders in this order before you see a single module card:

1. Auth Refresh banner — usually hidden, but real estate is reserved
2. Auth Health panel — fold-out warning, or a green "all fresh" strip on calm days
3. Queue banner — usually hidden
4. Agents panel — always visible, even when irrelevant
5. Startup Data Packs panel — always visible, even when nothing has run
6. Backup Health panel — visible whenever backup is enabled, i.e. always in prod

That's six conditional/unconditional strips above the heading "Modules". On a new QA's first login it reads as a dashboard, not a "pick a module" page. On a returning user's daily login it reads as noise — they've already seen these panels are green a hundred times.

The signal-to-noise problem is worst for **Backup** (informational; only matters when red), **Data Packs** (informational; only matters on failure), and **Agents** (useful before running, but doesn't need to be on the landing page — the existing Queue + Agent Picker surface this contextually when you trigger a run). Auth state is the one piece where "visually highlighting on the landing page" is the correct UX, because seeing the amber panel is what reminds a user to refresh before kicking off a run.

The cost of doing nothing is paid every visit by every user. The work is small (move three components, add one page + nav link + status dot) and pays back immediately.

## Current behaviour

`ui/src/pages/ModuleListPage.tsx` renders this order:

```tsx
<AuthRefreshBanner />
<AuthHealthPanel />
<QueueBanner />
<AgentsPanel />
<DataPacksPanel />
<BackupHealthPanel />
<div>{/* "Modules" heading + grid */}</div>
```

Each panel is self-contained — its own react-query subscription, its own hide-when-irrelevant logic. The problem isn't the panels themselves; it's that the Modules page is forced to host all of them because there is no other page to put them on. `ui/src/App.tsx` defines only module-shaped routes (`/`, `/modules/:id`, `/modules/:id/testsets/:id`, ...) plus the assistant pop-out. `ui/src/components/Layout.tsx` has a single nav link, `Modules`, so there is nowhere else for telemetry to live.

The `AuthHealthPanel` already does the right thing when there is no auth issue — it either renders nothing (tiles still loading) or renders a small green strip "All your agents' auth states are fresh." That green strip is helpful on `/system` but redundant on the Modules page once the panel has moved out of the user's daily eyeline.

## Scope — what's in

### 1. New page: System Health (`/system`)

A new route `/system` rendered by a new `ui/src/pages/SystemHealthPage.tsx`. It hosts, in order:

1. **Agents** — the existing `AgentsPanel` component, unchanged.
2. **Startup Data Packs** — the existing `DataPacksPanel` component, unchanged.
3. **Database Backup** — the existing `BackupHealthPanel` component, unchanged.
4. **Auth Health (read-only summary)** — a compact view of every environment's auth state, including the ones currently filtered out of the Modules-page Auth Health panel (i.e. envs where every surface is Fresh). This is the home for the "All fresh" green strip. Users who want to *see* their full auth picture come here; users who need to *act* on it stay on the Modules page where the existing amber/red panel still surfaces problems.

Page header: `System Health`. Subheading: one line explaining what lives here — e.g. "Agents, data packs, backups, and the full auth-state picture across every environment."

No new endpoints. The page reuses the existing react-query keys (`agents`, `dataPackReport`, `backupStatus`, `authHealth`).

### 2. Modules page — strip the three diagnostic panels

`ModuleListPage.tsx` reduces to:

```tsx
<AuthRefreshBanner />
<AuthHealthPanel hideFreshStrip />  // <-- new prop, see §4
<QueueBanner />
<div>{/* "Modules" heading + grid */}</div>
```

The three removed components (`AgentsPanel`, `DataPacksPanel`, `BackupHealthPanel`) are not deleted — they're re-used as-is by `SystemHealthPage`.

### 3. Header nav — add System link with status dot

`Layout.tsx` adds a second nav link beside `Modules`:

```
[ Modules ]   [ System • ]
```

The dot is 8 px, sits to the right of the label, and its colour is the max severity across the three moved panels:

| Source | Green | Amber | Red |
|---|---|---|---|
| Agents | ≥ 1 Online | 0 Online but ≥ 1 Offline registered | (none) |
| Backup | `getTone() === 'green'` | `getTone() === 'amber'` or backup disabled | `getTone() === 'red'` |
| Data Packs | 0 failures and ≥ 1 env Ran | 0 envs ran or all skipped | `totalFailures > 0` |

Final dot = the worst of the three. If everything is green, the dot is green (positive confirmation, not absence). If any source is red, the dot is red. The nav link is `<Link to="/system">` and uses the same active-state styling as the existing Modules link.

Hover tooltip on the dot reads the underlying triple, e.g. `"Agents: green · Backup: amber (last backup 2h ago) · Data Packs: green"`. Pure CSS title attribute is fine; no popover required for this requirement.

The status-dot data comes from the same react-query subscriptions the panels use. Layout invokes the three queries with `staleTime` matching their existing refetch intervals (5 s / 30 s / 60 s) — no new API surface, no extra polling burden beyond what's already there. The `authHealth` query stays on the Modules page where the panel that consumes it lives; the System dot does **not** roll Auth Health into its colour, because Auth Health is already loud on the Modules page and double-surfacing it would dilute the dot.

### 4. AuthHealthPanel — add `hideFreshStrip` prop

The panel currently returns one of three states: amber/red list, green "All fresh" strip, or null. Add an optional `hideFreshStrip?: boolean` prop. When true, the green strip is suppressed and the panel renders null whenever there's nothing to act on.

- Modules page passes `hideFreshStrip`. Result: panel is invisible on calm days, amber/red when something needs attention.
- System Health page does not pass it. Result: panel shows the green "All fresh" confirmation when calm — useful for an ops view.

Implementation is a one-line change in `AuthHealthPanel.tsx` around line 40–48.

## Scope — what's out

- **No redesign of the panels themselves.** Same data, same buttons, same colours. The only panel touched is `AuthHealthPanel` (one prop).
- **No relocation of `AuthRefreshBanner` or `QueueBanner`.** Both are action-required ("a run is paused waiting for auth", "a run is queued"). They stay on the Modules page. Both already hide themselves when there's nothing to show.
- **No real-time push.** The status dot updates on the same react-query refetch cadence as the panels it summarises. The 5 s agents poll is the dominant signal; users will see the dot flip within ~5 s of an agent going offline.
- **No mobile-specific layout work.** Existing pages are desktop-first; this requirement matches that posture.
- **No role-based gating.** Anyone who can see the Modules page can see `/system`. (Force-quit on agents stays as today's check — no new permissions needed.)
- **No telemetry/event logging** for nav-link clicks. Out of scope.

## Acceptance criteria

1. Navigating to `/` shows, in order: optional AuthRefresh banner, optional AuthHealth panel (only when an env needs action), optional Queue banner, the "Modules" heading, the modules grid. The Agents / Data Packs / Backup panels are **not** present on this page.
2. Navigating to `/system` shows a "System Health" heading and, in order: Agents panel, Data Packs panel, Backup panel, Auth Health summary (read-only; "All fresh" green strip is visible on `/system` when calm).
3. The header shows two nav links: `Modules` and `System`. The active link uses the existing active-state styling. Clicking `System` navigates to `/system` and updates the active state; clicking `Modules` returns to `/`.
4. The `System` nav link displays an 8 px coloured dot. With one Online agent, last backup < 90 min ago, and no data-pack failures: dot is green. Force-quit the only agent: dot turns amber within ~5 s. Cause a data-pack failure on the WebApi's next startup scan: dot turns red. Hovering the dot shows a tooltip with the three component states.
5. On the Modules page, when all envs are auth-Fresh, the `AuthHealthPanel` renders nothing (no green strip). On `/system`, the same panel renders the green "All your agents' auth states are fresh" strip.
6. No new API endpoints. No new react-query keys. The three diagnostic panels work identically before and after, just at a new URL.
7. The `Modules` page first-meaningful-paint is no longer pushed down by ~3 panels of telemetry. (Eyeball test — no perf SLA needed.)

## Out of scope (future work)

- A unified "ops console" with logs, queue history, agent metrics, etc. `/system` is the seed; deeper ops tooling is a separate REQ.
- Replacing the dot with a notification dropdown ("3 issues — click for detail"). Reasonable next step if the dot proves too coarse, but not in this REQ.
- Persisting the user's last-visited tab inside `/system` (e.g. deep links to `/system/agents`). The page is short enough to scroll; sub-routes are unnecessary.
