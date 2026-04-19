using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Utilities;

namespace AiTestCrew.Agents.Teardown;

/// <summary>
/// Executes user-defined SQL teardown statements against the Bravo application
/// DB, one per teardown step. Connection string comes from
/// <see cref="IEnvironmentResolver.ResolveBravoDbConnectionString"/> (same path
/// as <c>BravoEndpointResolver</c>) so each customer environment hits its own
/// DB.
///
/// Safety stack:
/// <list type="number">
///   <item><description>Per-env opt-in via <see cref="IEnvironmentResolver.ResolveDataTeardownEnabled"/> — enforced before any connection opens.</description></item>
///   <item><description><see cref="SqlGuardrails.Validate"/> — WHERE required, destructive keyword denylist.</description></item>
///   <item><description><c>TokenSubstituter.Substitute(..., throwOnMissing: true)</c> — unknown <c>{{Token}}</c> fails the step.</description></item>
///   <item><description>Every executed statement is logged at Information with env, step name, substituted SQL, and rows affected — audit trail.</description></item>
/// </list>
/// </summary>
public sealed class BravoTeardownExecutor : ITeardownExecutor
{
    private readonly IEnvironmentResolver _envResolver;
    private readonly ILogger<BravoTeardownExecutor> _logger;

    public BravoTeardownExecutor(
        IEnvironmentResolver envResolver,
        ILogger<BravoTeardownExecutor> logger)
    {
        _envResolver = envResolver;
        _logger = logger;
    }

    public async Task<TeardownResult> ExecuteAsync(
        IReadOnlyList<SqlTeardownStepDto> steps,
        IReadOnlyDictionary<string, string> context,
        string environmentKey,
        bool dryRun,
        CancellationToken ct = default)
    {
        var result = new TeardownResult { Success = true };

        if (steps.Count == 0) return result;

        if (!_envResolver.ResolveDataTeardownEnabled(environmentKey))
        {
            result.Success = false;
            result.Error =
                $"Data teardown is not enabled for environment '{environmentKey}'. " +
                "Set TestEnvironment.Environments.<env>.DataTeardownEnabled = true " +
                "(or the top-level TestEnvironment.DataTeardownEnabled) to opt in.";
            _logger.LogError("{Error}", result.Error);
            return result;
        }

        // Validate every step BEFORE opening a connection — fail fast and don't
        // execute some steps while leaving others rejected (would leave DB in
        // an unknown state).
        var substituted = new List<(SqlTeardownStepDto Step, string Sql)>(steps.Count);
        foreach (var step in steps)
        {
            string sql;
            try
            {
                sql = TokenSubstituter.Substitute(step.Sql, context, throwOnMissing: true) ?? "";
            }
            catch (TokenSubstitutionException ex)
            {
                result.Success = false;
                result.Steps.Add(new TeardownStepResult
                {
                    Name = step.Name,
                    Sql = step.Sql,
                    Error = ex.Message
                });
                _logger.LogError(ex, "Teardown step '{Name}' failed token substitution (env={Env}).", step.Name, environmentKey);
                return result;
            }

            var (ok, reason) = SqlGuardrails.Validate(sql);
            if (!ok)
            {
                result.Success = false;
                result.Steps.Add(new TeardownStepResult
                {
                    Name = step.Name,
                    Sql = sql,
                    Error = reason
                });
                _logger.LogError(
                    "Teardown step '{Name}' rejected by guardrail (env={Env}): {Reason}",
                    step.Name, environmentKey, reason);
                return result;
            }

            substituted.Add((step, sql));
        }

        if (dryRun)
        {
            foreach (var (step, sql) in substituted)
            {
                _logger.LogInformation(
                    "[teardown dry-run] env={Env} step='{Name}' sql={Sql}",
                    environmentKey, step.Name, sql);
                result.Steps.Add(new TeardownStepResult
                {
                    Name = step.Name,
                    Sql = sql,
                    DryRun = true
                });
            }
            return result;
        }

        var connectionString = _envResolver.ResolveBravoDbConnectionString(environmentKey);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            result.Success = false;
            result.Error =
                $"Bravo DB connection string is not configured for environment '{environmentKey}'. " +
                "Set TestEnvironment.Environments.<env>.BravoDbConnectionString or " +
                "TestEnvironment.AseXml.BravoDb.ConnectionString.";
            _logger.LogError("{Error}", result.Error);
            return result;
        }

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        foreach (var (step, sql) in substituted)
        {
            try
            {
                await using var cmd = new SqlCommand(sql, conn);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                result.Steps.Add(new TeardownStepResult
                {
                    Name = step.Name,
                    Sql = sql,
                    RowsAffected = rows
                });
                _logger.LogInformation(
                    "[teardown] env={Env} step='{Name}' rows={Rows} sql={Sql}",
                    environmentKey, step.Name, rows, sql);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Steps.Add(new TeardownStepResult
                {
                    Name = step.Name,
                    Sql = sql,
                    Error = ex.Message
                });
                _logger.LogError(ex,
                    "[teardown] env={Env} step='{Name}' failed — aborting remaining teardown steps.",
                    environmentKey, step.Name);
                return result;
            }
        }

        return result;
    }
}
