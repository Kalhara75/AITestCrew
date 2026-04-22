using System.Text.Json;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>SQLite-backed implementation of <see cref="IAgentRepository"/>.</summary>
public sealed class SqliteAgentRepository : IAgentRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteAgentRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<Agent> UpsertAsync(Agent agent)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var now = DateTime.UtcNow;
        var capsJson = JsonSerializer.Serialize(agent.Capabilities, JsonOpts.Value);

        // Upsert clears force_quit_requested: a fresh registration means a fresh process,
        // so any pending force-quit signal from the previous run must not re-fire.
        cmd.CommandText = """
            INSERT INTO agents (id, name, user_id, capabilities, version, status, last_seen_at, registered_at, force_quit_requested)
            VALUES ($id, $name, $userId, $caps, $version, $status, $now, $registered, 0)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                user_id = excluded.user_id,
                capabilities = excluded.capabilities,
                version = excluded.version,
                status = excluded.status,
                last_seen_at = excluded.last_seen_at,
                force_quit_requested = 0
            """;
        cmd.Parameters.AddWithValue("$id", agent.Id);
        cmd.Parameters.AddWithValue("$name", agent.Name);
        cmd.Parameters.AddWithValue("$userId", (object?)agent.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$caps", capsJson);
        cmd.Parameters.AddWithValue("$version", (object?)agent.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", agent.Status);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$registered", (agent.RegisteredAt == default ? now : agent.RegisteredAt).ToString("O"));
        await cmd.ExecuteNonQueryAsync();

        agent.LastSeenAt = now;
        if (agent.RegisteredAt == default) agent.RegisteredAt = now;
        return agent;
    }

    public async Task<Agent?> GetByIdAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task<List<Agent>> ListAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " ORDER BY name ASC";
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Agent>();
        while (await reader.ReadAsync())
            result.Add(Read(reader));
        return result;
    }

    public async Task HeartbeatAsync(string id, string status)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agents SET last_seen_at = $now, status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agents WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> MarkStaleOfflineAsync(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow.Subtract(timeout).ToString("O");
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agents SET status = 'Offline'
            WHERE status != 'Offline' AND last_seen_at < $cutoff
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetForceQuitAsync(string id, bool requested)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // When requested, also mark the agent Offline immediately so the dashboard
        // reflects the intended state without waiting for AgentHeartbeatMonitor to
        // notice the stale heartbeat. The heartbeat endpoint will refuse to bump
        // status back to Online/Busy while the flag is set.
        cmd.CommandText = requested
            ? "UPDATE agents SET force_quit_requested = 1, status = 'Offline' WHERE id = $id"
            : "UPDATE agents SET force_quit_requested = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string SelectSql =
        "SELECT id, name, user_id, capabilities, version, status, last_seen_at, registered_at, force_quit_requested FROM agents";

    private static Agent Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        UserId = r.IsDBNull(2) ? null : r.GetString(2),
        Capabilities = JsonSerializer.Deserialize<List<string>>(r.GetString(3), JsonOpts.Value) ?? new(),
        Version = r.IsDBNull(4) ? null : r.GetString(4),
        Status = r.GetString(5),
        LastSeenAt = DateTime.Parse(r.GetString(6)).ToUniversalTime(),
        RegisteredAt = DateTime.Parse(r.GetString(7)).ToUniversalTime(),
        ForceQuitRequested = r.GetInt32(8) != 0
    };
}
