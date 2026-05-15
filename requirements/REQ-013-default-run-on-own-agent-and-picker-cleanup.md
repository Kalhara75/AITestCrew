---
id: REQ-013
title: Default "Run on" to the current user's own agent + drop confusing "[Both]" suffix
status: Proposed
created: 2026-05-15
author: Kalhara Samarasinghe
author-note: discovered during multi-user smoke test — picker shows "[Both]" next to every agent (meaningless to QAs) and defaults to "Any execution agent" forcing every QA to pick their own machine each run
area: ui + webapi
related: REQ-010 (agent roles + run pinning), REQ-012 (scoped auth + shared agents)
defers-to: REQ-014 (Central execution pool for scheduled / nightly runs) — explicitly out of scope here
---

# REQ-013 — Default Run-on to own agent; drop "[Both]" suffix

## Goal

The agent picker on a test set / test case row should do the right thing for the 95% case **without** the QA having to touch it:

1. **Default to the QA's own online agent** — when a QA clicks Run, the run should claim on their laptop without them having to expand the dropdown and pick it manually every time.
2. **Drop the `[Both]` / `[Execution]` role suffix from the picker options** — the picker already filters to execution-capable agents, so the role chip in the option text is duplicate information that confuses everyone who hasn't read REQ-010.

This is a UX polish on the REQ-010 picker. The underlying queue / pinning mechanism (target capability filter, optional preferred agent id, claim-deadline fallback) is correct and stays unchanged.

## Why now

REQ-010 shipped the picker to solve the "Alice's laptop got hijacked by Bob's run" problem. It works — but the default of `value = ""` (`AgentPicker.tsx:82`) means every QA on every run-trigger has to:

1. Open the dropdown.
2. Find their own machine in a list that may include teammates' laptops + the central VM.
3. Read past the `[Both]` chip after each name to understand what role they're picking.
4. Click their own laptop.

For a tool that's meant to fade into the background, that's four interactions to do the thing the user wanted by default. Once we have 5+ QAs onboarded, "Any execution agent" stops being a sensible default — it could claim onto a teammate's screen mid-recording (the exact problem REQ-010 set out to prevent, just relocated from claim-side to trigger-side default).

The `[Both]` confusion is the smaller of the two issues but it's the visible one: the user who filed this couldn't tell what "Both" meant without asking. In the picker context every entry is by definition execution-capable, so the chip is signalling the wrong thing.

## Current behaviour

`ui/src/components/AgentPicker.tsx:51-91`:

```tsx
const candidates = (agents ?? []).filter((a) => {
    const isOnline = a.status === 'Online' || a.status === 'Busy';
    const isExecution = a.role === 'Execution' || a.role === 'Both';   // already filters here
    const hasCap = !targetCapability || a.capabilities.includes(targetCapability);
    return isOnline && isExecution && hasCap;
});

return (
    <select value={value ?? ''} ...>
        <option value="">Any execution agent</option>                  // default fallback
        {candidates.map(a => (
            <option key={a.id} value={a.id}>
                {a.name} [{a.role}]{a.tags.length > 0 ? ` (${a.tags.join(', ')})` : ''}   // [Both] noise
            </option>
        ))}
    </select>
);
```

There's no awareness of who the **current user** is, so the picker can't sensibly auto-select.

`AuthContext.tsx:30` already exposes the logged-in user's id — that's the data we need; it just isn't threaded into the picker today.

## Scope — what's in

### 1. Drop the `[Both]` / `[Execution]` suffix from picker options

`AgentPicker.tsx:85` becomes:

```tsx
{candidates.map(a => (
    <option key={a.id} value={a.id}>
        {a.name}{a.userId === me?.id ? ' (you)' : ''}{a.tags.length > 0 ? ` (${a.tags.join(', ')})` : ''}
    </option>
))}
```

- **No role chip.** The picker is already a "Run on" picker — every entry is by definition an execution-capable agent. Showing `[Both]` confused users who think they're picking the role rather than the agent.
- **`(you)` hint** when the agent's `userId` matches the current user. Makes "which one is mine" obvious at a glance, especially once there are 5+ machines in the list.
- **Tags kept.** Tags carry pool-identity information (`ci`, `nightly`) that matters when picking; they were never the source of confusion.

The `RoleChip` component itself stays — it's still rendered correctly on `AgentsPanel.tsx` (the full agent list), where role information is genuinely useful. **Only the picker's per-option chip is removed.**

### 2. Default selection: prefer the current user's online agent

When `value` is `null` on first render AND there's no persisted pin for this test set/case, the picker should pre-select:

- The current user's online, execution-capable agent that advertises `targetCapability`. If multiple, the one with the most recent `lastSeenAt`.
- Fall through to `null` (= "Any execution agent") only when the user has zero matching agents.

Implementation note: this is **picker-side default state**, not a queue-side change. The picker fires `onChange(autoPickedId)` once after `candidates` resolves, the parent's `value` then drives the `<select>`. Persisted pins from REQ-010 take precedence — if the test case row already has `preferredAgentId` saved, that wins.

```tsx
const { user } = useAuth();

useEffect(() => {
    if (value !== null) return;                           // user-set or pinned — leave alone
    if (!candidates.length) return;                       // nothing to pick
    const mine = candidates
        .filter(a => a.userId === user?.id)
        .sort((a, b) => (b.lastSeenAt ?? '').localeCompare(a.lastSeenAt ?? ''));
    if (mine.length > 0) onChange(mine[0].id);
}, [candidates, value, user?.id]);
```

### 3. `AgentSummary` payload carries `userId` + `lastSeenAt`

`GET /api/agents` already returns these (REQ-010 schema). If the TypeScript `AgentSummary` type doesn't surface them, add them to `ui/src/types.ts` and the API client. **No backend change** if the JSON already includes them — verify during planning.

### 4. Hover tooltip on the picker

Small accessibility win: `<select title="...">` showing **"Run claims on this agent. Defaults to your machine when online; falls back to any available execution agent."** so a hovering user can see what the dropdown does without reading docs. One-line change.

### 5. Tests

- **Component test** — `AgentPicker.test.tsx`: with mocked `useAuth` returning userId `alice`, agents `[alice-pc (alice), bob-pc (bob)]`, picker auto-selects `alice-pc`. With agents `[bob-pc (bob)]` only, picker stays on `null`.
- **No backend tests needed** — server contracts unchanged.

## Scope — what's out (parked for REQ-014)

The user explicitly called this out:

> *"when we implement automated execution runs we should be able to nominate central agent pool to use in such executions. I don't mind when you park the central execution into a separate requirement"*

Therefore **NOT in this requirement**:

- **Central / pool nomination for scheduled runs.** When automated nightly runs land (future REQ-014), the scheduler will need a way to pick a pool by tag (e.g. `pool: nightly`) rather than a specific agent id. The picker may grow a "pool" mode at that point. Don't pre-build it.
- **Sticky last-used agent per user.** Could store the last-clicked agent id in `localStorage` and prefer it on next load. Probably nice-to-have but not the user's stated need; defer until someone asks.
- **Showing offline own-machine** as a disabled option ("Your machine is offline — start the agent to run here"). Worth considering but not blocking; leave the current behaviour where offline agents are filtered out.

## Acceptance criteria

1. Opening the agent picker shows option labels of the form `BRLAP110 (you)` or `Kalhara PC (ci, nightly)` — never `BRLAP110 [Both]`.
2. A QA logged in as Dileepa opens a test set with no saved pin. The picker auto-selects `BRLAP110 (you)` (his agent) without him touching it. Clicking Run claims onto BRLAP110, not "Any execution agent".
3. A QA whose own machine is currently offline sees the picker default to "Any execution agent" — same as today's fallback.
4. A test case with a REQ-010 pinned agent id continues to use that pin; the auto-select does not override a persisted preference.
5. When the user manually picks "Any execution agent" from the dropdown, the auto-select does not immediately re-override their choice within the same session.
6. Hovering the dropdown shows a tooltip explaining what the default behaviour is.
7. The `RoleChip` component still renders correctly inside `AgentsPanel.tsx` — this requirement only removes it from picker option labels, not from the main agent list.

## File-level impact preview

| File | Change |
|---|---|
| `ui/src/components/AgentPicker.tsx` | Drop `[role]` from option label; add `(you)` hint; auto-select effect; tooltip |
| `ui/src/types.ts` (or equivalent) | Ensure `AgentSummary` exposes `userId` + `lastSeenAt` (verify; may already) |
| `ui/src/api/agents.ts` | Same verify — type-only change |
| `ui/src/components/AgentPicker.test.tsx` | **NEW** — auto-select cases above |
| `docs/qa-quickstart.md` | One-line update: "Your machine is the default — you only touch the dropdown when you want someone else's." |

No backend changes. No schema changes.

## Done means

- A QA's day-one experience is: open dashboard → click Run → it runs on their laptop. No dropdown wrangling, no `[Both]` Googling.
- The picker still supports the original REQ-010 use cases — pinning to a teammate, picking the central VM (once REQ-014 lands), falling back to "any" when the user has no agent.
- One small, self-contained PR. Backend stays untouched, queue logic stays untouched, REQ-010 promises stay intact.
