namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Manages per-objective recording locks. A lock is held for the duration of a
/// recording job (Queued → Claimed → Running). The janitor sweeps stale locks
/// whose job_id is no longer in a non-terminal queue state.
/// </summary>
public interface IRecordingLockRepository
{
    /// <summary>
    /// Attempts to acquire a lock. Throws <see cref="InvalidOperationException"/>
    /// when another job already holds the lock for the same (moduleId, testSetId, objectiveId).
    /// </summary>
    Task AcquireAsync(string moduleId, string testSetId, string? objectiveId, string jobId, string lockedBy, CancellationToken ct = default);

    /// <summary>Releases all locks held by the given <paramref name="jobId"/>.</summary>
    Task ReleaseAsync(string jobId, CancellationToken ct = default);

    /// <summary>Returns the active lock for a (moduleId, testSetId, objectiveId) tuple, or null.</summary>
    Task<RecordingLockInfo?> GetLockAsync(string moduleId, string testSetId, string? objectiveId, CancellationToken ct = default);

    /// <summary>
    /// Deletes locks whose job_id is no longer in a non-terminal queue state
    /// (Queued, Claimed, Running). Called by the heartbeat janitor.
    /// </summary>
    Task SweepStaleLocksAsync(CancellationToken ct = default);
}

/// <summary>Represents the state of an active recording lock.</summary>
public sealed record RecordingLockInfo(
    string ModuleId,
    string TestSetId,
    string? ObjectiveId,
    string JobId,
    string LockedBy,
    string LockedAt);
