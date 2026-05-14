---
id: REQ-009
title: Scheduled central SQLite backup + documented restore — survive a Docker volume loss
status: Proposed
created: 2026-05-14
author: Kalhara Samarasinghe
area: webapi + ops + docs
---

# REQ-009 — Central database backup & restore

## Goal

Make the central WebApi's SQLite database (modules, test sets, executions, agents, queue, auth-state heartbeats, chat threads, users) survive any single-machine failure — host crash, Docker volume corruption, accidental `docker volume rm`, ransomware on the host — without losing more than the user-configured retention window (default: last 1 hour).

Concretely:

1. **Scheduled hot backup** — a `BackgroundService` (`DatabaseBackupService`) inside the WebApi runs SQLite's Online Backup API on a configurable cadence (default every 30 minutes) into a timestamped file under a configurable backup directory.
2. **Retention policy** — keep last N hourly + last N daily + last N weekly snapshots (cron-style tiered retention; defaults: 24 hourly, 14 daily, 8 weekly). Older files are pruned by the same service.
3. **Manual on-demand backup** — `POST /api/admin/backup` returns the just-created file path (admin-only, gated by a config flag + future role check).
4. **Backup health surface** — `GET /api/admin/backup/status` returns: last successful backup time, size, age, error if last attempt failed. Dashboard panel ("Backup health: green / amber / red") on the homepage.
5. **Documented restore procedure** — `docs/ops/backup-restore.md` with concrete commands: stop container, copy backup over the live DB, start container, verify schema_version, smoke-test.

Today (`src/AiTestCrew.Storage/Sqlite/SqliteConnectionFactory.cs`) the DB is just a file on the Docker volume. WAL mode is on (`DatabaseMigrator.cs:16`) so concurrent reads are safe, but **there is no backup, no snapshot, no off-host copy**. If the volume corrupts, every test set, every recording, every execution history row is gone.

## Why now

- The user is about to share the central WebApi with the rest of the QA team. Right now any disaster on the host PC loses everyone's work. The cost of writing a script *after* losing data is infinite; the cost of writing it now is small.
- SQLite's Online Backup API (`SqliteConnection.BackupDatabase` in `Microsoft.Data.Sqlite`) is purpose-built for this: it streams pages while the source DB is being written, no locking, no downtime.
- The file is currently ~tens of MB at our scale (per-test-set JSON blobs, no large blobs). Backing it up every 30 minutes for the next 24 months costs negligible disk.
- Restore is the *reason* backups exist. The procedure doc is more valuable than the code — without a tested-once doc, the first real recovery attempt will be slow and stressful.

## Current behaviour

- DB file at the path resolved by `SqliteConnectionFactory` (mounted as a Docker volume in production).
- WAL files (`-wal`, `-shm`) co-located. A naive `cp` of the `.db` file while the WebApi is running gives an *inconsistent* copy missing recent WAL writes.
- No snapshot tooling, no off-host replication, no documented restore.
- "Backup" today = "host the docker volume mount on a directory the host backs up nightly", which:
  - misses anything since the last host backup (potentially 24h of work)
  - risks an inconsistent file if the backup tool doesn't checkpoint WAL
  - is invisible to the WebApi (no in-app health check)
  - has no documented restore — recovery is "good luck".

## Scope — what's in

### 1. `DatabaseBackupService` (`BackgroundService`)

`src/AiTestCrew.WebApi/Services/DatabaseBackupService.cs`. Mirrors `AgentHeartbeatMonitor`'s shape. On each tick:

```csharp
var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var dest = Path.Combine(_opts.BackupDirectory, $"aitestcrew-{ts}.db");

using var src = _factory.CreateConnection();    // already open with the live DB
using var dst = new SqliteConnection($"Data Source={dest}");
dst.Open();
src.BackupDatabase(dst);                         // Online Backup API — atomic, no locking
dst.Close();

_lastSuccessAt = DateTime.UtcNow;
_lastSize = new FileInfo(dest).Length;
RunRetentionSweep();
```

Failure surfaces are logged + stored on the service for `/api/admin/backup/status`.

### 2. Tiered retention sweep

Default: 24 hourly + 14 daily + 8 weekly = 46 files at steady state. Keep:
- Newest 24 files (whatever cadence)
- One per day (the oldest taken in that UTC day) for the last 14 days, beyond the hourly cut
- One per week (the oldest taken Mon UTC) for the last 8 weeks, beyond the daily cut

Anything else is `File.Delete`'d. Sweep runs on every tick — cheap (file mtime parse on directory listing).

### 3. Config

`appsettings.json → TestEnvironment.Backup`:

```json
{
  "Enabled": true,
  "Directory": "c:\\backups",         // Windows container path; auto-created if missing
  "IntervalMinutes": 30,
  "RetentionHourly": 24,
  "RetentionDaily": 14,
  "RetentionWeekly": 8,
  "MinFreeDiskMb": 500                // skip backup + log warning if disk under this
}
```

Disabled by default in `appsettings.example.json` (opt-in), enabled in our production config.

**Critical — backup directory MUST be a separate host bind-mount, not a path inside the data volume.** Putting backups in `c:\data\backups` (same volume as the live DB) defeats the stated goal of surviving `docker volume rm aitestcrew_aitestcrew-data` or volume corruption — both would wipe the backups too. Mount a host directory in `docker-compose.yml` alongside the existing `auth-state` / `config` bind-mounts:

```yaml
volumes:
  - ./docker-backups:c:\backups     # bind-mount; survives `docker volume rm`
  - aitestcrew-data:c:\data          # named volume — the live DB
```

This also makes future off-host replication trivial — point `rclone` / `robocopy` / OneDrive at the host's `./docker-backups` directory and you have automatic off-host backups with zero in-app changes.

**Path note** — `/data/backups` (Linux-style) was the original draft default; production runs in a Windows container so paths are `c:\…`. The config value should match the container's OS. For a Linux-container deployment, swap to `/backups` and the equivalent compose mount.

### 4. Admin endpoints

`src/AiTestCrew.WebApi/Endpoints/AdminBackupEndpoints.cs`:

- `POST /api/admin/backup` — trigger an out-of-cycle backup. Returns `{ path, sizeBytes, durationMs }`. 409 if a backup is currently running.
- `GET /api/admin/backup/status` — returns `{ enabled, lastSuccessAt, lastSuccessSizeBytes, lastErrorAt, lastError, nextScheduledAt, totalBackupsOnDisk, oldestBackupAt }`.
- `GET /api/admin/backup/list` — directory listing for an admin-only file picker (paths only, not contents).

Authentication: existing `ApiKeyAuthMiddleware` + a new `IsAdmin` flag on `users` (additive column). For v1, anyone with a valid key can hit these (matches current "any user is admin" model); the column lands now so REQ-008/future role work can flip the predicate without a schema change.

### 5. Dashboard panel

`ui/src/components/BackupHealthPanel.tsx` — homepage tile next to the existing "Startup Data Packs" panel. Polls `/api/admin/backup/status` every 60s. Green: last success <90 min ago. Amber: 90 min – 2 × interval. Red: older, or `lastError` set in last hour. Click → modal with the full status JSON.

### 6. Restore procedure doc

`docs/ops/backup-restore.md` — separate sections for Windows and Linux containers, because the in-container paths and shell built-ins differ. Restore requires container downtime (you can't overwrite a live `.db` while the WebApi has handles on it) but is rare — backup is constant.

**Windows container (our prod):**

```powershell
# 1. Stop the WebApi
docker stop aitestcrew-webapi-1

# 2. Locate the chosen backup on the HOST (because the backup directory is a bind-mount,
#    you can browse it directly — no docker exec needed):
dir .\docker-backups\

# 3. Use a one-shot sidecar (same image, cmd entrypoint) to swap the file in the volume.
#    The data volume isn't reachable from the host without admin; the sidecar mounts it for us.
docker run --rm --entrypoint cmd `
  -v aitestcrew_aitestcrew-data:c:\data `
  -v ${PWD}\docker-backups:c:\backups `
  aitestcrew-webapi /c "ren c:\data\aitestcrew.db aitestcrew.db.broken && del /Q c:\data\aitestcrew.db-wal c:\data\aitestcrew.db-shm && copy /B c:\backups\aitestcrew-YYYYMMDD-HHMMSS.db c:\data\aitestcrew.db"

# 4. Start the container
docker start aitestcrew-webapi-1

# 5. Verify
curl http://localhost:5050/api/health    # → 200
curl http://localhost:5050/api/modules   # → expected modules
```

**Linux container:**

```bash
docker stop aitestcrew-webapi
ls -lh ./docker-backups/
docker run --rm --entrypoint sh \
  -v aitestcrew_aitestcrew-data:/data \
  -v $(pwd)/docker-backups:/backups \
  aitestcrew-webapi -c "mv /data/aitestcrew.db /data/aitestcrew.db.broken && rm -f /data/aitestcrew.db-wal /data/aitestcrew.db-shm && cp /backups/aitestcrew-YYYYMMDD-HHMMSS.db /data/aitestcrew.db"
docker start aitestcrew-webapi
curl http://localhost:5050/api/health && curl http://localhost:5050/api/modules
```

Plus: "exporting a backup file off-host" — because the backup directory is a host bind-mount (see config note above), this is just a normal file copy on the host. No `docker cp` needed.

## Scope — explicitly out

- **Off-host replication / cloud sync.** The backup directory is local to the WebApi container. Pushing to S3/Azure Blob/etc. is a separate REQ — hooking a sidecar process or scheduled `rclone` to the backup directory is straightforward once the local files exist.
- **Point-in-time recovery** (replaying WAL between backups). 30-minute granularity is sufficient for the test-authoring use case. PITR doubles complexity (continuous WAL archive) for a problem we don't have.
- **Encrypted-at-rest backups.** The live DB is unencrypted; encrypting backups while leaving the source plaintext is theatre. Encrypt the volume / use a SQLCipher build as a separate concern.
- **Backup of the auth-state files on agents.** Out of scope — those files are derived (re-runnable via `--auth-setup`) and per-machine.
- **Backup of screenshot/artifact files** stored on the WebApi host. Same directory should be backed up by the same host-level snapshot today; in-app backup of arbitrary file trees is bigger than this REQ.
- **Multi-user role enforcement** beyond the `is_admin` column landing. The endpoints are protected by API key; tightening to admin-only is part of a follow-on roles REQ.

## Acceptance criteria

1. **`DatabaseBackupService` is registered and ticks.** With `Enabled: true` and `IntervalMinutes: 1` in test config, a fresh backup file appears in the configured directory every minute. With `Enabled: false`, no files appear.
2. **Backups are restorable.** Integration test: write a known test set, take a backup, mutate the DB, stop service, swap files (manual fixture step), restart, assert the original test set is back. Done in `DatabaseBackupServiceTests.cs`.
3. **Retention prunes correctly.** Seed the directory with 100 timestamped fake backups spanning 90 days. Run one sweep. Assert exactly `Hourly + Daily + Weekly` files remain and they correspond to the right windows.
4. **Online Backup is non-blocking.** While the backup is running, a parallel test inserts a new test set and reads it back. The insert succeeds; the resulting backup file contains the pre-insert state (acceptable — Online Backup snapshots a consistent point).
5. **Status endpoint is accurate.** After a forced failure (point at a read-only directory), `lastError` is populated and the dashboard panel shows red.
6. **Disk-space guard.** When `MinFreeDiskMb` is greater than available space, the service skips the backup, logs a warning, surfaces the warning on `/api/admin/backup/status`, but does not throw.
7. **Restore doc exists and is tested.** A teammate (or you, 6 weeks from now) can follow `docs/ops/backup-restore.md` from a cold start without further questions.

## Files most likely touched

**New**
- `src/AiTestCrew.WebApi/Services/DatabaseBackupService.cs`
- `src/AiTestCrew.WebApi/Endpoints/AdminBackupEndpoints.cs`
- `src/AiTestCrew.Core/Configuration/BackupConfig.cs`
- `src/AiTestCrew.WebApi.Tests/DatabaseBackupServiceTests.cs`
- `ui/src/components/BackupHealthPanel.tsx`
- `ui/src/api/backupApi.ts`
- `docs/ops/backup-restore.md`

**Modified**
- `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` (add `Backup` block)
- `src/AiTestCrew.WebApi/Program.cs` (DI registration, `MapBackupEndpoints`)
- `src/AiTestCrew.Storage/Sqlite/DatabaseMigrator.cs` (next schema head + 1: add `users.is_admin INTEGER NOT NULL DEFAULT 0`; bootstrap user gets `1`)
- `appsettings.example.json` (Backup block with `Enabled: false`)
- `ui/src/components/Dashboard.tsx` (mount `BackupHealthPanel`)
- `CLAUDE.md` and `docs/architecture.md` (new "Backup & restore" section)
- `docker-compose.yml` (add `./docker-backups:c:\backups` bind-mount)

## Sequencing note

The schema migration here is "current schema head + 1". REQ-008 is currently on a feature branch landing v9 → v10. REQ-009's `users.is_admin` migration should be v10 → v11 if REQ-008 merges first, or v9 → v10 if REQ-008 hasn't landed yet. The implementer should check `DatabaseMigrator.cs`'s current schema version stamp on `main` before assigning a version number — don't hardcode v11 in the requirement.
