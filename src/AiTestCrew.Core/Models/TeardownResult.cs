namespace AiTestCrew.Core.Models;

/// <summary>
/// Outcome of running a test set's SQL teardown steps for a single objective.
/// Returned by <see cref="AiTestCrew.Core.Interfaces.ITeardownExecutor"/>.
/// </summary>
public class TeardownResult
{
    /// <summary>True when every step executed (or was dry-run) without error.</summary>
    public bool Success { get; set; }

    /// <summary>Per-step outcomes, in input order.</summary>
    public List<TeardownStepResult> Steps { get; set; } = [];

    /// <summary>
    /// Top-level failure message when execution couldn't start — e.g. the
    /// environment hasn't opted in to teardown, or a connection string is
    /// missing. Null when the failure (if any) is per-step.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Per-statement teardown outcome.
/// </summary>
public class TeardownStepResult
{
    /// <summary>Teardown step display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The SQL that was (or would have been, for dry-run) executed — tokens
    /// already substituted. Captured for audit and UI display.
    /// </summary>
    public string Sql { get; set; } = "";

    /// <summary>Rows affected by the statement. Zero for dry-run or failed steps.</summary>
    public int RowsAffected { get; set; }

    /// <summary>Error message when this step failed; null on success.</summary>
    public string? Error { get; set; }

    /// <summary>True when the step was logged but not executed.</summary>
    public bool DryRun { get; set; }
}
