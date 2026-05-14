using AiTestCrew.Core.Interfaces;
using Microsoft.Data.Sqlite;


namespace AiTestCrew.Agents.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IRecordingLockRepository"/>.
/// Uses the <c>recording_locks</c> table with a unique index on
/// (module_id, test_set_id, COALESCE(objective_id, '')) to enforce exclusivity.
/// </summary>
public sealed class SqliteRecordingLockRepository : IRecordingLockRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteRecordingLockRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task AcquireAsync(
        string moduleId, string testSetId, string? objectiveId,
        string jobId, string lockedBy, CancellationToken ct = default)
    {
        // Read any existing lock first so we can report who holds it.
        var existing = await GetLockAsync(moduleId, testSetId, objectiveId, ct);
        if (existing is not null)
            throw new InvalidOperationException(
                $"Recording already in progress on this objective by {existing.LockedBy} (job {existing.JobId})");

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recording_locks (module_id, test_set_id, objective_id, job_id, locked_by, locked_at)
            VALUES ($mid, $tsid, $objid, $jobId, $lockedBy, $lockedAt)
            """;
        cmd.Parameters.AddWithValue("$mid", moduleId);
        cmd.Parameters.AddWithValue("$tsid", testSetId);
        cmd.Parameters.AddWithValue("$objid", (object?)objectiveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$lockedBy", lockedBy);
        cmd.Parameters.AddWithValue("$lockedAt", DateTime.UtcNow.ToString("O"));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            // Re-read to get the current holder and produce a better message.
            var current = await GetLockAsync(moduleId, testSetId, objectiveId, ct);
            throw new InvalidOperationException(
                $"Recording already in progress on this objective by {current?.LockedBy ?? "unknown"} (job {current?.JobId ?? "unknown"})");
        }
    }

    public async Task ReleaseAsync(string jobId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recording_locks WHERE job_id = $jobId";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RecordingLockInfo?> GetLockAsync(
        string moduleId, string testSetId, string? objectiveId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT module_id, test_set_id, objective_id, job_id, locked_by, locked_at
            FROM recording_locks
            WHERE module_id = $mid AND test_set_id = $tsid
              AND COALESCE(objective_id, '') = COALESCE($objid, '')
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$mid", moduleId);
        cmd.Parameters.AddWithValue("$tsid", testSetId);
        cmd.Parameters.AddWithValue("$objid", (object?)objectiveId ?? DBNull.Value);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync()) return null;
        return new RecordingLockInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    public async Task SweepStaleLocksAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // Delete locks whose job_id is no longer in an active (non-terminal) queue state.
        cmd.CommandText = """
            DELETE FROM recording_locks
            WHERE job_id NOT IN (
                SELECT id FROM run_queue WHERE status IN ('Queued', 'Claimed', 'Running')
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
