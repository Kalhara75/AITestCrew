---
id: REQ-012
title: Scope auth-refresh prompts to the viewing user + introduce shared "central execution" agents
status: Proposed
created: 2026-05-15
author: Kalhara Samarasinghe
author-note: discovered during multi-user smoke test — Dileepa logged into BRLAP110 saw expired auth tiles for storage states living on Kalhara PC, which he can't physically refresh
area: storage + webapi + runner + ui
related: REQ-010 (agent roles + run pinning), REQ-008 (concurrent edit protection)
---

# REQ-012 — Scoped auth-refresh + shared central agents

## Goal

When a QA logs into the dashboard, the **Auth state needs refreshing** panel must only show storage-state entries they can actually act on:

1. **Their own agents** — the QA can click Refresh, their local browser opens, they re-auth.
2. **Shared "central execution" agents** — visible only to admins (and users explicitly granted the `AuthSteward` role). These are the dedicated VM / CI box that runs nightly executions; they don't belong to any single QA but somebody has to keep their auth state fresh.

A regular QA logged in from a remote machine **never** sees an "Expired — Last refreshed 12d ago on Kalhara PC" tile for a storage state file living on someone else's laptop. They can't unlock that machine, they can't open that browser, the tile is noise at best and a confusing dead-end at worst.

## Why now

Same trigger as REQ-010 — multi-user. The screenshot that motivated this requirement:

> *Dileepa (logged into the dashboard from BRLAP110) sees four expired tiles. All four are for storage state files on "Kalhara PC". The Refresh button doesn't do anything useful for him — clicking it would enqueue a refresh job that only Kalhara's agent can claim, and Kalhara's agent isn't online. The panel is offering an action that can't succeed.*

Without the fix, every new QA we onboard adds N more confusing tiles to everyone else's dashboard. The panel was designed for the single-admin scenario (Kalhara managing his own machine) and never re-scoped when REQ-010 added `agents.user_id`.

The orthogonal need — "central execution agents" — is the **opposite** problem: those agents have **no human owner** by design (they're a shared CI box), so the simple "show only your own" filter would hide them from everyone. We need an explicit shared-ownership flag for these.

## Current behaviour

### Auth-health endpoint reads all agents

`src/AiTestCrew.WebApi/Endpoints/AuthHealthEndpoints.cs:34-36`:

```csharp
var states = await authStateRepo.ListForOnlineAgentsAsync();   // all agents, all users
var activeRefreshes = await refreshRepo.ListActiveAsync();
var agents = (await agentRepo.ListAllAsync()).ToDictionary(a => a.Id);
```

No filter against `HttpContext.Items["User"]`. The `agentReports` list inside each surface explicitly includes every agent that has a row, regardless of ownership.

### Refresh trigger is unauthorised beyond "is logged in"

`src/AiTestCrew.WebApi/Endpoints/AuthRefreshEndpoints.cs` (or equivalent — the endpoint that enqueues a refresh job) checks the API key middleware but not whether the caller owns the agent the refresh would land on. The job gets enqueued; the right agent eventually claims it; if no matching agent is online, it sits in the queue.

### Users have no role

`src/AiTestCrew.Core/Models/User.cs` today:

```csharp
public class User {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
```

No `Role` field, no permissions concept. Everything is implicitly "regular user", which means there's no place to hang "admin can do X that QA can't" today.

### Agents have an owner but no "shared" flag

REQ-010 added `agents.user_id` so an agent is implicitly owned by the user whose API key the Runner used to register. There's no concept of a **shared** agent owned by nobody-in-particular. The current workaround of "register the central VM under the admin's API key" makes the admin look like its owner — which conflates personal-with-central.

## Scope — what's in

### 1. Schema v13: user role + agent shared flag

```sql
-- users: add a fixed-enum role.
ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'User';
-- Values: 'User' | 'AuthSteward' | 'Admin'
-- Bootstrap migration: the first user (by created_at) is promoted to 'Admin'
--   if they're currently 'User'. Subsequent users default to 'User'.

-- agents: add an explicit shared flag, separate from REQ-010's role/tags.
ALTER TABLE agents ADD COLUMN is_shared INTEGER NOT NULL DEFAULT 0;
-- 0 = personal (owned by the registering user)
-- 1 = shared (central execution agent — surfaces to admins + AuthStewards)
```

Defaults preserve current behaviour: existing agents stay personal; existing users (except the bootstrap admin) stay regular Users.

Why two columns instead of one — a single "scope" enum like `Personal | Shared`? Because the **owner identity** (`user_id` from REQ-010) is still meaningful for a shared agent: it tells you who set it up / who to chase when something breaks, even if the visibility rules treat it as collective property.

### 2. `User.Role` semantics

| Role | What they can do |
|---|---|
| `User` (default) | See + manage agents where `agent.user_id == self.id`. See + refresh auth states from those agents only. Cannot mark agents as shared. |
| `AuthSteward` | Everything `User` does, **plus** see shared agents in the auth-health panel and click Refresh on them. Cannot mark agents as shared. |
| `Admin` | Everything `AuthSteward` does, **plus** see + manage all agents regardless of ownership, mark agents as shared (`PUT /api/agents/{id}/shared`), promote / demote user roles (`PUT /api/users/{id}/role`), and delete agents. |

Three roles, fixed, no per-permission ACL. If the team needs finer roles later, that's a separate RFC.

### 3. Auth-health endpoint scoping

`AuthHealthEndpoints.cs` MapGet handler reads `ctx.Items["User"]` (set by `ApiKeyAuthMiddleware`) and filters before grouping:

```csharp
var me = ctx.Items["User"] as User
    ?? throw new UnauthorizedAccessException();

bool VisibleToMe(Agent a) => me.Role switch {
    "Admin"        => true,
    "AuthSteward"  => a.UserId == me.Id || a.IsShared,
    _              => a.UserId == me.Id,
};

var visibleAgentIds = agents.Values.Where(VisibleToMe).Select(a => a.Id).ToHashSet();
var states = (await authStateRepo.ListForOnlineAgentsAsync())
             .Where(s => visibleAgentIds.Contains(s.AgentId))
             .ToList();
```

A surface tile is suppressed when all its `agentReports` are filtered out — extending the existing "drop empty surfaces / drop empty envs" logic at `AuthHealthEndpoints.cs:93-115`.

**Result:** Dileepa (User) on BRLAP110 sees an empty panel (his agent has no expired states yet). Kalhara (Admin) sees everything. A future AuthSteward sees their own machine plus the central VM.

### 4. Refresh trigger scoping

The auth-refresh endpoint (`AuthRefreshEndpoints.cs` — verify exact filename during planning) gains a server-side authorisation check before enqueueing:

```csharp
// The request names an (env, surface). Find the agent that would claim
// the refresh — that's the agent whose storage state we'd overwrite.
var targetAgent = ResolveRefreshTargetAgent(envKey, surface);   // logic already exists
if (!VisibleToMe(targetAgent))
    return Results.Forbid();
```

Without this check, the panel filter is cosmetic — a savvy user could still curl `POST /api/auth/refresh` and trigger a refresh on someone else's machine. Defence in depth.

### 5. Agent registration: `--shared` flag

`src/AiTestCrew.Runner/Program.cs`:

```
--shared                    # mark this agent as a central execution agent
                            # (visible to admins + AuthStewards regardless of owner)
```

Backed by `TestEnvironmentConfig.AgentShared` (bool, default `false`). Threaded into the registration payload (`POST /api/agents/register` or the equivalent register call).

Server-side rule: **only admins can register a shared agent**. If a non-admin's API key is used with `--shared`, the registration call returns 403 with a clear error. This prevents a misconfigured QA laptop from accidentally polluting the shared pool.

Typical install for the central CI VM (admin-issued API key):

```powershell
.\AiTestCrew.Runner.exe --agent --name "CI-VM-01" --shared --role Execution
```

### 6. New admin endpoints

- `PUT /api/agents/{id}/shared` — body `{ "isShared": true | false }`. Admin-only. Lets the admin retroactively mark an existing agent as shared (or unshare one) without redeploying.
- `PUT /api/users/{id}/role` — body `{ "role": "User" | "AuthSteward" | "Admin" }`. Admin-only. Self-demotion is rejected (an admin cannot remove their own admin role unless another admin exists — prevents the "last admin demotes themselves" lockout).
- `GET /api/users/me` already exists (`UserEndpoints.cs:35`); extend the payload to include `role` so the frontend knows what the current user can do.

### 7. UI changes

- **Agent list (Modules dashboard):** shared agents get a "Shared" chip alongside the existing capability chips. Force-quit button is only enabled for own agents (admin sees it on all).
- **Auth health panel:** server already filters, so frontend just renders what it gets. Empty panel state: "All your agents' auth states are fresh." (no scary "0 expired" if there's nothing in scope to begin with).
- **Users page (when REQ-N adds one, or via curl today):** the role column is editable by admins.
- **No new login flow / SSO / etc.** API key + role lookup, full stop.

## Scope — what's out

- **Group / team-based agent ownership.** Shared-or-personal is binary in v1. If the team grows into needing per-team shared pools, that's a separate RFC.
- **Per-permission ACLs.** Three fixed roles. No `auth:refresh:shared`-style granular permissions. Easy to add later if needed; complex to remove if added prematurely.
- **Audit log for who refreshed what.** The existing `run_auth_refreshes` table records the operation; adding a "triggered by user X" column is small but is followup work.
- **SSO / OAuth / external identity.** Out of scope. The API key flow stays as it is.
- **Self-service agent claiming.** A user cannot transfer ownership of an existing agent to themselves. Re-register with their own key if you need to switch owners.

## Acceptance criteria

1. A new `User`-role user logged in from any machine sees auth-health tiles only for `(envKey, surface)` combinations where at least one agent they own has an actionable state. Tiles for storage-state files on someone else's machine never appear.
2. An `Admin` user sees the panel exactly as today — every actionable tile across every agent.
3. An `AuthSteward` user sees their own agents plus any agent with `is_shared = 1`.
4. A non-owner attempting `POST /api/auth/refresh` for an agent they don't own / don't qualify to refresh gets `403 Forbidden` — even when they craft the request manually outside the UI.
5. `--shared` on the Runner refuses to register the agent when the supplied API key belongs to a non-admin user (clear error: *"Only admin users can register a shared agent"*).
6. The bootstrap user (first ever `POST /api/users`) is auto-promoted to `Admin`. Subsequent users start as `User`. Existing users at migration time stay `User` except the chronologically first one.
7. An admin cannot demote themselves via `PUT /api/users/{id}/role` when they are the only remaining admin in the system.
8. The screenshot scenario does not reproduce: Dileepa logged into BRLAP110, with no agents of his own showing expired states, sees no tiles in the auth-health panel.

## File-level impact preview

| File | Change |
|---|---|
| `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` | Schema v12 → v13: add `users.role`, `agents.is_shared`; bootstrap-admin promotion |
| `src/AiTestCrew.Core/Models/User.cs` | Add `Role` (string enum, default `"User"`) |
| `src/AiTestCrew.Core/Models/Agent.cs` | Add `IsShared` (bool) |
| `src/AiTestCrew.Storage/Sqlite/SqliteUserRepository.cs` | Read / write `role` column |
| `src/AiTestCrew.Storage/Sqlite/SqliteAgentRepository.cs` | Read / write `is_shared` column; UPSERT clause |
| `src/AiTestCrew.WebApi/Endpoints/AuthHealthEndpoints.cs` | Scope filter against current user (see snippet above) |
| `src/AiTestCrew.WebApi/Endpoints/AuthRefreshEndpoints.cs` | 403 check before enqueueing |
| `src/AiTestCrew.WebApi/Endpoints/AgentEndpoints.cs` | New `PUT /{id}/shared`; reject `--shared` registration when caller is not Admin |
| `src/AiTestCrew.WebApi/Endpoints/UserEndpoints.cs` | New `PUT /{id}/role`; `/me` returns role; last-admin guard |
| `src/AiTestCrew.WebApi/Middleware/ApiKeyAuthMiddleware.cs` | No change needed — already populates `ctx.Items["User"]` with the full row |
| `src/AiTestCrew.Runner/Program.cs` | `--shared` CLI flag + `AgentShared` config |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Add `AgentShared` (bool, default `false`) |
| `src/AiTestCrew.Runner/appsettings.example.json` | Documented placeholder for `AgentShared` |
| `ui/src/components/AuthHealthPanel.tsx` | Friendlier empty state copy ("All your agents' auth states are fresh") |
| `ui/src/components/AgentList.tsx` (or equivalent) | "Shared" chip; force-quit gating |
| `ui/src/contexts/AuthContext.tsx` | Expose `role` so the frontend can conditionally render admin-only UI |
| `tests/AiTestCrew.WebApi.Tests/AuthHealthEndpointsTests.cs` | **NEW** — verify scoping per role |
| `tests/AiTestCrew.WebApi.Tests/AuthRefreshEndpointsTests.cs` | **NEW** — verify 403 path |
| `tests/AiTestCrew.WebApi.Tests/UserEndpointsTests.cs` | Cover role assignment + last-admin guard |
| `docs/architecture.md` | New short section under Distributed Execution: "User roles + shared agents" |
| `docs/qa-quickstart.md` | One-paragraph mention that auth tiles are scoped to your machine |
| `CLAUDE.md` "Where to extend" table | Row for *"Adding a new user role"* with the touch list |

## Done means

- The screenshot scenario no longer reproduces.
- A team can run nightly executions on a dedicated central VM, and the **one admin** can keep that VM's auth state fresh from any browser — without every QA seeing "Expired 12d ago on CI-VM-01" tiles they can't action.
- The "first user is admin, rest are users" default keeps the bar-to-entry exactly where it is today for single-person setups.
