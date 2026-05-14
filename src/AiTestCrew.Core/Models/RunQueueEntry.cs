namespace AiTestCrew.Core.Models;

/// <summary>
/// A run job in the distributed execution queue. Created by the WebApi when a
/// dashboard trigger targets a UI test that the server can't execute in-process;
/// claimed by an <see cref="Agent"/>, executed locally, and marked Completed/Failed.
/// </summary>
public class RunQueueEntry
{
    public string Id { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string TestSetId { get; set; } = "";

    /// <summary>Null = run the whole test set.</summary>
    public string? ObjectiveId { get; set; }

    /// <summary>Used to match against agent capabilities (e.g. "UI_Web_Blazor").</summary>
    public string TargetType { get; set; } = "";

    /// <summary>
    /// Kind of job: "Run" (default, existing behaviour — runs via TestOrchestrator),
    /// or one of "Record" / "RecordSetup" / "RecordVerification" / "AuthSetup"
    /// for interactive sessions executed by <c>IRecordingService</c> on the agent.
    /// </summary>
    public string JobKind { get; set; } = "Run";

    /// <summary>Reuse | Rebaseline | VerifyOnly for JobKind=Run. Unused for recording kinds.</summary>
    public string Mode { get; set; } = "";

    /// <summary>User.id of the person who triggered the run.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Queued | Claimed | Running | Completed | Failed | Cancelled.</summary>
    public string Status { get; set; } = "Queued";

    /// <summary>Agent.id that claimed the job.</summary>
    public string? ClaimedBy { get; set; }

    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Full RunRequest JSON so the agent reconstructs every parameter.</summary>
    public string RequestJson { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Earliest UTC time a claiming agent may pick up this entry. Null = immediate.
    /// Used to schedule deferred post-delivery verifications — the queue holds the entry
    /// until Bravo has had time to process the uploaded file.
    /// </summary>
    public DateTime? NotBeforeAt { get; set; }

    /// <summary>
    /// Absolute UTC cutoff for deferred verifications. A retry re-enqueue is refused
    /// past this time; the last attempt's failure becomes the authoritative result.
    /// Null on ordinary queue entries.
    /// </summary>
    public DateTime? DeadlineAt { get; set; }

    /// <summary>
    /// Number of execution attempts already made against the parent pending row.
    /// 0 = first attempt. Incremented each time a failed attempt re-enqueues.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// The original <c>pending_id</c> (= first queue entry id) for deferred-verification
    /// retries. Null on ordinary entries and on the first attempt.
    /// </summary>
    public string? ParentQueueEntryId { get; set; }

    /// <summary>
    /// The <see cref="PersistedExecutionRun.RunId"/> this queue entry ultimately contributes
    /// results to — needed so cancellation can sweep all pending entries for a run, and so the
    /// finalisation merge knows which run to update when the pending count hits zero. Null on
    /// ordinary dashboard-triggered queue entries.
    /// </summary>
    public string? ParentRunId { get; set; }

    /// <summary>
    /// When this entry is paused on an outstanding auth-refresh, the
    /// <see cref="AuthRefreshRequest.Id"/> blocking it. The janitor clears this
    /// and resets <see cref="NotBeforeAt"/> when the refresh completes so an
    /// agent picks the entry back up. Null on ordinary entries.
    /// </summary>
    public string? AuthRefreshId { get; set; }

    /// <summary>
    /// When set, only the agent with this id may claim this entry.
    /// The janitor applies <c>PreferredAgentFallbackPolicy</c> if the
    /// preferred agent is offline past the claim deadline.
    /// Null = any role+capability-matching agent may claim.
    /// </summary>
    public string? PreferredAgentId { get; set; }

    /// <summary>
    /// JSON-serialised list of pool tags that a claiming agent must supply as a superset.
    /// Null / empty list = no tag restriction.
    /// </summary>
    public List<string> RequiredTags { get; set; } = new();
}
