namespace AiTestCrew.Core.Models;

/// <summary>
/// Tracks one deferred post-delivery verification across retry attempts.
/// The authoritative "is this verification outstanding?" state for a given run —
/// <c>PersistedExecutionRun</c> is only finalised when every row for a run has
/// reached a terminal state.
/// </summary>
public class PendingVerification
{
    /// <summary>Stable identifier across retries. Same as the first queue entry's id.</summary>
    public string PendingId { get; set; } = "";

    /// <summary>The parent execution run's <c>RunId</c>.</summary>
    public string ParentRunId { get; set; } = "";

    /// <summary>Id of the currently-outstanding queue entry. Updated on re-enqueue.</summary>
    public string CurrentQueueEntryId { get; set; } = "";

    public string ModuleId { get; set; } = "";
    public string TestSetId { get; set; } = "";
    public string DeliveryObjectiveId { get; set; } = "";

    /// <summary>UTC — when the first attempt is due (delivery + wait × earlyStartFraction).</summary>
    public DateTime FirstDueAt { get; set; }

    /// <summary>UTC — past this, a failed attempt is final.</summary>
    public DateTime DeadlineAt { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Pending | Completed | Failed | Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Serialised <see cref="AiTestCrew.Agents.Persistence.PersistedObjectiveResult"/> captured from the final
    /// attempt. Null while pending.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// Append-only JSON array of attempt summaries: <c>[{ at, status, error }]</c>.
    /// Used for the rollup step rendered on the parent run.
    /// </summary>
    public string? AttemptLogJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
