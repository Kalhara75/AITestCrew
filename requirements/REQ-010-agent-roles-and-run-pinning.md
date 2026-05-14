---
id: REQ-010
title: Agent roles + run-trigger pinning — keep execution off personal QA machines
status: Proposed
created: 2026-05-14
author: Kalhara Samarasinghe
author-note: companion to REQ-008 (concurrent editing) and REQ-009 (backup)
area: storage + webapi + runner + ui
---

# REQ-010 — Agent roles & run-trigger pinning

## Goal

Stop the queue from claiming "execute this test run" jobs on QA engineers' personal recording machines. Today, capability strings (`UI_Web_Blazor`, etc.) are the only filter — any registered agent with matching capabilities can win the claim, including the laptop a QA is using to *author* test cases. We need a way to:

1. **Tag agents with a role** (`Recording`, `Execution`, or both) at registration. A QA's laptop registers as `Recording`-only; a CI host or dedicated VM registers as `Execution`-only (or both for small teams).
2. **Make the queue role-aware.** Recordings only claim on `Recording`-capable agents; runs only claim on `Execution`-capable agents. No more accidental hijack.
3. **Allow the run trigger to pin to a specific agent** (or an agent pool tag) — the recording endpoint already supports this (`RecordingEndpoints.cs:84-92`); the run endpoint does not.
4. **Surface roles + pinning in the UI.** Agent list shows role chips. Run trigger gets a dropdown "Run on: any execution agent / agent X / pool Y".

Today: `RunEndpoints.cs:89-101` enqueues with no agent pin and no role filter. The capability strings alone gate claim. A QA mid-recording on Blazor will have their browser hijacked if someone fires a Blazor execution run.

## Why now

- We're going multi-user (REQ-008 + REQ-009). Right now there's effectively one machine and the question is academic — every agent IS the executor and the recorder. As soon as 3+ QAs come online, the laptop-hijack scenario goes from "theoretical" to "first day".
- The infrastructure is mostly there. `AgentRepository` already has `capabilities`; adding a `role` column is a one-line schema bump. The queue's claim query already filters by `target_type`; adding a `role` predicate is the same shape.
- The recording endpoint already validates an optional `AgentId` against capability membership (`RecordingEndpoints.cs:85-91`). Lifting that shape to the run endpoint is straightforward.
- Without this, "centralised execution" (Q5) requires asking every QA to manually `--force-quit` their agent before each scheduled run — not a workflow that survives contact with reality.

## Current behaviour

```
Agent registration:
  POST /api/agents { id, name, capabilities: ["UI_Web_Blazor"], userId: "alice" }
  → row in agents with status=Online

Run trigger:
  POST /api/runs/start { testSetId, ... }
  → RunEndpoints decides to enqueue (UI test)
  → row in run_queue with target_type="UI_Web_Blazor", NO agent_id
  → next /api/queue/next from ANY UI_Web_Blazor agent wins
```

If Alice's laptop is registered with `UI_Web_Blazor` and idle for 1s, her Chrome opens, runs the test, takes screenshots — *while she is recording another test*. The active-run guard (`RunEndpoints.cs:72-75`) prevents two runs of the *same test set* but not "her recording vs someone else's run on a different test set with the same target type".

## Scope — what's in

### 1. Schema v11 → v12: agent role

```sql
ALTER TABLE agents ADD COLUMN role TEXT NOT NULL DEFAULT 'Both';
-- Values: 'Recording' | 'Execution' | 'Both'
-- Default 'Both' preserves current behaviour for any agent registered before upgrade.

ALTER TABLE agents ADD COLUMN tags TEXT NOT NULL DEFAULT '[]';
-- JSON array of free-form pool tags: ["ci", "linux-pool-a"]
```

`Agent` model gains `Role` and `Tags`. `IAgentRepository.UpsertAsync` plumbs both.

### 2. CLI flags + appsettings

`src/AiTestCrew.Runner/Program.cs`:

```
--role Recording | Execution | Both           # default Both — keep back-compat
--tags ci,linux-pool-a                        # optional, comma-separated
```

Backed by `TestEnvironmentConfig.AgentRole` / `AgentTags` so a host can persist its identity without re-passing flags. The QA-laptop typical install becomes:

```
dotnet run --project ... -- --agent --name "ALICE-PC" \
    --capabilities UI_Web_Blazor,UI_Web_MVC \
    --role Recording
```

The dedicated execution VM:

```
dotnet run --project ... -- --agent --name "EXEC-VM-01" \
    --capabilities UI_Web_Blazor,UI_Web_MVC,UI_Desktop_WinForms \
    --role Execution \
    --tags ci,nightly
```

Tags are unsanctioned identity labels — the queue treats them as opaque match strings, no central registry of valid tags.

### 3. Queue claim is role-aware

`SqliteRunQueueRepository.ClaimNextAsync(agentId, capabilities, role, tags)`:

- Look up the calling agent's role + tags from the `agents` row (single source of truth — agents can't lie about their role).
- Recording jobs (`job_kind IN ('Record','RecordSetup','RecordVerification','AuthSetup')`) only match agents with `role IN ('Recording','Both')`.
- Run jobs (`job_kind = 'Run'`, including deferred-verification + verify-only) only match agents with `role IN ('Execution','Both')`.
- Tag filter: if the queue entry has `required_tags` (new column on `run_queue`, nullable), the agent's tag set must be a superset. Empty/null = no tag restriction.

```sql
ALTER TABLE run_queue ADD COLUMN required_tags    TEXT;        -- JSON array; NULL = any
ALTER TABLE run_queue ADD COLUMN preferred_agent  TEXT;        -- agent_id; NULL = any matching
```

When `preferred_agent` is set, only that agent can claim (we keep the `not_before_at` semantics — pinned jobs still respect deferred scheduling). If the preferred agent is offline at deadline, the janitor (configurable `PreferredAgentFallbackPolicy`) either:
- (default) fails the job with a clear error,
- or releases the pin so any role-matching agent can claim.

### 4. Run trigger accepts pinning + tags

`POST /api/runs/start` accepts optional:

```json
{
  "testSetId": "...",
  "preferredAgentId": "exec-vm-01",   // optional — pre-validated like RecordingEndpoints does
  "requiredTags": ["nightly"],        // optional
  "fallbackToAnyAgent": false         // overrides PreferredAgentFallbackPolicy for this call
}
```

Pre-validation mirrors `RecordingEndpoints.cs:85-91`: 400 if `preferredAgentId` isn't registered, doesn't advertise the right capability, or has the wrong role.

`POST /api/recordings` already has `AgentId` — extend it with the same `requiredTags` field for symmetry.

### 5. UI surfaces

- **Agent list** (`AgentList.tsx`): role chip ("Recording", "Execution", "Both") and tag chips.
- **Run trigger button group** (existing) gets a "Run on: ▾" picker. Choices: `Any execution agent` (default), each individual online execution agent by name, plus any tags collected from the agent registry.
- **Recording trigger** picker already exists; extend to honour role filtering (Recording-capable only) instead of pure capability filtering.

### 6. Telemetry

- Heartbeat monitor logs role distribution every 5 min: `online: 3 Recording, 1 Execution, 1 Both`.
- A queue entry's `error` field captures the reason if it expired without being claimed (`No online agent with role=Execution and tags=[nightly]`), instead of sitting `Queued` indefinitely. New `QueueClaimDeadlineSeconds` config (default 600) — sweep on heartbeat tick.

## Scope — explicitly out

- **Capability auto-discovery.** The agent still declares its capabilities; we don't probe the host's installed browsers. Out for v1.
- **A real RBAC matrix** — "this user can only target tag=staging". The `requiredTags` field is a *job* property, not a *user* property. RBAC is a separate REQ.
- **Pool-based load balancing** (round-robin across agents in a tag). The current claim is "oldest queued, first-come-first-served". Adding load-balancing is a separate concern and the existing FIFO is fine for the team sizes we're targeting.
- **Cross-host live migration** (move a Claimed-but-stalled job to another agent in the same pool). The existing stale-claim sweep (`SqliteRunQueueRepository.ListStaleClaimsAsync`) already handles this for unpinned jobs; preferred-agent pins respect the `PreferredAgentFallbackPolicy` knob and that's enough for v1.
- **Renaming `capabilities`** to something more accurate ("target types"). Not worth the breaking change; the capability/role distinction already separates "what can it do?" from "what is it for?".

## Acceptance criteria

1. **Schema is v12.** Existing agents land with `role='Both'` and `tags='[]'` — current users see zero behaviour change unless they opt in to role flags.
2. **Role gating works.** With one Recording-only agent + one Execution-only agent online:
   - `POST /api/recordings { target: UI_Web_Blazor }` → claimed by the Recording agent.
   - `POST /api/runs/start { testSetId for a Blazor objective }` → claimed by the Execution agent.
   - Reverse cases (no matching-role agent online) → job sits `Queued` until `QueueClaimDeadlineSeconds`, then fails with the helpful error message.
3. **Pinning works.** `POST /api/runs/start { preferredAgentId: "exec-vm-01" }` → only `exec-vm-01` can claim, even if other Execution agents are online. Offline + `fallbackToAnyAgent: false` → fails with reason.
4. **Tag filtering works.** `POST /api/runs/start { requiredTags: ["nightly"] }` → only agents whose tag set is a superset of `["nightly"]` are candidates.
5. **CLI flags persist via appsettings.** `--role Recording` overrides config; absent flag falls back to `TestEnvironmentConfig.AgentRole`; absent both falls back to `Both`.
6. **UI dropdown reflects the agent registry.** Empty → "Any execution agent". With agents online → list of names with role + tag chips. Pre-validation runs before submit.
7. **Existing 219+ tests pass.** New tests added for role-gated claim (`SqliteRunQueueRepositoryRoleTests.cs`) and the run-trigger pin path (`RunEndpointsTests.cs` extensions). No existing test needs to be modified because the default role is `Both`.

## Files most likely touched

**New**
- `src/AiTestCrew.WebApi.Tests/SqliteRunQueueRepositoryRoleTests.cs`
- `ui/src/components/AgentPicker.tsx` (reusable for both run + recording trigger)

**Modified**
- `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` (v11 → v12 ALTERs)
- `src/AiTestCrew.Storage/Sqlite/SqliteAgentRepository.cs` (Role, Tags round-trip)
- `src/AiTestCrew.Storage/Sqlite/SqliteRunQueueRepository.cs` (claim query — role + tags + preferred_agent predicates)
- `src/AiTestCrew.Core/Models/Agent.cs` (Role, Tags)
- `src/AiTestCrew.Core/Models/RunQueueEntry.cs` (PreferredAgentId, RequiredTags)
- `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` (AgentRole, AgentTags, QueueClaimDeadlineSeconds, PreferredAgentFallbackPolicy)
- `src/AiTestCrew.Runner/Program.cs` (--role, --tags CLI flags + registration payload)
- `src/AiTestCrew.WebApi/Endpoints/RunEndpoints.cs` (preferredAgentId + requiredTags validation, enqueue plumbing)
- `src/AiTestCrew.WebApi/Endpoints/RecordingEndpoints.cs` (requiredTags symmetry)
- `src/AiTestCrew.WebApi/Endpoints/QueueEndpoints.cs` (role-aware claim — controller side small change)
- `src/AiTestCrew.WebApi/Endpoints/AgentEndpoints.cs` (return role + tags on listing)
- `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` (queue-claim-deadline sweep + role-distribution log line)
- `ui/src/components/AgentList.tsx` (role + tag chips)
- `ui/src/components/RunTestSetButton.tsx` and the recording triggers (mount AgentPicker)
- `ui/src/api/agentApi.ts` (Role, Tags types)
- `docs/architecture.md` (extend "Distributed Execution (Phase 4)" with the role + tag + pinning section)
- `docs/functional.md` (CLI reference for `--role` / `--tags`, run trigger pickers)
- `CLAUDE.md` ("Where to extend" — new row: "Restrict a run to a specific agent / pool")
