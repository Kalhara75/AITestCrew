namespace AiTestCrew.Core.Models;

/// <summary>
/// Detailed per-env / per-script outcome from the most recent
/// <see cref="AiTestCrew.Core.Interfaces.IDataPackRunner.RunAllAsync"/> call.
/// Surfaced via the WebApi for dashboard troubleshooting.
/// </summary>
public sealed record DataPackStartupReport(
    DateTime CompletedAtUtc,
    string RootPath,
    bool RootExists,
    TimeSpan Elapsed,
    IReadOnlyList<DataPackEnvReport> Envs);

/// <summary>One environment's outcome.</summary>
public sealed record DataPackEnvReport(
    string EnvKey,
    string Status,
    string? SkipReason,
    string? Error,
    int ScriptsTotal,
    int ScriptsExecuted,
    int BatchesExecuted,
    int Failures,
    IReadOnlyList<DataPackScriptReport> Scripts);

/// <summary>One .sql file's outcome.</summary>
public sealed record DataPackScriptReport(
    string Phase,
    string Subfolder,
    string RelativePath,
    string Status,
    int BatchCount,
    long ElapsedMs,
    string? Error);

/// <summary>Allowed values for <see cref="DataPackEnvReport.Status"/>.</summary>
public static class DataPackEnvStatus
{
    public const string Ran = "Ran";
    public const string SkippedNotConfigured = "SkippedNotConfigured";
    public const string SkippedOptOut = "SkippedOptOut";
    public const string SkippedNoConnection = "SkippedNoConnection";
    public const string ConnectionFailed = "ConnectionFailed";
}

/// <summary>Allowed values for <see cref="DataPackScriptReport.Status"/>.</summary>
public static class DataPackScriptStatus
{
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}
