---
id: REQ-014
title: Central execution pool + scheduled / automated runs
status: Draft (TODO — elaborate before planning)
created: 2026-05-15
author: Kalhara Samarasinghe
author-note: placeholder created as a reminder while REQ-013 was being scoped — central execution is critical for completeness but the design needs a dedicated session to land properly
area: storage + webapi + runner + ui + scheduler
depends-on: REQ-010 (agent tags + pinning), REQ-012 (shared agents + AuthSteward role)
related: REQ-013 (run-trigger picker — defers the pool-nomination UX to here)
---

# REQ-014 — Central execution pool + scheduled runs

> **⚠ This is a stub.** It exists so the idea doesn't get lost while REQ-011 / REQ-012 / REQ-013 ship. Before this requirement goes to `feature-coordinator`, expand each "open question" section into concrete design decisions.

## The need (one paragraph)

QA engineers run tests interactively on their own machines today. For the framework to be production-grade we also need **automated, unattended execution** against a **central pool of shared agents** — typically a dedicated VM (or two) that runs the full suite nightly, on a schedule, without anyone clicking Run. Results land in the dashboard the next morning. When a scheduled run fires, it nominates a pool (by tag, e.g. `pool: nightly`) rather than a specific agent or a specific user's machine — any healthy member of the pool can claim. Failures need to surface clearly enough that an on-call QA can triage them without replaying the whole run interactively.

This is the missing piece between "QAs author + ad-hoc run" (covered today + by REQ-010/012/013) and "the framework actually guards regressions in CI-time."

## Building blocks already in place / coming

| From | What it provides |
|---|---|
| REQ-010 | Agent role (`Recording` / `Execution` / `Both`), free-form `tags` JSON column, run-trigger pinning to a specific agent id |
| REQ-012 | `agents.is_shared` flag for central agents; `User.Role = AuthSteward` for non-admin users who keep central auth state fresh |
| REQ-013 | Note in out-of-scope: *"When automated nightly runs land, the scheduler will need a way to pick a pool by tag rather than a specific agent id. The picker may grow a 'pool' mode at that point."* |

So this requirement does **not** start from zero — the data model for tagging, role-filtering, and ownership is already there.

## Open questions to resolve before planning

These are the things to think through (and write into the requirement) before this becomes implementable:

### Scheduling

- Cron-style triggers (`0 2 * * *`) vs. interval-style (`every 6h`) vs. both?
- Where does the schedule live — new DB table `schedules` with `cron`, `testSetId`, `moduleId`, `pool`, `enabled` columns?
- Who owns a schedule (admin-only? or any user can schedule against pools they're authorised for)?
- Does the WebApi run the scheduler in-process (BackgroundService, like `DatabaseBackupService`)? Or external (Windows Task Scheduler firing a CLI)? In-process is simpler; external survives WebApi restarts more gracefully.

### Pool nomination

- Does the schedule target a single tag (`pool: nightly`) or an expression (`pool: nightly AND capability: UI_Web_Blazor`)?
- What happens when no agent in the pool is online at the scheduled time — fail, retry, queue indefinitely with a deadline (like REQ-010's `QueueClaimDeadlineSeconds`)?
- Pool overlap — can one agent belong to multiple pools (`tags: ["nightly", "smoke"]`)? Probably yes; verify.

### Result surfacing + alerts

- How does a QA find out a nightly run failed — email? Teams / Slack webhook? Dashboard badge? All three?
- Run history retention — does the existing `MaxExecutionRunsPerTestSet` cap apply, or do scheduled runs need a separate retention policy?
- Do we want a "this is the same failure we saw yesterday" dedupe so repeat failures don't spam alerts?

### Pool management UI

- Where does an admin define a pool — separate page, or "tag agents and we'll auto-treat tag-X as pool-X"?
- The REQ-013 picker grows a third option: `Any execution agent` / `<specific agent>` / `Pool: <tag>`. Or do schedules use a different picker entirely and the interactive picker stays at agent-granularity?
- Can a user manually fire a "run against pool X" interactively (handy for "rerun last night's failure on the pool right now")?

### Auth / authority

- Who can create / edit / disable a schedule — `Admin` only? Or `AuthSteward` too (they manage the pool already)? Or any owner of a test set?
- Audit log for "who started / modified this schedule" — small table `schedule_events`?

### Auth-state freshness for unattended runs

- Nightly runs depend on the pool's browser storage state being fresh. REQ-012 introduces `AuthSteward` to keep these refreshed; what's the contract — does the scheduler refuse to start a run when the pool's auth is `Expired`, or does it start and fail loudly?
- Pre-flight alert to the AuthSteward at e.g. `T - 1h` if storage state is expiring — useful or noise? Tied to REQ-012's `Auth.ExpiryWarningHours`.

### CLI parity

- A QA running `--reuse <testset>` interactively today picks an env and runs. A scheduled run does the same thing on a timer. Should `dotnet AiTestCrew.Runner.exe --reuse <testset> --pool nightly` exist as a manual escape hatch (and is that what the in-process scheduler actually invokes under the hood)?

## What this requirement is **not**

- Not a real-time test orchestrator. Scheduled runs are coarse-grained ("run the smoke suite at 2am") not stream-driven.
- Not a CI/CD trigger system. The schedule is time-based; integrating with GitHub Actions / Azure DevOps webhook triggers is a separate concern (and arguably should live in those tools, not inside AITestCrew).
- Not a load-balancer. If two agents are in `pool: nightly`, jobs claim onto whichever picks them first — first-come, first-served. No round-robin, no weighted dispatch.

## When to come back to this

After REQ-011 / 012 / 013 ship and at least one QA other than the author has been onboarded successfully. By then the pain point of "no automated coverage between commits" will be concrete enough that the open questions above resolve themselves.

When you're ready to elaborate this, sit down with:

- The latest agent-table schema (post-REQ-012)
- A list of every test set you'd want to run nightly (gives concrete pool-tag examples)
- Whichever alerting tool the team is on (Teams / Slack / email) — drives the result-surfacing question

Then convert this `Draft` status to `Proposed`, fill in the **Goal / Why now / Scope** sections in the REQ-NNN format used by REQ-010 through REQ-013, and hand it to `feature-coordinator`.
