using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IAuthRefreshRepository"/>.
/// Dedup-by-scope is enforced by the unique partial index
/// <c>uq_auth_refresh_active_scope</c> — concurrent INSERTs at the same scope
/// race; the loser falls back to returning the existing active row.
/// </summary>
public sealed class SqliteAuthRefreshRepository : IAuthRefreshRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteAuthRefreshRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<AuthRefreshRequest> InsertOrJoinAsync(AuthRefreshRequest r)
    {
        if (string.IsNullOrEmpty(r.Id)) r.Id = Guid.NewGuid().ToString("N")[..12];
        if (r.CreatedAt == default) r.CreatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(r.Status)) r.Status = "Pending";

        using var conn = _factory.CreateConnection();

        // Fast path: existing active row at the same scope wins.
        var existing = await FindActiveByScopeAsync(conn, r.EnvironmentKey, r.Surface, r.ApiStackKey, r.AgentId);
        if (existing is not null) return existing;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO run_auth_refreshes
                    (id, env_key, surface, stack_key, agent_id, requested_by_run_id,
                     status, auto_attempt_count, last_attempt_at, created_at, completed_at, error_message)
                VALUES ($id, $env, $surface, $stack, $agent, $rb, $st, $ac, NULL, $ca, NULL, NULL)
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$env", r.EnvironmentKey);
            cmd.Parameters.AddWithValue("$surface", r.Surface.ToString());
            cmd.Parameters.AddWithValue("$stack", (object?)r.ApiStackKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$agent", (object?)r.AgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rb", (object?)r.RequestedByRunId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", r.Status);
            cmd.Parameters.AddWithValue("$ac", r.AutoAttemptCount);
            cmd.Parameters.AddWithValue("$ca", r.CreatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return r;
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race — return the row that won.
            var winner = await FindActiveByScopeAsync(conn, r.EnvironmentKey, r.Surface, r.ApiStackKey, r.AgentId);
            if (winner is not null) return winner;
            throw;
        }
    }

    public async Task<AuthRefreshRequest?> GetByIdAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task<List<AuthRefreshRequest>> ListActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE status IN ('Pending', 'InProgress') ORDER BY created_at ASC";
        return await ReadListAsync(cmd);
    }

    public async Task MarkInProgressAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_auth_refreshes
            SET status = 'InProgress',
                auto_attempt_count = auto_attempt_count + 1,
                last_attempt_at = $now
            WHERE id = $id AND status IN ('Pending', 'InProgress')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public Task MarkCompletedAsync(string id) => MarkTerminalAsync(id, "Completed", null);

    public Task MarkFailedAsync(string id, string errorMessage) => MarkTerminalAsync(id, "Failed", errorMessage);

    public async Task<bool> CancelAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_auth_refreshes
            SET status = 'Cancelled', completed_at = $now
            WHERE id = $id AND status IN ('Pending', 'InProgress')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<List<AuthRefreshRequest>> ListStaleInProgressAsync(DateTime cutoffUtc)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql +
            " WHERE status = 'InProgress' AND last_attempt_at IS NOT NULL AND last_attempt_at <= $cutoff" +
            " ORDER BY last_attempt_at ASC";
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("O"));
        return await ReadListAsync(cmd);
    }

    public async Task<List<AuthRefreshRequest>> ListRecentlyCompletedAsync(DateTime sinceUtc)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql +
            " WHERE status IN ('Completed', 'Failed') AND completed_at IS NOT NULL AND completed_at >= $since" +
            " ORDER BY completed_at ASC";
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
        return await ReadListAsync(cmd);
    }

    private async Task MarkTerminalAsync(string id, string status, string? error)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_auth_refreshes
            SET status = $st, completed_at = $now, error_message = $err
            WHERE id = $id AND status IN ('Pending', 'InProgress')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<AuthRefreshRequest?> FindActiveByScopeAsync(
        SqliteConnection conn, string envKey, AuthSurface surface, string? stackKey, string? agentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + """
             WHERE status IN ('Pending', 'InProgress')
               AND env_key = $env
               AND surface = $surface
               AND COALESCE(stack_key, '') = COALESCE($stack, '')
               AND COALESCE(agent_id, '') = COALESCE($agent, '')
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$env", envKey);
        cmd.Parameters.AddWithValue("$surface", surface.ToString());
        cmd.Parameters.AddWithValue("$stack", (object?)stackKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", (object?)agentId ?? DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    private static bool IsUniqueViolation(SqliteException ex) =>
        ex.SqliteErrorCode == 19 || ex.SqliteExtendedErrorCode == 2067;

    private const string SelectSql = """
        SELECT id, env_key, surface, stack_key, agent_id, requested_by_run_id,
               status, auto_attempt_count, last_attempt_at, created_at, completed_at, error_message
        FROM run_auth_refreshes
        """;

    private static async Task<List<AuthRefreshRequest>> ReadListAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<AuthRefreshRequest>();
        while (await reader.ReadAsync()) result.Add(Read(reader));
        return result;
    }

    private static AuthRefreshRequest Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        EnvironmentKey = r.GetString(1),
        Surface = Enum.TryParse<AuthSurface>(r.GetString(2), out var s) ? s : AuthSurface.Api,
        ApiStackKey = r.IsDBNull(3) ? null : r.GetString(3),
        AgentId = r.IsDBNull(4) ? null : r.GetString(4),
        RequestedByRunId = r.IsDBNull(5) ? null : r.GetString(5),
        Status = r.GetString(6),
        AutoAttemptCount = r.GetInt32(7),
        LastAttemptAt = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8)).ToUniversalTime(),
        CreatedAt = DateTime.Parse(r.GetString(9)).ToUniversalTime(),
        CompletedAt = r.IsDBNull(10) ? null : DateTime.Parse(r.GetString(10)).ToUniversalTime(),
        ErrorMessage = r.IsDBNull(11) ? null : r.GetString(11),
    };
}
