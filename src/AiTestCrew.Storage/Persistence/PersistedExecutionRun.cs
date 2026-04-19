using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Persisted snapshot of a single test suite execution.
/// Stored as JSON in executions/{testSetId}/{runId}.json.
/// </summary>
public class PersistedExecutionRun
{
    public string RunId { get; set; } = "";
    public string TestSetId { get; set; } = "";
    public string? ModuleId { get; set; }
    public string Objective { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public string Summary { get; set; } = "";

    /// <summary>User ID who started this run. Null for runs started before user tracking.</summary>
    public string? StartedBy { get; set; }

    /// <summary>Display name of the user who started this run.</summary>
    public string? StartedByName { get; set; }

    /// <summary>
    /// Customer environment this run executed against (e.g. "sumo-retail").
    /// Recorded for audit / filtering. Null on runs that predate the multi-env feature.
    /// </summary>
    public string? EnvironmentKey { get; set; }

    /// <summary>
    /// Schema version for migration detection.
    /// Version 1 (or absent): legacy format with TaskResults.
    /// Version 2: new format with ObjectiveResults.
    /// </summary>
    public int SchemaVersion { get; set; }

    // ── v2 fields (objective-based) ──
    public int TotalObjectives { get; set; }
    public int PassedObjectives { get; set; }
    public int FailedObjectives { get; set; }
    public int ErrorObjectives { get; set; }
    public List<PersistedObjectiveResult> ObjectiveResults { get; set; } = [];

    // ── v1 legacy fields (kept for deserialization of old runs) ──
    [Obsolete("Use TotalObjectives instead.")]
    public int TotalTasks { get; set; }
    [Obsolete("Use PassedObjectives instead.")]
    public int PassedTasks { get; set; }
    [Obsolete("Use FailedObjectives instead.")]
    public int FailedTasks { get; set; }
    [Obsolete("Use ErrorObjectives instead.")]
    public int ErrorTasks { get; set; }
    [Obsolete("Use ObjectiveResults instead.")]
    public List<PersistedTaskResult>? TaskResults { get; set; }

    /// <summary>
    /// Performs in-memory v1→v2 migration when deserializing legacy execution runs.
    /// </summary>
    public void MigrateToV2()
    {
#pragma warning disable CS0612
        if (SchemaVersion >= 2) return;
        if (TaskResults is { Count: > 0 } && ObjectiveResults.Count == 0)
        {
            ObjectiveResults = TaskResults.Select(tr => new PersistedObjectiveResult
            {
                ObjectiveId = tr.TaskId,
                ObjectiveName = tr.TaskId, // best we have from v1
                AgentName = tr.AgentName,
                Status = tr.Status,
                Summary = tr.Summary,
                Duration = tr.Duration,
                CompletedAt = tr.CompletedAt,
                PassedSteps = tr.PassedSteps,
                FailedSteps = tr.FailedSteps,
                TotalSteps = tr.TotalSteps,
                Steps = tr.Steps
            }).ToList();

            TotalObjectives = TotalTasks;
            PassedObjectives = PassedTasks;
            FailedObjectives = FailedTasks;
            ErrorObjectives = ErrorTasks;
        }
        SchemaVersion = 2;
#pragma warning restore CS0612
    }

    /// <summary>
    /// Converts an in-memory TestSuiteResult to the persisted form (v2).
    /// </summary>
    public static PersistedExecutionRun FromSuiteResult(
        TestSuiteResult suite, string testSetId, RunMode mode, DateTime startedAt,
        string? moduleId = null, string? environmentKey = null)
    {
        return new PersistedExecutionRun
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            TestSetId = testSetId,
            ModuleId = moduleId,
            Objective = suite.Objective,
            Mode = mode.ToString(),
            Status = suite.AllPassed ? "Passed"
                   : suite.Errors > 0 ? "Error"
                   : "Failed",
            StartedAt = startedAt,
            CompletedAt = suite.CompletedAt,
            TotalDuration = suite.TotalDuration,
            Summary = suite.Summary,
            SchemaVersion = 2,
            EnvironmentKey = environmentKey,
            TotalObjectives = suite.TotalObjectives,
            PassedObjectives = suite.Passed,
            FailedObjectives = suite.Failed,
            ErrorObjectives = suite.Errors,
            ObjectiveResults = suite.Results.Select(r => new PersistedObjectiveResult
            {
                ObjectiveId = r.ObjectiveId,
                ObjectiveName = r.ObjectiveName,
                AgentName = r.AgentName,
                Status = r.Status.ToString(),
                Summary = r.Summary,
                Duration = r.Duration,
                CompletedAt = r.CompletedAt,
                PassedSteps = r.PassedSteps,
                FailedSteps = r.FailedSteps,
                TotalSteps = r.Steps.Count,
                Steps = r.Steps.Select(s => new PersistedStepResult
                {
                    Action = s.Action,
                    Summary = s.Summary,
                    Status = s.Status.ToString(),
                    Detail = s.Detail,
                    Duration = s.Duration,
                    Timestamp = s.Timestamp
                }).ToList(),
                Deliveries = ExtractDeliveries(r.Metadata),
                TeardownResults = ExtractTeardown(r.Metadata)
            }).ToList()
        };
    }

    /// <summary>
    /// Projects a <c>TestResult.Metadata["teardown"]</c> entry
    /// (<c>List&lt;TeardownStepResult&gt;</c>) into the persisted shape.
    /// </summary>
    private static List<PersistedTeardownStep>? ExtractTeardown(IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("teardown", out var raw) || raw is null) return null;

        if (raw is IEnumerable<TeardownStepResult> typed)
        {
            var list = typed.Select(t => new PersistedTeardownStep
            {
                Name = t.Name,
                Sql = t.Sql,
                RowsAffected = t.RowsAffected,
                Error = t.Error,
                DryRun = t.DryRun
            }).ToList();
            return list.Count > 0 ? list : null;
        }

        return null;
    }

    /// <summary>
    /// Projects a delivery agent's <c>TestResult.Metadata["deliveries"]</c> entry
    /// (<c>List&lt;Dictionary&lt;string, object?&gt;&gt;</c>) into the typed
    /// <see cref="PersistedDelivery"/> list for persistence. Returns null when no
    /// deliveries are present so non-delivery objectives don't carry empty arrays.
    /// </summary>
    private static List<PersistedDelivery>? ExtractDeliveries(IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("deliveries", out var raw) || raw is null) return null;

        var list = new List<PersistedDelivery>();
        if (raw is IEnumerable<Dictionary<string, object?>> typed)
        {
            foreach (var d in typed)
            {
                list.Add(new PersistedDelivery
                {
                    MessageId     = AsString(d, "messageId"),
                    TransactionId = AsString(d, "transactionId"),
                    EndpointCode  = AsString(d, "endpointCode"),
                    RemotePath    = AsString(d, "remotePath"),
                    UploadedAs    = AsString(d, "uploadedAs"),
                    Bytes         = AsLong(d, "bytes"),
                    Status        = AsString(d, "status"),
                });
            }
        }
        return list.Count > 0 ? list : null;

        static string AsString(IReadOnlyDictionary<string, object?> d, string k) =>
            d.TryGetValue(k, out var v) && v is not null ? v.ToString() ?? "" : "";
        static long AsLong(IReadOnlyDictionary<string, object?> d, string k) =>
            d.TryGetValue(k, out var v) && v is not null
                ? (v is long l ? l : long.TryParse(v.ToString(), out var parsed) ? parsed : 0)
                : 0;
    }
}

/// <summary>
/// Persisted result of a single test objective within an execution run.
/// </summary>
public class PersistedObjectiveResult
{
    public string ObjectiveId { get; set; } = "";
    public string ObjectiveName { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public DateTime CompletedAt { get; set; }
    public int PassedSteps { get; set; }
    public int FailedSteps { get; set; }
    public int TotalSteps { get; set; }
    public List<PersistedStepResult> Steps { get; set; } = [];

    /// <summary>
    /// aseXML delivery receipts from this objective, captured from the delivery
    /// agent's TestResult.Metadata["deliveries"]. Null for non-delivery objectives.
    /// Used by the Phase 3 recorder to seed auto-parameterisation context with
    /// MessageID / TransactionID / Filename / EndpointCode from the latest run.
    /// </summary>
    public List<PersistedDelivery>? Deliveries { get; set; }

    /// <summary>
    /// Per-step teardown outcomes recorded before this objective's agent task
    /// dispatched. Null when the test set has no teardown configured or the
    /// run skipped teardown.
    /// </summary>
    public List<PersistedTeardownStep>? TeardownResults { get; set; }
}

/// <summary>
/// Persisted snapshot of one teardown step within an execution run.
/// Mirrors <see cref="AiTestCrew.Core.Models.TeardownStepResult"/> plus a stable shape for JSON.
/// </summary>
public class PersistedTeardownStep
{
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
    public int RowsAffected { get; set; }
    public string? Error { get; set; }
    public bool DryRun { get; set; }
}

/// <summary>
/// Typed summary of one aseXML delivery within an execution run — the subset of
/// <c>AseXmlDeliveryAgent.Metadata["deliveries"]</c> worth keeping in history.
/// </summary>
public class PersistedDelivery
{
    public string MessageId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string EndpointCode { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string UploadedAs { get; set; } = "";   // "xml" | "zip"
    public long Bytes { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>
/// Legacy: persisted result of a single task (v1 schema).
/// Kept for deserialization of old execution run files.
/// </summary>
[Obsolete("Use PersistedObjectiveResult instead.")]
public class PersistedTaskResult
{
    public string TaskId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public DateTime CompletedAt { get; set; }
    public int PassedSteps { get; set; }
    public int FailedSteps { get; set; }
    public int TotalSteps { get; set; }
    public List<PersistedStepResult> Steps { get; set; } = [];
}

/// <summary>
/// Persisted result of a single test step within an objective/task result.
/// </summary>
public class PersistedStepResult
{
    public string Action { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Detail { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime Timestamp { get; set; }
}
