namespace AiTestCrew.Agents.AseXmlAgent.Delivery;

/// <summary>
/// Self-contained snapshot serialised into a deferred <c>VerifyOnly</c> queue entry.
/// Carries the full delivery context so the claiming agent does NOT have to reconstruct
/// it from execution history — that would race with concurrent deliveries against the
/// same test set, which would overwrite MessageID before the first delivery's verification
/// fires.
///
/// Persisted as JSON in <c>run_queue.request_json</c> alongside a <c>Kind</c> tag so the
/// <c>JobExecutor</c> can distinguish deferred-verification jobs from ordinary
/// <c>VerifyOnly</c> jobs triggered from the CLI.
/// </summary>
public class DeferredVerificationRequest
{
    /// <summary>Discriminator so the executor can detect a deferred job.</summary>
    public string Kind { get; set; } = "DeferredVerification";

    public string ParentRunId { get; set; } = "";
    public string PendingId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string TestSetId { get; set; } = "";
    public string DeliveryObjectiveId { get; set; } = "";
    public string DeliveryObjectiveName { get; set; } = "";

    /// <summary>Customer environment key (null = default).</summary>
    public string? EnvironmentKey { get; set; }

    /// <summary>When the delivery upload actually completed (UTC). Used to display elapsed time to the user.</summary>
    public DateTime DeliveryCompletedAt { get; set; }

    /// <summary>Deadline (UTC) past which the next failed attempt is final.</summary>
    public DateTime DeadlineAt { get; set; }

    /// <summary>Attempts already made against this pending row before this claim.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Resolved delivery context at the moment of upload — MessageID, TransactionID,
    /// EndpointCode, Filename, RemotePath, UploadedAs + every resolved template field.
    /// Fed into <c>{{Token}}</c> substitution on each verification's UI steps.
    /// </summary>
    public Dictionary<string, string> DeliveryContext { get; set; } = new();

    /// <summary>
    /// The verification steps to replay, in order. Each retains its original
    /// <c>WaitBeforeSeconds</c> so relative gaps between verifications are honoured
    /// (the first verification's wait is consumed by the queue's <c>not_before_at</c>;
    /// subsequent ones wait incrementally inside the handler).
    /// </summary>
    public List<VerificationStep> Verifications { get; set; } = new();
}
