using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Executes read-only DB checks as post-steps. Only invoked via the post-step
/// pathway — never as a top-level test agent — because a DB assertion without
/// a preceding write has no signal.
///
/// Consumes <c>PreloadedTestCases</c> as a <c>List&lt;DbCheckStepDefinition&gt;</c>
/// (the shape the orchestrator sets up for Db_SqlServer post-steps). For each
/// definition:
/// <list type="number">
///   <item>Validates the SQL via <see cref="DbCheckSqlGuardrails"/>.</item>
///   <item>Resolves the connection string via <see cref="IEnvironmentResolver"/>
///     (same path as the Bravo endpoint resolver / teardown executor).</item>
///   <item>Runs the SELECT and asserts the result against either
///     <c>ExpectedRowCount</c> or <c>ExpectedColumnValues</c>.</item>
/// </list>
///
/// Connection keys other than <c>"BravoDb"</c> surface as Fail — additional
/// DBs can be added by routing <c>ConnectionKey</c> through a resolver, but
/// that's out of scope for Slice 2.
/// </summary>
public class DbCheckAgent : BaseTestAgent
{
    private readonly IEnvironmentResolver _envResolver;

    public override string Name => "DB Check Agent";
    public override string Role =>
        "Senior SQL Test Engineer who asserts database state after a feature invocation.";

    public DbCheckAgent(
        Kernel kernel,
        ILogger<DbCheckAgent> logger,
        IEnvironmentResolver envResolver,
        PostStepOrchestrator postStepOrchestrator)
        : base(kernel, logger, postStepOrchestrator)
    {
        _envResolver = envResolver;
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.Db_SqlServer);

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        task.Parameters.TryGetValue("EnvironmentKey", out var rawEnvKey);
        var envKey = rawEnvKey as string;

        Logger.LogInformation("[{Agent}] Starting DB check task: {Desc} (env: {Env})",
            Name, task.Description, envKey ?? "default");

        if (!task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
            || preloaded is not List<DbCheckStepDefinition> checks
            || checks.Count == 0)
        {
            steps.Add(TestStep.Err("db-check",
                "DbCheckAgent requires a PreloadedTestCases list of DbCheckStepDefinition. " +
                "DB checks are only invoked as post-steps, never standalone."));
            return Build(task, steps, TestStatus.Error, "No DB check definitions supplied.", sw);
        }

        var connectionString = ResolveConnectionString(checks[0].ConnectionKey, envKey);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            steps.Add(TestStep.Err("db-check",
                $"No connection string resolved for key '{checks[0].ConnectionKey}' (env '{envKey ?? "default"}')."));
            return Build(task, steps, TestStatus.Error, "DB check connection unresolved.", sw);
        }

        await using var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err("db-check-open",
                $"Failed to open connection for key '{checks[0].ConnectionKey}': {ex.Message}"));
            return Build(task, steps, TestStatus.Error, "DB check connection failed.", sw);
        }

        for (var i = 0; i < checks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RunOneAsync(conn, checks[i], i + 1, steps, ct);
        }

        var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
        var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
        var status = hasErrors ? TestStatus.Error
                   : hasFails ? TestStatus.Failed
                   : TestStatus.Passed;
        var summary = status == TestStatus.Passed
            ? $"{checks.Count} DB check(s) passed."
            : $"{steps.Count(s => s.Status == TestStatus.Failed)} of {checks.Count} DB check(s) failed.";
        return Build(task, steps, status, summary, sw);
    }

    private string? ResolveConnectionString(string connectionKey, string? envKey)
    {
        // Slice 2: only "BravoDb" is routed through the resolver. If future
        // keys are added (e.g. SDR reporting DB), extend this switch — don't
        // hardcode connection strings in the agent.
        return string.Equals(connectionKey, "BravoDb", StringComparison.OrdinalIgnoreCase)
            ? _envResolver.ResolveBravoDbConnectionString(envKey)
            : null;
    }

    private async Task RunOneAsync(
        SqlConnection conn,
        DbCheckStepDefinition check,
        int index,
        List<TestStep> steps,
        CancellationToken ct)
    {
        var action = $"db-check[{index}] {check.Name}";

        var (ok, reason) = DbCheckSqlGuardrails.Validate(check.Sql);
        if (!ok)
        {
            steps.Add(TestStep.Fail(action, $"SQL guardrail rejected the statement: {reason}"));
            return;
        }

        try
        {
            await using var cmd = new SqlCommand(check.Sql, conn)
            {
                CommandTimeout = Math.Max(1, check.TimeoutSeconds)
            };

            if (check.ExpectedRowCount is int expectedCount)
            {
                var actualCount = 0;
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct)) actualCount++;
                }

                if (actualCount == expectedCount)
                {
                    steps.Add(TestStep.Pass(action,
                        $"Row count matched ({actualCount}). SQL: {Preview(check.Sql)}"));
                }
                else
                {
                    steps.Add(TestStep.Fail(action,
                        $"Expected {expectedCount} row(s), got {actualCount}. SQL: {Preview(check.Sql)}"));
                }
                return;
            }

            if (check.ExpectedColumnValues.Count > 0)
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    steps.Add(TestStep.Fail(action,
                        $"Expected at least one row with column values {FormatExpectations(check.ExpectedColumnValues)}, got no rows. SQL: {Preview(check.Sql)}"));
                    return;
                }

                var mismatches = new List<string>();
                foreach (var (col, expected) in check.ExpectedColumnValues)
                {
                    var ordinal = -1;
                    try { ordinal = reader.GetOrdinal(col); }
                    catch (IndexOutOfRangeException)
                    {
                        mismatches.Add($"column '{col}' missing from result set");
                        continue;
                    }
                    var actual = reader.IsDBNull(ordinal) ? "" : reader.GetValue(ordinal)?.ToString() ?? "";
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add($"{col}: expected '{expected}', got '{actual}'");
                }

                if (mismatches.Count == 0)
                {
                    steps.Add(TestStep.Pass(action,
                        $"All {check.ExpectedColumnValues.Count} expected column value(s) matched on first row. SQL: {Preview(check.Sql)}"));
                }
                else
                {
                    steps.Add(TestStep.Fail(action,
                        $"Column value mismatch: {string.Join("; ", mismatches)}. SQL: {Preview(check.Sql)}"));
                }
                return;
            }

            steps.Add(TestStep.Err(action,
                "DbCheck has neither ExpectedRowCount nor ExpectedColumnValues set — nothing to assert."));
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(action,
                $"DB check threw: {ex.Message}. SQL: {Preview(check.Sql)}"));
        }
    }

    private static string Preview(string sql)
    {
        var single = new StringBuilder(sql.Length);
        foreach (var c in sql) single.Append(c == '\n' || c == '\r' ? ' ' : c);
        var s = single.ToString().Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private static string FormatExpectations(Dictionary<string, string> exp) =>
        "{ " + string.Join(", ", exp.Select(kv => $"{kv.Key}='{kv.Value}'")) + " }";

    private TestResult Build(
        TestTask task, List<TestStep> steps, TestStatus status, string summary, Stopwatch sw) =>
        new()
        {
            ObjectiveId = task.Id,
            ObjectiveName = task.Description,
            AgentName = Name,
            Status = status,
            Summary = summary,
            Steps = steps,
            Duration = sw.Elapsed,
        };
}
