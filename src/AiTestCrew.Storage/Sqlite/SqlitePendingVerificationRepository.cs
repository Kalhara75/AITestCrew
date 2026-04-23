using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using Microsoft.Data.Sqlite;

namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>SQLite-backed implementation of <see cref="IPendingVerificationRepository"/>.</summary>
public sealed class SqlitePendingVerificationRepository : IPendingVerificationRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqlitePendingVerificationRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(PendingVerification p)
    {
        if (p.CreatedAt == default) p.CreatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(p.Status)) p.Status = "Pending";

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_pending_verifications
                (pending_id, parent_run_id, current_queue_entry_id, module_id, test_set_id,
                 delivery_objective_id, first_due_at, deadline_at, attempt_count, status,
                 result_json, attempt_log_json, created_at, completed_at)
            VALUES ($pid, $prid, $cqid, $mid, $tsid, $doid, $fdue, $dl, $ac, $st,
                    $rj, $alj, $ca, NULL)
            """;
        cmd.Parameters.AddWithValue("$pid", p.PendingId);
        cmd.Parameters.AddWithValue("$prid", p.ParentRunId);
        cmd.Parameters.AddWithValue("$cqid", p.CurrentQueueEntryId);
        cmd.Parameters.AddWithValue("$mid", p.ModuleId);
        cmd.Parameters.AddWithValue("$tsid", p.TestSetId);
        cmd.Parameters.AddWithValue("$doid", p.DeliveryObjectiveId);
        cmd.Parameters.AddWithValue("$fdue", p.FirstDueAt.ToString("O"));
        cmd.Parameters.AddWithValue("$dl", p.DeadlineAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ac", p.AttemptCount);
        cmd.Parameters.AddWithValue("$st", p.Status);
        cmd.Parameters.AddWithValue("$rj", (object?)p.ResultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alj", (object?)p.AttemptLogJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", p.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<PendingVerification?> GetByIdAsync(string pendingId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE pending_id = $pid";
        cmd.Parameters.AddWithValue("$pid", pendingId);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    public async Task UpdateAttemptAsync(string pendingId, string newQueueEntryId, int attemptCount, string attemptLogJson)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_pending_verifications
            SET current_queue_entry_id = $cqid,
                attempt_count = $ac,
                attempt_log_json = $alj
            WHERE pending_id = $pid AND status = 'Pending'
            """;
        cmd.Parameters.AddWithValue("$pid", pendingId);
        cmd.Parameters.AddWithValue("$cqid", newQueueEntryId);
        cmd.Parameters.AddWithValue("$ac", attemptCount);
        cmd.Parameters.AddWithValue("$alj", attemptLogJson);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task MarkCompletedAsync(string pendingId, string resultJson, string attemptLogJson) =>
        MarkTerminalAsync(pendingId, "Completed", resultJson, attemptLogJson);

    public Task MarkFailedAsync(string pendingId, string resultJson, string attemptLogJson) =>
        MarkTerminalAsync(pendingId, "Failed", resultJson, attemptLogJson);

    private async Task MarkTerminalAsync(string pendingId, string status, string resultJson, string attemptLogJson)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_pending_verifications
            SET status = $st,
                result_json = $rj,
                attempt_log_json = $alj,
                completed_at = $now
            WHERE pending_id = $pid AND status = 'Pending'
            """;
        cmd.Parameters.AddWithValue("$pid", pendingId);
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$rj", resultJson);
        cmd.Parameters.AddWithValue("$alj", attemptLogJson);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CancelForRunAsync(string parentRunId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE run_pending_verifications
            SET status = 'Cancelled', completed_at = $now
            WHERE parent_run_id = $prid AND status = 'Pending'
            """;
        cmd.Parameters.AddWithValue("$prid", parentRunId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountPendingForRunAsync(string parentRunId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM run_pending_verifications
            WHERE parent_run_id = $prid AND status = 'Pending'
            """;
        cmd.Parameters.AddWithValue("$prid", parentRunId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<PendingVerification>> ListForRunAsync(string parentRunId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE parent_run_id = $prid ORDER BY first_due_at ASC";
        cmd.Parameters.AddWithValue("$prid", parentRunId);
        return await ReadListAsync(cmd);
    }

    public async Task<List<PendingVerification>> ListExpiredAsync(DateTime cutoffUtc)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql +
            " WHERE status = 'Pending' AND deadline_at <= $cutoff ORDER BY deadline_at ASC";
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("O"));
        return await ReadListAsync(cmd);
    }

    public async Task<List<PendingVerification>> ListPendingAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE status = 'Pending' ORDER BY first_due_at ASC";
        return await ReadListAsync(cmd);
    }

    private const string SelectSql = """
        SELECT pending_id, parent_run_id, current_queue_entry_id, module_id, test_set_id,
               delivery_objective_id, first_due_at, deadline_at, attempt_count, status,
               result_json, attempt_log_json, created_at, completed_at
        FROM run_pending_verifications
        """;

    private static async Task<List<PendingVerification>> ReadListAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<PendingVerification>();
        while (await reader.ReadAsync()) result.Add(Read(reader));
        return result;
    }

    private static PendingVerification Read(SqliteDataReader r) => new()
    {
        PendingId = r.GetString(0),
        ParentRunId = r.GetString(1),
        CurrentQueueEntryId = r.GetString(2),
        ModuleId = r.GetString(3),
        TestSetId = r.GetString(4),
        DeliveryObjectiveId = r.GetString(5),
        FirstDueAt = DateTime.Parse(r.GetString(6)).ToUniversalTime(),
        DeadlineAt = DateTime.Parse(r.GetString(7)).ToUniversalTime(),
        AttemptCount = r.GetInt32(8),
        Status = r.GetString(9),
        ResultJson = r.IsDBNull(10) ? null : r.GetString(10),
        AttemptLogJson = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt = DateTime.Parse(r.GetString(12)).ToUniversalTime(),
        CompletedAt = r.IsDBNull(13) ? null : DateTime.Parse(r.GetString(13)).ToUniversalTime(),
    };
}
