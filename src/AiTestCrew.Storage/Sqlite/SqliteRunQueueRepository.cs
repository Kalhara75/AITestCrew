using System.Text.Json;
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
        var requiredTagsJson = entry.RequiredTags != null && entry.RequiredTags.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(entry.RequiredTags) : null;
        cmd.CommandText = """
            INSERT INTO run_queue (id, module_id, test_set_id, objective_id, target_type, mode, job_kind,
                                   requested_by, status, claimed_by, claimed_at, completed_at, error,
                                   request_json, created_at,
                                   not_before_at, deadline_at, attempt_count, parent_queue_entry_id, parent_run_id,
                                   auth_refresh_id, required_tags, preferred_agent)
            VALUES ($id, $moduleId, $tsId, $objId, $target, $mode, $jobKind, $requestedBy, $status,
                    NULL, NULL, NULL, NULL, $requestJson, $createdAt,
                    $notBeforeAt, $deadlineAt, $attemptCount, $parentQueueEntryId, $parentRunId,
                    $authRefreshId, $requiredTags, $preferredAgent)
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
        cmd.Parameters.AddWithValue("$authRefreshId", (object?)entry.AuthRefreshId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$requiredTags", (object?)requiredTagsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preferredAgent", (object?)entry.PreferredAgentId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return entry;
    }

    /// <summary>
    /// Releases queue entries blocked on a now-completed auth refresh: clears
    /// <c>auth_refresh_id</c> and resets <c>not_before_at = now</c> so an agent
    /// picks them back up on the next claim. Returns the number of entries
    /// released.
    /// </summary>
    public async Task<int> ReleaseForAuthRefreshAsync(string authRefreshId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue
            SET auth_refresh_id = NULL, not_before_at = $now
            WHERE auth_refresh_id = $arid AND status = 'Queued'
            """;
        cmd.Parameters.AddWithValue("$arid", authRefreshId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Cancels queue entries blocked on a now-failed auth refresh: marks them
    /// <c>Cancelled</c> with the failure error so the dependent runs finalise
    /// as Failed. Returns the number of entries cancelled.
    /// </summary>
    public async Task<int> CancelForAuthRefreshAsync(string authRefreshId, string error)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue
            SET status = 'Cancelled', completed_at = $now, error = $err
            WHERE auth_refresh_id = $arid AND status = 'Queued'
            """;
        cmd.Parameters.AddWithValue("$arid", authRefreshId);
        cmd.Parameters.AddWithValue("$err", error);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>List queue entries currently blocked on the given auth refresh.</summary>
    public async Task<List<RunQueueEntry>> ListForAuthRefreshAsync(string authRefreshId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE auth_refresh_id = $arid ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("$arid", authRefreshId);
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<RunQueueEntry>();
        while (await reader.ReadAsync()) result.Add(Read(reader));
        return result;
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

        // Look up the calling agent role + tags (agents table is the source of truth).
        string agentRole = "Both";
        List<string> agentTags = new();
        using (var agentLookup = conn.CreateCommand())
        {
            agentLookup.Transaction = tx;
            agentLookup.CommandText = "SELECT role, tags FROM agents WHERE id = $aid";
            agentLookup.Parameters.AddWithValue("$aid", agentId);
            using var ar = await agentLookup.ExecuteReaderAsync();
            if (await ar.ReadAsync())
            {
                agentRole = ar.IsDBNull(0) ? "Both" : ar.GetString(0);
                var tagsRaw = ar.IsDBNull(1) ? null : ar.GetString(1);
                if (!string.IsNullOrEmpty(tagsRaw))
                    agentTags = JsonSerializer.Deserialize<List<string>>(tagsRaw) ?? new();
            }
        }

        // Role filter: recording-only agents skip Run jobs; execution-only agents skip recording jobs.
        var recordKinds = new[] { "Record", "RecordSetup", "RecordVerification", "AuthSetup" };
        bool canRecording = agentRole == "Recording" || agentRole == "Both";
        bool canExecution = agentRole == "Execution" || agentRole == "Both";

        var placeholders = string.Join(",", caps.Select((_, i) => $"$c{i}"));
        using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = $"""
            SELECT id, job_kind, required_tags, preferred_agent FROM run_queue
            WHERE status = 'Queued'
              AND target_type IN ({placeholders})
              AND (not_before_at IS NULL OR not_before_at <= $now)
            ORDER BY created_at ASC
            """;
        for (int i = 0; i < caps.Length; i++)
            select.Parameters.AddWithValue($"$c{i}", caps[i]);
        select.Parameters.AddWithValue("$now", nowIso);

        string? jobId = null;
        using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var candidateId = reader.GetString(0);
                var jobKind = reader.IsDBNull(1) ? "Run" : reader.GetString(1);
                var reqTagsJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                var prefAgent = reader.IsDBNull(3) ? null : reader.GetString(3);

                // Preferred-agent pin: only the named agent may claim this entry.
                if (!string.IsNullOrEmpty(prefAgent) && prefAgent != agentId)
                    continue;

                // Role filter
                bool isRecordKind = recordKinds.Contains(jobKind, StringComparer.OrdinalIgnoreCase);
                if (isRecordKind && !canRecording) continue;
                if (!isRecordKind && !canExecution) continue;

                // Required-tags superset filter: agent tags must contain all required tags.
                if (!string.IsNullOrEmpty(reqTagsJson))
                {
                    var reqTags = JsonSerializer.Deserialize<List<string>>(reqTagsJson) ?? new();
                    if (reqTags.Count > 0 && !reqTags.All(t => agentTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                        continue;
                }

                jobId = candidateId;
                break;
            }
        }

        if (jobId is null)
        {
            tx.Rollback();
            return null;
        }

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

    /// <summary>
    /// Marks Queued entries older than <paramref name="cutoff"/> as Failed.
    /// Called by the janitor to fail jobs whose claim deadline has elapsed with no agent.
    /// </summary>
    public async Task<int> ExpireUnclaimedAsync(DateTime cutoff, string errorMessage)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_queue
            SET status = 'Failed', completed_at = $now, error = $err
            WHERE status = 'Queued' AND created_at <= $cutoff
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$err", errorMessage);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return await cmd.ExecuteNonQueryAsync();
    }

    private const string SelectSql = """
        SELECT id, module_id, test_set_id, objective_id, target_type, mode, job_kind,
               requested_by, status, claimed_by, claimed_at, completed_at, error,
               request_json, created_at,
               not_before_at, deadline_at, attempt_count, parent_queue_entry_id, parent_run_id,
               auth_refresh_id, required_tags, preferred_agent
        FROM run_queue
        """;

    private static RunQueueEntry Read(SqliteDataReader r)
    {
        var reqTagsRaw = r.IsDBNull(21) ? null : r.GetString(21);
        return new RunQueueEntry
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
            AuthRefreshId = r.IsDBNull(20) ? null : r.GetString(20),
            RequiredTags = string.IsNullOrEmpty(reqTagsRaw) ? new() : (JsonSerializer.Deserialize<List<string>>(reqTagsRaw) ?? new()),
            PreferredAgentId = r.IsDBNull(22) ? null : r.GetString(22),
        };
    }
}
