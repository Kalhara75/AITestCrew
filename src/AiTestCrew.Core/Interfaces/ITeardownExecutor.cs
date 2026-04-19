using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Runs a test set's SQL teardown statements against the active environment's
/// Bravo DB. Invoked by the orchestrator once per objective, before the agent
/// task dispatches.
///
/// Implementations MUST enforce per-environment opt-in
/// (<c>EnvironmentConfig.DataTeardownEnabled</c>) and a SQL guardrail check
/// (WHERE required, keyword denylist) before opening any connection.
/// </summary>
public interface ITeardownExecutor
{
    /// <summary>
    /// Executes <paramref name="steps"/> in order against the given environment,
    /// substituting <c>{{Token}}</c> placeholders using <paramref name="context"/>
    /// in strict mode (unknown tokens fail the run).
    /// </summary>
    /// <param name="steps">The teardown statements, as loaded from the test set.</param>
    /// <param name="context">Token values. Built by the orchestrator from env params + delivery step fields + template const fields.</param>
    /// <param name="environmentKey">Resolved environment key (used for opt-in + connection-string lookup).</param>
    /// <param name="dryRun">When true, logs the substituted SQL but opens no connection.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TeardownResult> ExecuteAsync(
        IReadOnlyList<SqlTeardownStepDto> steps,
        IReadOnlyDictionary<string, string> context,
        string environmentKey,
        bool dryRun,
        CancellationToken ct = default);
}

/// <summary>
/// DTO form of a teardown step — decouples the executor interface (in Core)
/// from the persistence model (in Storage/Agents).
/// </summary>
public record SqlTeardownStepDto(string Name, string Sql);
