using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Discovers and executes version-controlled data-pack SQL scripts at process
/// startup. Scripts live under <c>bin/datapacks/&lt;phase&gt;/&lt;envKey&gt;/&lt;NN.subfolder&gt;/&lt;NN.script&gt;.sql</c>
/// (packaged from the solution-root <c>data/datapacks/</c> folder by MSBuild).
///
/// These scripts are dev-authored and version-controlled, so they are NOT routed
/// through <see cref="AiTestCrew.Agents.Teardown.SqlGuardrails"/> — they
/// intentionally contain <c>CREATE/ALTER PROCEDURE</c>, <c>EXEC</c>, and
/// unbounded <c>DELETE</c> statements (different trust boundary from
/// LLM-/user-generated objective teardown).
///
/// Per-env opt-in via <see cref="IEnvironmentResolver.ResolveRunDataPacksOnStartup"/>;
/// envs without a configured Bravo DB connection string are skipped with an
/// INFO log. Failures within an env abort that env's remaining scripts but
/// other envs continue. The runner never throws.
/// </summary>
public interface IDataPackRunner
{
    /// <summary>
    /// Runs all enabled phases for every opted-in environment. Returns a
    /// structured summary suitable for startup logging.
    /// </summary>
    Task<DataPackRunSummary> RunAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Detailed report from the most recent <see cref="RunAllAsync"/> call,
    /// or null until the first invocation completes. Surfaced via the WebApi
    /// for the dashboard troubleshooting panel.
    /// </summary>
    DataPackStartupReport? LatestReport { get; }
}
