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
    public int TotalTasks { get; set; }
    public int PassedTasks { get; set; }
    public int FailedTasks { get; set; }
    public int ErrorTasks { get; set; }
    public List<PersistedTaskResult> TaskResults { get; set; } = [];

    /// <summary>
    /// Converts an in-memory TestSuiteResult to the persisted form.
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
            TotalTasks = suite.TotalTasks,
            PassedTasks = suite.Passed,
            FailedTasks = suite.Failed,
            ErrorTasks = suite.Errors,
            TaskResults = suite.Results.Select(r => new PersistedTaskResult
            {
                TaskId = r.TaskId,
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
/// Persisted result of a single task within an execution run.
/// </summary>
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
/// Persisted result of a single test step within a task result.
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
