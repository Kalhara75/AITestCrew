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
        string? moduleId = null)
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
                }).ToList()
            }).ToList()
        };
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
