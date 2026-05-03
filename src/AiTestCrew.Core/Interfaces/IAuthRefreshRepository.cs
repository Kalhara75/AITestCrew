using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Persists outstanding auth-refresh requests. The unique partial index on
/// <c>(env_key, surface, stack_key, agent_id)</c> WHERE
/// <c>status IN ('Pending', 'InProgress')</c> deduplicates concurrent failures
/// at the same scope — one refresh unblocks every paused run that shares it.
/// </summary>
public interface IAuthRefreshRepository
{
    /// <summary>
    /// Idempotent insert. If a row already exists in <c>Pending</c> or
    /// <c>InProgress</c> for the same scope, returns it instead of creating a
    /// new one (and does not modify it). Otherwise inserts a fresh
    /// <c>Pending</c> row and returns it.
    /// </summary>
    Task<AuthRefreshRequest> InsertOrJoinAsync(AuthRefreshRequest request);

    Task<AuthRefreshRequest?> GetByIdAsync(string id);

    /// <summary>Pending or InProgress only — for the dashboard banner.</summary>
    Task<List<AuthRefreshRequest>> ListActiveAsync();

    /// <summary>Transitions Pending → InProgress; bumps AutoAttemptCount + LastAttemptAt.</summary>
    Task MarkInProgressAsync(string id);

    Task MarkCompletedAsync(string id);

    Task MarkFailedAsync(string id, string errorMessage);

    Task<bool> CancelAsync(string id);

    /// <summary>Rows in <c>InProgress</c> older than <paramref name="cutoffUtc"/>.</summary>
    Task<List<AuthRefreshRequest>> ListStaleInProgressAsync(DateTime cutoffUtc);

    /// <summary>Recently-Completed rows — used by the janitor to release dependent queue entries.</summary>
    Task<List<AuthRefreshRequest>> ListRecentlyCompletedAsync(DateTime sinceUtc);
}
