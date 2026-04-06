namespace AiTestCrew.Core.Models;

/// <summary>
/// The complete result of executing a single test objective.
/// In v2, each TestResult maps 1:1 to a TestObjective.
/// </summary>
public class TestResult
{
    /// <summary>The test objective ID this result corresponds to.</summary>
    public required string ObjectiveId { get; init; }

    /// <summary>Human-readable name of the test objective.</summary>
    public string ObjectiveName { get; init; } = "";

    public required string AgentName { get; init; }
    public required TestStatus Status { get; init; }
    public required string Summary { get; init; }
    public List<TestStep> Steps { get; init; } = [];
    public Dictionary<string, object> Metadata { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;

    public int PassedSteps => Steps.Count(s => s.Status == TestStatus.Passed);
    public int FailedSteps => Steps.Count(s => s.Status == TestStatus.Failed);
}


/// <summary>
/// Aggregated result of a full test suite run.
/// </summary>
public class TestSuiteResult
{
    public required string Objective { get; init; }
    public required List<TestResult> Results { get; init; }
    public required string Summary { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;

    public int TotalObjectives => Results.Count;
    public int Passed => Results.Count(r => r.Status == TestStatus.Passed);
    public int Failed => Results.Count(r => r.Status == TestStatus.Failed);
    public int Errors => Results.Count(r => r.Status == TestStatus.Error);
    public bool AllPassed => Results.All(r => r.Status == TestStatus.Passed);
}
