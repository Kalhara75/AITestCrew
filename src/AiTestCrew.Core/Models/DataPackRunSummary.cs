namespace AiTestCrew.Core.Models;

/// <summary>
/// Outcome of a single <see cref="AiTestCrew.Core.Interfaces.IDataPackRunner"/>
/// invocation. Counts are aggregated across every environment that opted in
/// and had a Bravo DB connection string configured.
/// </summary>
public sealed record DataPackRunSummary(
    int EnvsConsidered,
    int EnvsRan,
    int EnvsSkipped,
    int ScriptsExecuted,
    int BatchesExecuted,
    int Failures,
    TimeSpan Elapsed,
    IReadOnlyList<string> EnvsTouched,
    IReadOnlyList<string> Errors);
