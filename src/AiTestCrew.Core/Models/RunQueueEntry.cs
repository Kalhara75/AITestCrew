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
}
