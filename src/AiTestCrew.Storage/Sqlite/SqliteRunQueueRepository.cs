using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>SQLite-backed implementation of <see cref="IRunQueueRepository"/>.</summary>
public sealed class SqliteRunQueueRepository : IRunQueueRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteRunQueueRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<RunQueueEntry> EnqueueAsync(RunQueueEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString("N")[..12];
        if (entry.CreatedAt == default) entry.CreatedAt = DateTime.UtcNow;
        entry.Status = "Queued";

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_queue (id, module_id, test_set_id, objective_id, target_type, mode, job_kind,
                                   requested_by, status, claimed_by, claimed_at, completed_at, error,
                                   request_json, created_at,
                                   not_before_at, deadline_at, attempt_count, parent_queue_entry_id, parent_run_id)
            VALUES ($id, $moduleId, $tsId, $objId, $target, $mode, $jobKind, $requestedBy, $status,
                    NULL, NULL, NULL, NULL, $requestJson, $createdAt,
                    $notBeforeAt, $deadlineAt, $attemptCount, $parentQueueEntryId, $parentRunId)
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$moduleId", entry.ModuleId);
        cmd.Parameters.AddWithValue("$tsId", entry.TestSetId);
        cmd.Parameters.AddWithValue("$objId", (object?)entry.ObjectiveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$target", entry.TargetType);
        cmd.Parameters.AddWithValue("$mode", entry.Mode);
        cmd.Parameters.AddWithValue("$jobKind", string.IsNullOrWhiteSpace(entry.JobKind) ? "Run" : entry.JobKind);
        cmd.Parameters.AddWithValue("$requestedBy", (object?)entry.RequestedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", entry.Status);
        cmd.Parameters.AddWithValue("$requestJson", entry.RequestJson);
        cmd.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$notBeforeAt",
            entry.NotBeforeAt.HasValue ? entry.NotBeforeAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$deadlineAt",
            entry.DeadlineAt.HasValue ? entry.DeadlineAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$attemptCount", entry.AttemptCount);
        cmd.Parameters.AddWithValue("$parentQueueEntryId", (object?)entry.ParentQueueEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$parentRunId", (object?)entry.ParentRunId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return entry;
    }

    public async Task<RunQueueEntry?> ClaimNextAsync(string agentId, IEnumerable<string> capabilities)
    {
        var caps = capabilities.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToArray();
        if (caps.Length == 0) return null;

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);

        // Find the oldest Queued job with a matching target type AND whose not_before_at has elapsed.
        // not_before_at is ISO-8601 text ("O" round-trip format), so lexicographic compare
        // against UtcNow formatted the same way is a valid chronological compare.
        var nowIso = DateTime.UtcNow.ToString("O");

        using var select = conn.CreateCommand();
        select.Transaction = tx;
        var placeholders = string.Join(",", caps.Select((_, i) => $"$c{i}"));
        select.CommandText = $"""
            SELECT id FROM run_queue
            WHERE status = 'Queued'
              AND target_type IN ({placeholders})
              AND (not_before_at IS NULL OR not_before_at <= $now)
            ORDER BY created_at ASC
            LIMIT 1
            """;
        for (int i = 0; i < caps.Length; i++)
            select.Parameters.AddWithValue($"$c{i}", caps[i]);
        select.Parameters.AddWithValue("$now", nowIso);
        var jobIdObj = await select.ExecuteScalarAsync();
        if (jobIdObj is null or DBNull)
        {
            tx.Rollback();
            return null;
        }
        var jobId = (string)jobIdObj;

        using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE run_queue SET status = 'Claimed', claimed_by = $agent, claimed_at = $now
            WHERE id = $id AND status = 'Queued'
            """;
        update.Parameters.AddWithValue("$agent", agentId);
        update.Parameters.AddWithValue("$now", nowIso);
        update.Parameters.AddWithValue("$id", jobId);
        var rows = await update.ExecuteNonQueryAsync();
        tx.Commit();
        if (rows == 0) return null;

        return await GetByIdAsync(jobId);
    }

    public async Task<RunQueueEntry?> GetByIdAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task MarkRunningAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE run_queue SET status = 'Running' WHERE id = $id AND status = 'Claimed'";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkCompletedAsync(string id, bool success, string? error)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue
            SET status = $status, completed_at = $now, error = $error
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", success ? "Completed" : "Failed");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> CancelAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue SET status = 'Cancelled', completed_at = $now
            WHERE id = $id AND status = 'Queued'
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<int> CancelPendingForRunAsync(string parentRunId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue SET status = 'Cancelled', completed_at = $now
            WHERE parent_run_id = $parentRunId AND status IN ('Queued', 'Claimed')
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$parentRunId", parentRunId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<RunQueueEntry>> ListRecentAsync(int max = 50)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " ORDER BY created_at DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max", max);
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<RunQueueEntry>();
        while (await reader.ReadAsync()) result.Add(Read(reader));
        return result;
    }

    public async Task<RunQueueEntry?> GetActiveForAgentAsync(string agentId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql +
            " WHERE claimed_by = $agent AND status IN ('Claimed', 'Running')" +
            " ORDER BY claimed_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$agent", agentId);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task<List<RunQueueEntry>> ListStaleClaimsAsync(TimeSpan staleAfter)
    {
        var cutoff = DateTime.UtcNow.Subtract(staleAfter).ToString("O");
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql +
            " WHERE status = 'Claimed' AND claimed_at IS NOT NULL AND claimed_at < $cutoff" +
            " ORDER BY claimed_at ASC";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<RunQueueEntry>();
        while (await reader.ReadAsync()) result.Add(Read(reader));
        return result;
    }

    public async Task<bool> ReleaseClaimAsync(string id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue SET status = 'Queued', claimed_by = NULL, claimed_at = NULL
            WHERE id = $id AND status = 'Claimed'
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }

    private const string SelectSql = """
        SELECT id, module_id, test_set_id, objective_id, target_type, mode, job_kind,
               requested_by, status, claimed_by, claimed_at, completed_at, error,
               request_json, created_at,
               not_before_at, deadline_at, attempt_count, parent_queue_entry_id, parent_run_id
        FROM run_queue
        """;

    private static RunQueueEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ModuleId = r.GetString(1),
        TestSetId = r.GetString(2),
        ObjectiveId = r.IsDBNull(3) ? null : r.GetString(3),
        TargetType = r.GetString(4),
        Mode = r.GetString(5),
        JobKind = r.IsDBNull(6) ? "Run" : r.GetString(6),
        RequestedBy = r.IsDBNull(7) ? null : r.GetString(7),
        Status = r.GetString(8),
        ClaimedBy = r.IsDBNull(9) ? null : r.GetString(9),
        ClaimedAt = r.IsDBNull(10) ? null : DateTime.Parse(r.GetString(10)).ToUniversalTime(),
        CompletedAt = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)).ToUniversalTime(),
        Error = r.IsDBNull(12) ? null : r.GetString(12),
        RequestJson = r.GetString(13),
        CreatedAt = DateTime.Parse(r.GetString(14)).ToUniversalTime(),
        NotBeforeAt = r.IsDBNull(15) ? null : DateTime.Parse(r.GetString(15)).ToUniversalTime(),
        DeadlineAt = r.IsDBNull(16) ? null : DateTime.Parse(r.GetString(16)).ToUniversalTime(),
        AttemptCount = r.IsDBNull(17) ? 0 : r.GetInt32(17),
        ParentQueueEntryId = r.IsDBNull(18) ? null : r.GetString(18),
        ParentRunId = r.IsDBNull(19) ? null : r.GetString(19),
    };
}
