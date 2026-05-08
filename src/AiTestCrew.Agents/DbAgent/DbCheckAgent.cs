using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.Base;
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
///   <item>Resolves the connection string via
///     <see cref="IEnvironmentResolver.ResolveDbConnectionString"/>.</item>
///   <item>Runs the SELECT and asserts the result against either
///     <c>ExpectedRowCount</c> or <c>ColumnAssertions</c>, with NULL-aware
///     evaluation via <see cref="ColumnAssertionEvaluator"/>.</item>
///   <item>On green assertions, evaluates each <see cref="ColumnCapture"/> and
///     attaches the captured tokens to the step's <c>Metadata["capturedTokens"]</c>
///     so <c>PostStepOrchestrator</c> can merge them into the run context for
///     siblings (inline + deferred).</item>
/// </list>
///
/// On any failure (assertion or row-count) the full first row is captured into
/// <c>Metadata["dbCheckRow"]</c>; row-count failures additionally attach the
/// first three rows under <c>Metadata["dbCheckRows"]</c> for the run-detail UI.
/// </summary>
public class DbCheckAgent : BaseTestAgent
{
    private const int RowCellTruncate = 200;

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

        // Per-check connection — each entry can target a different logical DB.
        for (var i = 0; i < checks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RunOneAsync(checks[i], i + 1, envKey, steps, ct);
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

    private async Task RunOneAsync(
        DbCheckStepDefinition check,
        int index,
        string? envKey,
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

        var connectionString = _envResolver.ResolveDbConnectionString(check.ConnectionKey, envKey);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            steps.Add(TestStep.Err(action,
                $"DB connection key '{check.ConnectionKey}' is not configured for env '{envKey ?? "default"}'."));
            return;
        }

        await using var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(action,
                $"Failed to open connection for key '{check.ConnectionKey}' (env '{envKey ?? "default"}'): {ex.Message}"));
            return;
        }

        try
        {
            await using var cmd = new SqlCommand(check.Sql, conn)
            {
                CommandTimeout = Math.Max(1, check.TimeoutSeconds)
            };

            // ── Mode 1: row-count assertion (column-assertions list takes precedence) ──
            if (check.ColumnAssertions.Count == 0 && check.ExpectedRowCount is int expectedCount)
            {
                await RunRowCountModeAsync(check, action, cmd, expectedCount, steps, ct);
                return;
            }

            // ── Mode 2: per-column assertions (with optional captures) ──
            if (check.ColumnAssertions.Count > 0)
            {
                await RunColumnAssertionsModeAsync(check, action, cmd, steps, ct);
                return;
            }

            steps.Add(TestStep.Err(action,
                "DbCheck has neither ExpectedRowCount nor ColumnAssertions set — nothing to assert."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(action,
                $"DB check threw: {ex.Message}. SQL: {Preview(check.Sql)}"));
        }
    }

    private static async Task RunRowCountModeAsync(
        DbCheckStepDefinition check, string action, SqlCommand cmd,
        int expectedCount, List<TestStep> steps, CancellationToken ct)
    {
        var actualCount = 0;
        var sampleRows = new List<Dictionary<string, string?>>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (sampleRows.Count < 3) sampleRows.Add(ReadRow(reader));
                actualCount++;
            }
        }

        if (actualCount == expectedCount)
        {
            steps.Add(TestStep.Pass(action,
                $"Row count matched ({actualCount}). SQL: {Preview(check.Sql)}"));
            return;
        }

        var failStep = new TestStep
        {
            Action = action,
            Status = TestStatus.Failed,
            Summary = $"Expected {expectedCount} row(s), got {actualCount}. SQL: {Preview(check.Sql)}",
        };
        if (sampleRows.Count > 0)
        {
            failStep.Metadata["dbCheckRow"] = sampleRows[0];
            if (sampleRows.Count > 1) failStep.Metadata["dbCheckRows"] = sampleRows;
        }
        steps.Add(failStep);
    }

    private async Task RunColumnAssertionsModeAsync(
        DbCheckStepDefinition check, string action, SqlCommand cmd,
        List<TestStep> steps, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            steps.Add(TestStep.Fail(action,
                $"Expected at least one row matching {check.ColumnAssertions.Count} assertion(s), got no rows. SQL: {Preview(check.Sql)}"));
            return;
        }

        // Capture the full first row up front so failures can attach it without re-reading.
        var firstRow = ReadRow(reader);

        // Build a column→ordinal lookup once so JsonPath / repeated lookups are fast.
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;

        var failureReasons = new List<string>();
        foreach (var assertion in check.ColumnAssertions)
        {
            if (!ordinals.TryGetValue(assertion.Column, out var ordinal))
            {
                failureReasons.Add($"column '{assertion.Column}' missing from result set");
                continue;
            }

            var isDbNull = reader.IsDBNull(ordinal);
            var rawValue = isDbNull ? null : reader.GetValue(ordinal);

            var result = ColumnAssertionEvaluator.Evaluate(assertion, rawValue, isDbNull);
            if (!result.Passed)
                failureReasons.Add(result.Reason ?? $"column '{assertion.Column}' assertion failed");
        }

        if (failureReasons.Count == 0)
        {
            // Captures only run on green assertions: a failing assertion means the
            // row is wrong, so any captured value would be suspect. Documented in
            // ColumnCapture's XML and on the agent for reviewer reference.
            var passStep = new TestStep
            {
                Action = action,
                Status = TestStatus.Passed,
                Summary = $"All {check.ColumnAssertions.Count} assertion(s) passed on first row. SQL: {Preview(check.Sql)}",
            };

            if (check.Captures.Count > 0)
            {
                var capResult = EvaluateCaptures(check.Captures, reader, ordinals, action);
                if (capResult.FailureReason is not null)
                {
                    var capFail = new TestStep
                    {
                        Action = action,
                        Status = TestStatus.Failed,
                        Summary = $"Capture failed: {capResult.FailureReason}. SQL: {Preview(check.Sql)}",
                    };
                    capFail.Metadata["dbCheckRow"] = firstRow;
                    steps.Add(capFail);
                    return;
                }

                if (capResult.Captured.Count > 0)
                    passStep.Metadata["capturedTokens"] = capResult.Captured;
            }

            steps.Add(passStep);
            return;
        }

        var failStep = new TestStep
        {
            Action = action,
            Status = TestStatus.Failed,
            Summary = $"Column value mismatch: {string.Join("; ", failureReasons)}. SQL: {Preview(check.Sql)}",
        };
        failStep.Metadata["dbCheckRow"] = firstRow;
        steps.Add(failStep);
    }

    private CaptureResult EvaluateCaptures(
        List<ColumnCapture> captures,
        SqlDataReader reader,
        IReadOnlyDictionary<string, int> ordinals,
        string action)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in captures)
        {
            if (string.IsNullOrWhiteSpace(cap.As))
            {
                if (cap.Required)
                    return new CaptureResult(captured,
                        $"capture for column '{cap.Column}' has no 'As' token name");
                continue;
            }

            if (!ordinals.TryGetValue(cap.Column, out var ordinal))
            {
                if (cap.Required)
                    return new CaptureResult(captured,
                        $"capture column '{cap.Column}' missing from result set");
                Logger.LogWarning(
                    "{Action}: optional capture column '{Col}' missing — token '{{{{{Tok}}}}}' left undefined",
                    action, cap.Column, cap.As);
                continue;
            }

            var isDbNull = reader.IsDBNull(ordinal);

            if (!string.IsNullOrEmpty(cap.JsonPath))
            {
                if (isDbNull)
                {
                    if (cap.Required)
                        return new CaptureResult(captured,
                            $"capture column '{cap.Column}' is NULL — cannot extract JSON path '{cap.JsonPath}'");
                    Logger.LogWarning(
                        "{Action}: optional capture column '{Col}' is NULL — token '{{{{{Tok}}}}}' left undefined",
                        action, cap.Column, cap.As);
                    continue;
                }

                var rawText = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                var status = JsonValueExtractor.TryExtract(rawText, cap.JsonPath!, out var node, out var err);
                if (status == JsonValueExtractor.ExtractionStatus.Found)
                {
                    captured[cap.As] = JsonValueExtractor.ToScalarString(node!);
                    continue;
                }

                if (cap.Required)
                    return new CaptureResult(captured,
                        $"capture for column '{cap.Column}': {err ?? $"JSON path '{cap.JsonPath}' did not resolve"}");

                Logger.LogWarning(
                    "{Action}: optional capture for '{Col}.{Path}' did not resolve — token '{{{{{Tok}}}}}' left undefined",
                    action, cap.Column, cap.JsonPath, cap.As);
                continue;
            }

            if (isDbNull)
            {
                if (cap.Required)
                    return new CaptureResult(captured,
                        $"capture column '{cap.Column}' is NULL");
                Logger.LogWarning(
                    "{Action}: optional capture column '{Col}' is NULL — token '{{{{{Tok}}}}}' left undefined",
                    action, cap.Column, cap.As);
                continue;
            }

            captured[cap.As] = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
        }

        return new CaptureResult(captured, null);
    }

    private static Dictionary<string, string?> ReadRow(SqlDataReader reader)
    {
        var row = new Dictionary<string, string?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (reader.IsDBNull(i))
            {
                row[name] = null;
                continue;
            }
            var raw = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            row[name] = raw.Length <= RowCellTruncate ? raw : raw[..RowCellTruncate] + "…";
        }
        return row;
    }

    private static string Preview(string sql)
    {
        var single = new StringBuilder(sql.Length);
        foreach (var c in sql) single.Append(c == '\n' || c == '\r' ? ' ' : c);
        var s = single.ToString().Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

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

    private readonly record struct CaptureResult(Dictionary<string, string> Captured, string? FailureReason);
}
