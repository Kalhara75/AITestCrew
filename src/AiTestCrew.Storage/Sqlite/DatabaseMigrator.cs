using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// Creates and upgrades the SQLite schema. All operations are idempotent.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>Ensures all required tables exist. Safe to call on every startup.</summary>
    public static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();

        // Enable WAL mode for concurrent reads
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS modules (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                data        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS test_sets (
                id          TEXT NOT NULL,
                module_id   TEXT NOT NULL,
                name        TEXT NOT NULL,
                data        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                last_run_at TEXT NOT NULL,
                run_count   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (module_id, id)
            );

            CREATE TABLE IF NOT EXISTS execution_runs (
                run_id       TEXT PRIMARY KEY,
                test_set_id  TEXT NOT NULL,
                module_id    TEXT,
                status       TEXT NOT NULL,
                data         TEXT NOT NULL,
                started_at   TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_execution_runs_test_set
                ON execution_runs (test_set_id, started_at DESC);

            CREATE TABLE IF NOT EXISTS users (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                api_key    TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                is_active  INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS active_runs (
                run_id       TEXT PRIMARY KEY,
                test_set_id  TEXT,
                module_id    TEXT,
                user_id      TEXT,
                objective    TEXT NOT NULL DEFAULT '',
                mode         TEXT NOT NULL DEFAULT '',
                status       TEXT NOT NULL DEFAULT 'Running',
                started_at   TEXT NOT NULL,
                completed_at TEXT,
                error        TEXT
            );

            CREATE TABLE IF NOT EXISTS active_module_runs (
                module_run_id TEXT PRIMARY KEY,
                module_id     TEXT NOT NULL,
                module_name   TEXT NOT NULL DEFAULT '',
                user_id       TEXT,
                status        TEXT NOT NULL DEFAULT 'Running',
                started_at    TEXT NOT NULL,
                completed_at  TEXT,
                error         TEXT,
                data          TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE IF NOT EXISTS agents (
                id                    TEXT PRIMARY KEY,
                name                  TEXT NOT NULL,
                user_id               TEXT,
                capabilities          TEXT NOT NULL,
                version               TEXT,
                status                TEXT NOT NULL,
                last_seen_at          TEXT NOT NULL,
                registered_at         TEXT NOT NULL,
                force_quit_requested  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS run_queue (
                id             TEXT PRIMARY KEY,
                module_id      TEXT NOT NULL,
                test_set_id    TEXT NOT NULL,
                objective_id   TEXT,
                target_type    TEXT NOT NULL,
                mode           TEXT NOT NULL,
                job_kind       TEXT NOT NULL DEFAULT 'Run',
                requested_by   TEXT,
                status         TEXT NOT NULL,
                claimed_by     TEXT,
                claimed_at     TEXT,
                completed_at   TEXT,
                error          TEXT,
                request_json   TEXT NOT NULL,
                created_at     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_run_queue_status_target
                ON run_queue (status, target_type);

            CREATE INDEX IF NOT EXISTS idx_run_queue_claimed_by
                ON run_queue (claimed_by, status);

            CREATE TABLE IF NOT EXISTS schema_version (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_version (key, value) VALUES ('version', '5');
            """;
        cmd.ExecuteNonQuery();

        // ── v3 → v4: add job_kind column to existing run_queue rows ──
        // Idempotent: only runs ALTER when the column is missing.
        if (!ColumnExists(conn, "run_queue", "job_kind"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN job_kind TEXT NOT NULL DEFAULT 'Run'";
            alter.ExecuteNonQuery();
        }

        // ── v4 → v5: add force_quit_requested column to agents ──
        if (!ColumnExists(conn, "agents", "force_quit_requested"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE agents ADD COLUMN force_quit_requested INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }

        // Ensure schema_version reflects the latest applied migration even on upgraded DBs
        using var bump = conn.CreateCommand();
        bump.CommandText = "UPDATE schema_version SET value = '5' WHERE key = 'version' AND CAST(value AS INTEGER) < 5";
        bump.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
