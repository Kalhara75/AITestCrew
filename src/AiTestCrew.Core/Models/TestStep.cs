using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AiTestCrew.Core.Models;

/// <summary>
/// A single step within a test execution.
/// Each agent produces multiple steps per task.
/// </summary>
public class TestStep
{
    public required string Action { get; init; }
    public required string Summary { get; init; }
    public required TestStatus Status { get; init; }
    public string? Detail { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Convenience factory for a passed step.</summary>
    public static TestStep Pass(string action, string summary, string? detail = null) =>
        new() { Action = action, Summary = summary, Status = TestStatus.Passed, Detail = detail };

    /// <summary>Convenience factory for a failed step.</summary>
    public static TestStep Fail(string action, string summary, string? detail = null) =>
        new() { Action = action, Summary = summary, Status = TestStatus.Failed, Detail = detail };

    /// <summary>Convenience factory for an error step.</summary>
    public static TestStep Err(string action, string summary, string? detail = null) =>
        new() { Action = action, Summary = summary, Status = TestStatus.Error, Detail = detail };
}