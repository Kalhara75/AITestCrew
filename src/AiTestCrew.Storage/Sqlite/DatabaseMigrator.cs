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
                updated_at  TEXT NOT NULL,
                version     INTEGER NOT NULL DEFAULT 1,
                created_by  TEXT,
                updated_by  TEXT
            );

            CREATE TABLE IF NOT EXISTS test_sets (
                id          TEXT NOT NULL,
                module_id   TEXT NOT NULL,
                name        TEXT NOT NULL,
                data        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                last_run_at TEXT NOT NULL,
                run_count   INTEGER NOT NULL DEFAULT 0,
                version     INTEGER NOT NULL DEFAULT 1,
                created_by  TEXT,
                updated_by  TEXT,
                updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
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
                is_active  INTEGER NOT NULL DEFAULT 1,
                is_admin   INTEGER NOT NULL DEFAULT 0
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
                force_quit_requested  INTEGER NOT NULL DEFAULT 0,
                role                  TEXT NOT NULL DEFAULT 'Both',
                tags                  TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE IF NOT EXISTS run_queue (
                id                     TEXT PRIMARY KEY,
                module_id              TEXT NOT NULL,
                test_set_id            TEXT NOT NULL,
                objective_id           TEXT,
                target_type            TEXT NOT NULL,
                mode                   TEXT NOT NULL,
                job_kind               TEXT NOT NULL DEFAULT 'Run',
                requested_by           TEXT,
                status                 TEXT NOT NULL,
                claimed_by             TEXT,
                claimed_at             TEXT,
                completed_at           TEXT,
                error                  TEXT,
                request_json           TEXT NOT NULL,
                created_at             TEXT NOT NULL,
                not_before_at          TEXT,
                deadline_at            TEXT,
                attempt_count          INTEGER NOT NULL DEFAULT 0,
                parent_queue_entry_id  TEXT,
                parent_run_id          TEXT,
                required_tags          TEXT,
                preferred_agent        TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_run_queue_claimed_by
                ON run_queue (claimed_by, status);

            -- Indexes that reference v6 columns are created AFTER the ALTERs below so
            -- that upgrading from v5 doesn't fail on missing columns.

            CREATE TABLE IF NOT EXISTS run_pending_verifications (
                pending_id              TEXT PRIMARY KEY,
                parent_run_id           TEXT NOT NULL,
                current_queue_entry_id  TEXT NOT NULL,
                module_id               TEXT NOT NULL,
                test_set_id             TEXT NOT NULL,
                delivery_objective_id   TEXT NOT NULL,
                first_due_at            TEXT NOT NULL,
                deadline_at             TEXT NOT NULL,
                attempt_count           INTEGER NOT NULL DEFAULT 0,
                status                  TEXT NOT NULL,
                result_json             TEXT,
                attempt_log_json        TEXT,
                created_at              TEXT NOT NULL,
                completed_at            TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_pending_verif_run
                ON run_pending_verifications (parent_run_id, status);

            CREATE INDEX IF NOT EXISTS idx_pending_verif_due
                ON run_pending_verifications (status, first_due_at);

            CREATE TABLE IF NOT EXISTS chat_conversations (
                id            TEXT PRIMARY KEY,
                user_id       TEXT NOT NULL,
                title         TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                message_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_chat_conversations_user
                ON chat_conversations (user_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS chat_messages (
                id              TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                role            TEXT NOT NULL,
                content         TEXT NOT NULL,
                actions_json    TEXT,
                created_at      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_chat_messages_conv
                ON chat_messages (conversation_id, created_at);

            CREATE TABLE IF NOT EXISTS run_auth_refreshes (
                id                  TEXT PRIMARY KEY,
                env_key             TEXT NOT NULL,
                surface             TEXT NOT NULL,
                stack_key           TEXT,
                agent_id            TEXT,
                requested_by_run_id TEXT,
                status              TEXT NOT NULL,
                auto_attempt_count  INTEGER NOT NULL DEFAULT 0,
                last_attempt_at     TEXT,
                created_at          TEXT NOT NULL,
                completed_at        TEXT,
                error_message       TEXT
            );

            -- Dedup-by-scope: at most one Pending or InProgress row per
            -- (env, surface, stack, agent). COALESCE flattens NULLs so the
            -- index treats them as a sentinel rather than 'always distinct'.
            CREATE UNIQUE INDEX IF NOT EXISTS uq_auth_refresh_active_scope
                ON run_auth_refreshes (env_key, surface, COALESCE(stack_key, ''), COALESCE(agent_id, ''))
                WHERE status IN ('Pending', 'InProgress');

            CREATE INDEX IF NOT EXISTS idx_auth_refresh_status
                ON run_auth_refreshes (status, created_at DESC);

            CREATE TABLE IF NOT EXISTS agent_auth_state (
                agent_id        TEXT NOT NULL,
                env_key         TEXT NOT NULL,
                surface         TEXT NOT NULL,
                file_exists     INTEGER NOT NULL,
                file_mtime_utc  TEXT,
                reported_at_utc TEXT NOT NULL,
                PRIMARY KEY (agent_id, env_key, surface)
            );

            CREATE INDEX IF NOT EXISTS idx_agent_auth_state_scope
                ON agent_auth_state (env_key, surface);

            -- v10: per-objective recording lock table.
            CREATE TABLE IF NOT EXISTS recording_locks (
                module_id      TEXT NOT NULL,
                test_set_id    TEXT NOT NULL,
                objective_id   TEXT,
                job_id         TEXT NOT NULL,
                locked_by      TEXT NOT NULL,
                locked_at      TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uix_recording_locks
                ON recording_locks (module_id, test_set_id, COALESCE(objective_id, ''));

            CREATE TABLE IF NOT EXISTS schema_version (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_version (key, value) VALUES ('version', '13');
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

        // ── v5 → v6: deferred verification scheduling fields ──
        if (!ColumnExists(conn, "run_queue", "not_before_at"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN not_before_at TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "run_queue", "deadline_at"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN deadline_at TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "run_queue", "attempt_count"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "run_queue", "parent_queue_entry_id"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN parent_queue_entry_id TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "run_queue", "parent_run_id"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN parent_run_id TEXT";
            alter.ExecuteNonQuery();
        }

        // Ensure the pending-verifications table + indices exist. This block must run
        // AFTER the run_queue ALTERs above so the parent_run_id / not_before_at indexes
        // can reference the columns they index. Idempotent on fresh DBs (where the
        // initial CREATE TABLE block already created the columns).
        using (var createPending = conn.CreateCommand())
        {
            createPending.CommandText = """
                CREATE TABLE IF NOT EXISTS run_pending_verifications (
                    pending_id              TEXT PRIMARY KEY,
                    parent_run_id           TEXT NOT NULL,
                    current_queue_entry_id  TEXT NOT NULL,
                    module_id               TEXT NOT NULL,
                    test_set_id             TEXT NOT NULL,
                    delivery_objective_id   TEXT NOT NULL,
                    first_due_at            TEXT NOT NULL,
                    deadline_at             TEXT NOT NULL,
                    attempt_count           INTEGER NOT NULL DEFAULT 0,
                    status                  TEXT NOT NULL,
                    result_json             TEXT,
                    attempt_log_json        TEXT,
                    created_at              TEXT NOT NULL,
                    completed_at            TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_pending_verif_run
                    ON run_pending_verifications (parent_run_id, status);
                CREATE INDEX IF NOT EXISTS idx_pending_verif_due
                    ON run_pending_verifications (status, first_due_at);
                CREATE INDEX IF NOT EXISTS idx_run_queue_claim
                    ON run_queue (status, target_type, not_before_at);
                CREATE INDEX IF NOT EXISTS idx_run_queue_parent_run
                    ON run_queue (parent_run_id, status);
                """;
            createPending.ExecuteNonQuery();
        }

        // ── v6 → v7: per-user Assistant conversations + messages ──
        // Tables are created above by the initial CREATE TABLE IF NOT EXISTS block,
        // so v6 DBs upgrade cleanly without column ALTERs. Indexes are also covered
        // by the CREATE INDEX IF NOT EXISTS statements in the same block.

        // ── v7 → v8: seamless auth recovery — auth_refresh_id on run_queue ──
        // The CREATE TABLE block above already creates run_auth_refreshes for
        // fresh DBs. For upgraded DBs, the run_queue ALTER below is the only
        // schema change needed beyond the new table.
        if (!ColumnExists(conn, "run_queue", "auth_refresh_id"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN auth_refresh_id TEXT";
            alter.ExecuteNonQuery();
        }

        // Ensure auth-refresh table + index exist on upgraded DBs (CREATE TABLE
        // IF NOT EXISTS on the initial block won't run if the schema_version
        // already advanced past v8 and the DB shape differs — defensive copy).
        using (var createAuthRefresh = conn.CreateCommand())
        {
            createAuthRefresh.CommandText = """
                CREATE TABLE IF NOT EXISTS run_auth_refreshes (
                    id                  TEXT PRIMARY KEY,
                    env_key             TEXT NOT NULL,
                    surface             TEXT NOT NULL,
                    stack_key           TEXT,
                    agent_id            TEXT,
                    requested_by_run_id TEXT,
                    status              TEXT NOT NULL,
                    auto_attempt_count  INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at     TEXT,
                    created_at          TEXT NOT NULL,
                    completed_at        TEXT,
                    error_message       TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS uq_auth_refresh_active_scope
                    ON run_auth_refreshes (env_key, surface, COALESCE(stack_key, ''), COALESCE(agent_id, ''))
                    WHERE status IN ('Pending', 'InProgress');
                CREATE INDEX IF NOT EXISTS idx_auth_refresh_status
                    ON run_auth_refreshes (status, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_run_queue_auth_refresh
                    ON run_queue (auth_refresh_id) WHERE auth_refresh_id IS NOT NULL;
                """;
            createAuthRefresh.ExecuteNonQuery();
        }

        // ── v8 → v9: pre-flight auth health — agent_auth_state ──
        // Fresh DBs get the table from the initial CREATE block above. Upgraded
        // DBs need this defensive create so existing v8 installs gain the table
        // without manual intervention. CREATE TABLE IF NOT EXISTS is idempotent.
        using (var createAuthState = conn.CreateCommand())
        {
            createAuthState.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_auth_state (
                    agent_id        TEXT NOT NULL,
                    env_key         TEXT NOT NULL,
                    surface         TEXT NOT NULL,
                    file_exists     INTEGER NOT NULL,
                    file_mtime_utc  TEXT,
                    reported_at_utc TEXT NOT NULL,
                    PRIMARY KEY (agent_id, env_key, surface)
                );
                CREATE INDEX IF NOT EXISTS idx_agent_auth_state_scope
                    ON agent_auth_state (env_key, surface);
                """;
            createAuthState.ExecuteNonQuery();
        }

        // ── v9 → v10: optimistic concurrency + recording locks ──
        if (!ColumnExists(conn, "test_sets", "version"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE test_sets ADD COLUMN version INTEGER NOT NULL DEFAULT 1";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "test_sets", "created_by"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE test_sets ADD COLUMN created_by TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "test_sets", "updated_by"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE test_sets ADD COLUMN updated_by TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "test_sets", "updated_at"))
        {
            // SQLite's ALTER TABLE ADD COLUMN forbids non-constant defaults, so we
            // add the column nullable and backfill pre-migration rows from created_at.
            // The CREATE TABLE path uses `NOT NULL DEFAULT (datetime('now'))` for fresh DBs;
            // application writes always supply updated_at, so the looser constraint here is safe.
            using (var alter = conn.CreateCommand())
            {
                alter.CommandText = "ALTER TABLE test_sets ADD COLUMN updated_at TEXT";
                alter.ExecuteNonQuery();
            }
            using var backfill = conn.CreateCommand();
            backfill.CommandText = "UPDATE test_sets SET updated_at = created_at WHERE updated_at IS NULL";
            backfill.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "modules", "version"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE modules ADD COLUMN version INTEGER NOT NULL DEFAULT 1";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "modules", "created_by"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE modules ADD COLUMN created_by TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "modules", "updated_by"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE modules ADD COLUMN updated_by TEXT";
            alter.ExecuteNonQuery();
        }

        using (var createLocks = conn.CreateCommand())
        {
            createLocks.CommandText = """
                CREATE TABLE IF NOT EXISTS recording_locks (
                    module_id      TEXT NOT NULL,
                    test_set_id    TEXT NOT NULL,
                    objective_id   TEXT,
                    job_id         TEXT NOT NULL,
                    locked_by      TEXT NOT NULL,
                    locked_at      TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS uix_recording_locks
                    ON recording_locks (module_id, test_set_id, COALESCE(objective_id, ''));
                """;
            createLocks.ExecuteNonQuery();
        }

        // Ensure schema_version reflects the latest applied migration even on upgraded DBs
        using var bump = conn.CreateCommand();
        bump.CommandText = "UPDATE schema_version SET value = '11' WHERE key = 'version' AND CAST(value AS INTEGER) < 11";
        bump.ExecuteNonQuery();

        // ── v10 → v11: users.is_admin ──
        // Additive column landing now so role-based enforcement can flip the
        // predicate later without a schema change. For v1, all existing users are
        // backfilled to is_admin = 1 (matches current "any key holder is admin" policy).
        if (!ColumnExists(conn, "users", "is_admin"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();

            using var backfill = conn.CreateCommand();
            // v1: all existing users are treated as admins (role enforcement is a future REQ)
            backfill.CommandText = "UPDATE users SET is_admin = 1";
            backfill.ExecuteNonQuery();
        }

        // -- v11 -> v12: agent role + tags; queue preferred_agent + required_tags --
        if (!ColumnExists(conn, "agents", "role"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE agents ADD COLUMN role TEXT NOT NULL DEFAULT 'Both'";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "agents", "tags"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE agents ADD COLUMN tags TEXT NOT NULL DEFAULT '[]'";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "run_queue", "required_tags"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN required_tags TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "run_queue", "preferred_agent"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE run_queue ADD COLUMN preferred_agent TEXT";
            alter.ExecuteNonQuery();
        }

        using var bump12 = conn.CreateCommand();
        bump12.CommandText = "UPDATE schema_version SET value = '12' WHERE key = 'version' AND CAST(value AS INTEGER) < 12";
        bump12.ExecuteNonQuery();

        // -- v12 -> v13: user role column + agent is_shared flag --
        // users.role: fixed enum User | AuthSteward | Admin.
        // Bootstrap: the chronologically first user is promoted to Admin so there
        // is always at least one admin on existing deployments.
        // Subsequent users and all future creates default to 'User'.
        if (!ColumnExists(conn, "users", "role"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'User'";
            alter.ExecuteNonQuery();

            // Promote the chronologically first user to Admin (bootstrap admin).
            using var backfill = conn.CreateCommand();
            backfill.CommandText = "UPDATE users SET role = 'Admin' WHERE id = (SELECT id FROM users ORDER BY created_at ASC LIMIT 1)";
            backfill.ExecuteNonQuery();
        }

        // agents.is_shared: 0 = personal (default), 1 = shared central-execution agent.
        if (!ColumnExists(conn, "agents", "is_shared"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE agents ADD COLUMN is_shared INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }

        using var bump13 = conn.CreateCommand();
        bump13.CommandText = "UPDATE schema_version SET value = '13' WHERE key = 'version' AND CAST(value AS INTEGER) < 13";
        bump13.ExecuteNonQuery();
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
