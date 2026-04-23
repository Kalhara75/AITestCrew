using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Tracks in-flight deferred post-delivery verifications. A parent execution run
/// stays in <c>AwaitingVerification</c> until every row for it reaches a terminal
/// state (Completed / Failed / Cancelled).
/// </summary>
public interface IPendingVerificationRepository
{
    Task InsertAsync(PendingVerification pending);

    Task<PendingVerification?> GetByIdAsync(string pendingId);

    /// <summary>Swap the <c>CurrentQueueEntryId</c> and bump <c>AttemptCount</c> on re-enqueue.</summary>
    Task UpdateAttemptAsync(string pendingId, string newQueueEntryId, int attemptCount, string attemptLogJson);

    /// <summary>Terminal success path — stores the final result JSON.</summary>
    Task MarkCompletedAsync(string pendingId, string resultJson, string attemptLogJson);

    /// <summary>Terminal failure path — deadline exceeded or agent exhausted retries.</summary>
    Task MarkFailedAsync(string pendingId, string resultJson, string attemptLogJson);

    /// <summary>User cancelled the parent run. Sweeps all <c>Pending</c> rows for the run.</summary>
    Task<int> CancelForRunAsync(string parentRunId);

    /// <summary>Rows still in <c>Pending</c> status for a given parent run.</summary>
    Task<int> CountPendingForRunAsync(string parentRunId);

    /// <summary>All rows (any status) for a given parent run. Used for run-status display + finalisation.</summary>
    Task<List<PendingVerification>> ListForRunAsync(string parentRunId);

    /// <summary>
    /// Rows in <c>Pending</c> status whose deadline has expired without finalisation —
    /// used by the janitor to fail them and finalise their parent runs.
    /// </summary>
    Task<List<PendingVerification>> ListExpiredAsync(DateTime cutoffUtc);

    /// <summary>All rows currently in <c>Pending</c> (dashboard banner, admin views).</summary>
    Task<List<PendingVerification>> ListPendingAsync();
}
