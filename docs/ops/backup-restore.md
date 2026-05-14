# AITestCrew - Database Backup and Restore

This document covers:
1. How automatic backups work and where they land
2. Restore from a backup - Windows container (production default)
3. Restore from a backup - Linux container
4. Exporting a backup file off-host
5. Tuning backup settings

---

## How backups work

DatabaseBackupService is a BackgroundService inside the WebApi. On each tick it calls the SQLite Online Backup API (SqliteConnection.BackupDatabase), streaming a consistent page-level snapshot of the live database. The source DB stays fully writable during the copy -- no locks, no downtime.

Backup files land under TestEnvironment.Backup.Directory (default c:\backups in the Windows container). Because this is a **host bind-mount** (not inside the named aitestcrew-data volume) the files survive:

- docker volume rm aitestcrew_aitestcrew-data
- Volume corruption or accidental deletion
- Container recreation

File name format: aitestcrew-YYYYMMDD-HHmmss.db

### Tiered retention

After each backup a sweep prunes old files. Three tiers:

| Tier | Default | What it keeps |
|---|---|---|
| Hourly | 24 | The 24 most-recent backup files, regardless of age |
| Daily | 14 | One file per UTC calendar day beyond the hourly window, up to 14 days |
| Weekly | 8 | One file per Monday-boundary week beyond the daily window, up to 8 weeks |

At steady state at most 46 files are retained. Anything outside these windows is deleted.

### Status dashboard

The homepage has a **Backup Health** tile polling GET /api/admin/backup/status every 60 s.

- **Green** -- last successful backup under 90 minutes ago
- **Amber** -- last successful backup between 90 min and 2x the configured interval
- **Red** -- last backup older than 2x interval, or lastError set in the last hour

Click the tile for: last success time, file size, next scheduled time, total files on disk, oldest backup.
---

## Restore procedure - Windows container (production default)

**Downtime required.** SQLite keeps the DB file open. You cannot swap it while the WebApi is running. Typical restore takes under two minutes.

### Prerequisites

- Access to the host machine running Docker
- Docker Compose project name (default: aitestcrew)
- The ./docker-backups/ folder on the host (bind-mount, no docker exec needed)

### Step 1 - Stop the WebApi

    docker stop aitestcrew-webapi-1
    docker ps --filter name=aitestcrew-webapi-1

The container should no longer appear.

### Step 2 - Choose a backup file

    dir .\docker-backups\

Files are named aitestcrew-YYYYMMDD-HHmmss.db in UTC. Pick the one closest to the time before the data loss event.

### Step 3 - Swap the database using a sidecar container

The live DB is inside Docker volume aitestcrew_aitestcrew-data. Use a one-shot sidecar mounting both the data volume and backup bind-mount.

PowerShell:

    docker run --rm --entrypoint cmd `
      -v aitestcrew_aitestcrew-data:c:\data `
      -v "\{PWD}\docker-backups:c:\backups" `
      aitestcrew-webapi /c "ren c:\data\aitestcrew.db aitestcrew.db.broken && del /Q c:\data\aitestcrew.db-wal c:\data\aitestcrew.db-shm && copy /B c:\backups\aitestcrew-YYYYMMDD-HHMMSS.db c:\data\aitestcrew.db"

Replace aitestcrew-YYYYMMDD-HHMMSS.db with the actual filename from Step 2.

This command: (1) renames the broken live DB to aitestcrew.db.broken (reversible), (2) deletes stale WAL/SHM files, (3) copies the chosen backup into place.

### Step 4 - Start the container

    docker start aitestcrew-webapi-1

### Step 5 - Verify the restore

Wait 5-10 s for startup, then:

    curl http://localhost:5050/api/health
    curl http://localhost:5050/api/modules

Open http://localhost:5050 and confirm your modules and test sets are present.

### Rollback

If the restored DB is also corrupt, aitestcrew.db.broken remains in the volume. Swap back:

    docker stop aitestcrew-webapi-1
    docker run --rm --entrypoint cmd `
      -v aitestcrew_aitestcrew-data:c:\data `
      aitestcrew-webapi /c "del /Q c:\data\aitestcrew.db && ren c:\data\aitestcrew.db.broken aitestcrew.db"
    docker start aitestcrew-webapi-1
---

## Restore procedure - Linux container

The steps are identical in concept; only shell syntax and paths differ.

    docker stop aitestcrew-webapi
    ls -lht ./docker-backups/
    docker run --rm --entrypoint sh \
      -v aitestcrew_aitestcrew-data:/data \
      -v "$(pwd)/docker-backups:/backups" \
      aitestcrew-webapi -c \
      "mv /data/aitestcrew.db /data/aitestcrew.db.broken && rm -f /data/aitestcrew.db-wal /data/aitestcrew.db-shm && cp /backups/aitestcrew-YYYYMMDD-HHMMSS.db /data/aitestcrew.db"
    docker start aitestcrew-webapi
    curl http://localhost:5050/api/health && curl http://localhost:5050/api/modules

---

## Exporting a backup file off-host

Because the backup directory is a host bind-mount, files are already on your host filesystem -- no docker cp or docker exec required.

Windows:

    Copy-Item .\docker-backups\aitestcrew-20260514-120000.db \\nas\aitest-backups\

Linux:

    rsync -av ./docker-backups/ user@backup-server:/aitest-backups/

For continuous off-host replication, point rclone, robocopy, or a cloud sync client at the ./docker-backups/ directory. No in-app changes required.

---

## Tuning backup settings

Edit appsettings.json under TestEnvironment.Backup:

    "Backup": {
      "Enabled": true,
      "Directory": "c:\\backups",
      "IntervalMinutes": 30,
      "RetentionHourly": 24,
      "RetentionDaily": 14,
      "RetentionWeekly": 8,
      "MinFreeDiskMb": 500
    }

| Setting | Purpose |
|---|---|
| Enabled | false disables the service entirely (no ticks, no files) |
| Directory | Absolute container path; must match the bind-mount target in docker-compose.yml |
| IntervalMinutes | How often to take a backup (minimum 1 minute) |
| RetentionHourly | Number of most-recent files to keep unconditionally |
| RetentionDaily | Days of daily coverage beyond the hourly window |
| RetentionWeekly | Weeks of weekly coverage beyond the daily window |
| MinFreeDiskMb | If host disk free space drops below this, backup is skipped and a warning logged. Does not throw. |

Restart the WebApi after changing any of these values.

---

## Triggering an on-demand backup

    curl -X POST http://localhost:5050/api/admin/backup -H "X-Api-Key: <your-api-key>"

Returns HTTP 200 with path, sizeBytes, durationMs on success.
Returns HTTP 409 if a scheduled backup is already in progress.
