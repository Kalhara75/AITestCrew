using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IAgentAuthStateRepository"/>.
/// Each agent's report is replaced wholesale per heartbeat — keeping the
/// table authoritative for the agent's current view of its storage-state files.
/// Joins on the <c>agents</c> table to filter out Offline agents in the aggregate.
/// </summary>
public sealed class SqliteAgentAuthStateRepository : IAgentAuthStateRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteAgentAuthStateRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task ReplaceForAgentAsync(string agentId, IReadOnlyList<AgentAuthState> entries)
    {
        if (string.IsNullOrEmpty(agentId)) return;

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agent_auth_state WHERE agent_id = $id";
            del.Parameters.AddWithValue("$id", agentId);
            await del.ExecuteNonQueryAsync();
        }

        var now = DateTime.UtcNow.ToString("O");
        foreach (var e in entries)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO agent_auth_state
                    (agent_id, env_key, surface, file_exists, file_mtime_utc, reported_at_utc)
                VALUES ($id, $env, $surface, $exists, $mtime, $now)
                """;
            ins.Parameters.AddWithValue("$id", agentId);
            ins.Parameters.AddWithValue("$env", e.EnvironmentKey);
            ins.Parameters.AddWithValue("$surface", e.Surface.ToString());
            ins.Parameters.AddWithValue("$exists", e.FileExists ? 1 : 0);
            ins.Parameters.AddWithValue("$mtime",
                e.FileMtimeUtc.HasValue ? (object)e.FileMtimeUtc.Value.ToString("O") : DBNull.Value);
            ins.Parameters.AddWithValue("$now", now);
            await ins.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task<List<AgentAuthState>> ListForOnlineAgentsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.agent_id, s.env_key, s.surface, s.file_exists, s.file_mtime_utc, s.reported_at_utc
            FROM agent_auth_state s
            INNER JOIN agents a ON a.id = s.agent_id
            WHERE a.status <> 'Offline'
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<AgentAuthState>();
        while (await reader.ReadAsync())
        {
            result.Add(new AgentAuthState
            {
                AgentId = reader.GetString(0),
                EnvironmentKey = reader.GetString(1),
                Surface = Enum.TryParse<AuthSurface>(reader.GetString(2), out var s) ? s : AuthSurface.WebBlazor,
                FileExists = reader.GetInt32(3) == 1,
                FileMtimeUtc = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
                ReportedAtUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
            });
        }
        return result;
    }

    public async Task DeleteForAgentAsync(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_auth_state WHERE agent_id = $id";
        cmd.Parameters.AddWithValue("$id", agentId);
        await cmd.ExecuteNonQueryAsync();
    }
}
