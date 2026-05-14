---
id: REQ-008
title: Concurrent test-set edit protection — ownership, optimistic concurrency, recording locks
status: Proposed
created: 2026-05-14
author: Kalhara Samarasinghe
area: storage + webapi + ui
---

# REQ-008 — Concurrent test-set edit protection

## Goal

Make it safe for multiple QA engineers to author and record against the same central WebApi without silently overwriting each other's work. Concretely:

1. **Track who created / last-edited each module and test set.** Surface this in the dashboard list views so a second editor can see "Alice edited this 3 minutes ago" before they start.
2. **Optimistic concurrency on `PUT /api/test-sets/...` and `PUT /api/modules/...`.** Add a monotonic `version` column; reject writes whose `if-match` version is stale with `409 Conflict + diff hint`, instead of last-writer-wins.
3. **Recording lock per test set.** While a `POST /api/recordings` job is `Queued`, `Claimed`, or `Running` against a test set + objective, block conflicting edits (PUT test set, delete objective, start another recording on the same objective) with a clear error message.

Today (`DatabaseMigrator.cs:29-38`) `test_sets` has no `version`, no `created_by`, no `updated_by`, no lock. Modules are the same. Two QAs editing the same test set produces a silent clobber — the second `PUT` overwrites the first with no warning, no audit trail, no recovery.

## Why now

- We're transitioning from single-user (host's Docker) to multi-user (shared WebApi, federated agents). The fundamental safety net for concurrent editing — optimistic concurrency — does not exist.
- The active-run guard (`RunEndpoints.cs:72-75`) already proves the pattern works for executions ("test set X already has an active run → 409"). Recordings need the same shape: "test set X already has an active recording on objective Y → 409".
- Without this, the first time two QAs collaborate, one will lose work. Reproducing the lost edit by re-recording is expensive (auth, environment setup, UI flow). Failing loudly is cheap.
- Cost is small (~150 lines + one schema bump v9 → v10) and gives us the audit trail (`created_by`, `updated_by`, `updated_at`) we'll want for any future "who broke this test?" investigation.

## Current behaviour

```
QA Alice: GET /api/test-sets/foo → version implicit, 5 objectives
QA Bob:   GET /api/test-sets/foo → same
Alice:    PUT /api/test-sets/foo (adds Objective #6) → 200 OK
Bob:      PUT /api/test-sets/foo (renames Objective #3, has stale view without #6) → 200 OK
                                                                                     └─ Alice's Objective #6 is gone
```

There is no detection, no log entry, no UI warning. The execution-history table preserves past runs but the test-set definition is overwritten in place.

Same story for `POST /api/recordings` followed by a manual PUT: the recording's writeback (Runner finalising a recording into `TestSetRepository.UpsertAsync`) can race a UI save with no detection.

## Scope — what's in

### 1. Schema bump v9 → v10

Add to `test_sets` and `modules`:

```sql
ALTER TABLE test_sets ADD COLUMN version     INTEGER NOT NULL DEFAULT 1;
ALTER TABLE test_sets ADD COLUMN created_by  TEXT;     -- user_id, NULL for pre-migration rows
ALTER TABLE test_sets ADD COLUMN updated_by  TEXT;     -- user_id of last writer
ALTER TABLE test_sets ADD COLUMN updated_at  TEXT NOT NULL DEFAULT (datetime('now'));

ALTER TABLE modules ADD COLUMN version     INTEGER NOT NULL DEFAULT 1;
ALTER TABLE modules ADD COLUMN created_by  TEXT;
ALTER TABLE modules ADD COLUMN updated_by  TEXT;
-- modules already has updated_at
```

New table for active recordings (per-objective lock):

```sql
CREATE TABLE recording_locks (
    module_id      TEXT NOT NULL,
    test_set_id    TEXT NOT NULL,
    objective_id   TEXT,                       -- NULL = locks whole test set (RecordSetup, full re-record)
    job_id         TEXT NOT NULL,              -- run_queue.id
    locked_by      TEXT NOT NULL,              -- user_id
    locked_at      TEXT NOT NULL,
    PRIMARY KEY (module_id, test_set_id, objective_id)
);
```

Janitor (`AgentHeartbeatMonitor`) sweeps stale locks whose `job_id` no longer maps to a non-terminal `run_queue` row — covers crash-without-deregister.

### 2. Optimistic concurrency

`ITestSetRepository.UpsertAsync(PersistedTestSet, int? expectedVersion, string? userId)` — when `expectedVersion` is provided and doesn't match the row's current version, throw `ConcurrencyException`. Bump version on successful write. SQLite-side:

```sql
UPDATE test_sets
SET data = $data, version = version + 1, updated_by = $user, updated_at = $now
WHERE id = $id AND module_id = $mod AND version = $expected;
```

If 0 rows updated → read the current version and throw with both values, so the endpoint can return:

```json
{
  "error": "Test set was modified by another user",
  "currentVersion": 7,
  "yourVersion": 6,
  "currentUpdatedBy": "Alice",
  "currentUpdatedAt": "2026-05-14T03:21:08Z"
}
```

with HTTP `409 Conflict`.

Endpoint changes:
- `PUT /api/test-sets/{moduleId}/{testSetId}` accepts header `If-Match: <version>` (or body field `version`). Same for `PUT /api/modules/{id}`.
- `GET /api/test-sets/...` and the list endpoint surface `version`, `updatedBy`, `updatedAt` (mapped to user *names*, not IDs, by joining `users`).

Frontend (`ui/src/components/TestSetDetail.tsx` and editors): cache the loaded `version`, send it back on save, show a "reload from server" dialog on 409 with a basic diff (which objective IDs differ).

### 3. Recording lock

`POST /api/recordings` (`RecordingEndpoints.cs`): before enqueuing, attempt `INSERT INTO recording_locks (...)`. Unique-constraint violation → `409 Conflict { error: "Recording in progress on this objective by <user>" }`.

Lock release on three paths:
- `JobExecutor` deletes the row when the job finishes (Completed / Failed / Cancelled).
- `POST /api/runs/{id}/cancel` deletes any matching row.
- Janitor sweep: delete locks whose `job_id` is no longer in `run_queue` with status in (`Queued`, `Claimed`, `Running`).

Existing `tracker.HasActiveRunForTestSet` pattern stays — this is a *different* lock (recordings, not executions).

### 4. Read-only awareness in the UI

- Test set list: add columns "Last edited" + "By" + a small pill if a recording lock is active ("Recording: Alice").
- Module list: same.
- Edit dialogs disable the Save button (with a tooltip) when a recording lock for that test set is held by someone else.
- Polling: the existing run-status poller already runs every 5s — piggyback on it to refresh lock state without a new endpoint.

## Scope — explicitly out

- **Pessimistic locking on edit dialogs** ("Alice has the test set open, you cannot edit"). Optimistic + clear 409 is sufficient; pessimistic adds heartbeat complexity and "stale lock from crashed browser" recovery for marginal benefit at the team sizes we're targeting (<15 QAs).
- **Per-objective optimistic concurrency** (sub-test-set versioning). The whole test set is one JSON blob in storage; bumping at the test-set granularity matches the storage model. Re-evaluate if test sets grow beyond ~50 objectives.
- **Diff UI for the 409 case.** A naive "reload and re-apply your changes" works for v1. A real merge UI is out — file as REQ if/when 409s become common.
- **Role-based access** (Alice can edit but Bob can only read). Out — every authenticated user has full edit rights, consistent with current behaviour. Multi-tier roles is a separate REQ.
- **Audit log table** (every change preserved forever). The `updated_by` / `updated_at` pair gives "who broke it" for the last write; full history is a separate REQ. Executions are already preserved in `execution_runs`.

## Acceptance criteria

1. **Schema is v10.** `EnsureSchema` runs cleanly on a v9 DB and stamps `version=1` on every existing row. `created_by` / `updated_by` are NULL for pre-migration rows (treated as "system" in the UI).
2. **Concurrent edit produces 409.** Integration test: two `PUT /api/test-sets/...` against the same row with the same `If-Match` value — one wins, the other gets 409 with a body containing `currentVersion`, `yourVersion`, `currentUpdatedBy`.
3. **Recording lock blocks PUT and second recording.** Test: `POST /api/recordings` against objective X → lock row created. Subsequent `POST /api/recordings` on same objective → 409. `PUT /api/test-sets` covering that objective → 409. Cancel the recording → lock cleared → both succeed.
4. **Janitor reclaims stale locks.** Mark a queue job Completed without releasing the lock (simulating crash); wait one heartbeat tick; assert the lock row is gone.
5. **UI shows last-editor.** Dashboard renders "Last edited by Bob • 3 min ago" on every test set tile.
6. **Existing 219+ tests pass.** Older tests that PUT without `If-Match` continue to work — when the header is absent, the repository fetches current version and uses it (legacy behaviour preserved; the 409 path is opt-in via header).

## Files most likely touched

**New**
- `src/AiTestCrew.Storage/Sqlite/SqliteRecordingLockRepository.cs`
- `src/AiTestCrew.Core/Interfaces/IRecordingLockRepository.cs`
- `src/AiTestCrew.Core/Exceptions/ConcurrencyException.cs`

**Modified**
- `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` (v9 → v10 ALTERs + new table)
- `src/AiTestCrew.Storage/Sqlite/SqliteTestSetRepository.cs`, `SqliteModuleRepository.cs` (versioned upsert)
- `src/AiTestCrew.Storage/Persistence/PersistedTestSet.cs`, `PersistedModule.cs` (expose `Version`, `UpdatedBy`, `UpdatedAt`)
- `src/AiTestCrew.WebApi/Endpoints/TestSetEndpoints.cs`, `ModuleEndpoints.cs`, `RecordingEndpoints.cs` (If-Match handling, 409, lock acquire/release)
- `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` (sweep stale recording locks)
- `src/AiTestCrew.Runner/AgentMode/JobExecutor.cs` (release lock on terminal status)
- `ui/src/components/TestSetList.tsx`, `TestSetDetail.tsx`, `ModuleList.tsx`, `Edit*Dialog.tsx` (version round-trip, lock pill)
- `ui/src/api/testSetApi.ts`, `moduleApi.ts` (send `If-Match`, handle 409)
- `docs/architecture.md` (new section: "Multi-user collaboration: optimistic concurrency + recording locks")
- `docs/functional.md` (note: 409 behaviour, last-edited surface)
