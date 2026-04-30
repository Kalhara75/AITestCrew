using System.Diagnostics;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Agents.DataPack;

/// <summary>
/// Default <see cref="IDataPackRunner"/>. Walks <see cref="DataPackRegistry"/>'s
/// plan and executes every <c>.sql</c> file against the matching environment's
/// Bravo DB. Each script is split on <c>GO</c> by <see cref="SqlBatchSplitter"/>
/// and the resulting batches run as separate <see cref="SqlCommand"/>s in
/// per-batch autocommit (no outer transaction).
///
/// Different trust boundary from <see cref="Teardown.BravoTeardownExecutor"/>:
/// data-pack scripts are dev-authored and version-controlled, so they bypass
/// <see cref="Teardown.SqlGuardrails"/>. They intentionally use <c>EXEC</c>,
/// <c>CREATE/ALTER PROCEDURE</c>, and unbounded <c>DELETE</c>.
/// </summary>
public sealed class DataPackRunner : IDataPackRunner
{
    private readonly TestEnvironmentConfig _config;
    private readonly IEnvironmentResolver _envResolver;
    private readonly ILogger<DataPackRunner> _logger;

    public DataPackStartupReport? LatestReport { get; private set; }

    public DataPackRunner(
        TestEnvironmentConfig config,
        IEnvironmentResolver envResolver,
        ILogger<DataPackRunner> logger)
    {
        _config = config;
        _envResolver = envResolver;
        _logger = logger;
    }

    public async Task<DataPackRunSummary> RunAllAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var envsTouched = new List<string>();
        var errors = new List<string>();
        var envReports = new List<DataPackEnvReport>();
        int envsConsidered = 0;
        int envsRan = 0;
        int envsSkipped = 0;
        int scriptsExecuted = 0;
        int batchesExecuted = 0;
        int failures = 0;

        var rootAbsolute = ResolveRoot();
        var rootExists = Directory.Exists(rootAbsolute);
        _logger.LogInformation(
            "DataPackRunner: scanning '{Root}' for envs configured to run on startup.",
            rootAbsolute);

        var plan = DataPackRegistry.Discover(rootAbsolute, _logger);

        var configuredEnvs = new HashSet<string>(
            _envResolver.ListKeys(), StringComparer.OrdinalIgnoreCase);

        foreach (var envPlan in plan.Envs)
        {
            envsConsidered++;
            var envKey = envPlan.EnvKey;
            int totalScripts = envPlan.Phases.Sum(p => p.Scripts.Count);

            if (!configuredEnvs.Contains(envKey))
            {
                _logger.LogWarning(
                    "DataPackRunner: env folder '{Env}' has no matching entry in TestEnvironment.Environments — skipping.",
                    envKey);
                envReports.Add(SkippedReport(
                    envKey, DataPackEnvStatus.SkippedNotConfigured,
                    "Env folder exists on disk but has no matching entry in TestEnvironment.Environments.",
                    totalScripts));
                envsSkipped++;
                continue;
            }

            if (!_envResolver.ResolveRunDataPacksOnStartup(envKey))
            {
                _logger.LogInformation(
                    "DataPackRunner: env={Env} opt-in OFF — skipping.", envKey);
                envReports.Add(SkippedReport(
                    envKey, DataPackEnvStatus.SkippedOptOut,
                    "RunDataPacksOnStartup is false for this env.",
                    totalScripts));
                envsSkipped++;
                continue;
            }

            var connectionString = _envResolver.ResolveBravoDbConnectionString(envKey);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogInformation(
                    "DataPackRunner: env={Env} has no Bravo DB connection string — skipping.",
                    envKey);
                envReports.Add(SkippedReport(
                    envKey, DataPackEnvStatus.SkippedNoConnection,
                    "BravoDbConnectionString is empty for this env.",
                    totalScripts));
                envsSkipped++;
                continue;
            }

            _logger.LogWarning(
                "DATAPACKS: about to execute {Count} script(s) against env={Env}. THIS IS DESTRUCTIVE.",
                totalScripts, envKey);

            var envReport = await RunEnvAsync(envPlan, connectionString, ct);
            envReports.Add(envReport);

            scriptsExecuted += envReport.ScriptsExecuted;
            batchesExecuted += envReport.BatchesExecuted;
            failures += envReport.Failures;
            if (envReport.Error is not null) errors.Add(envReport.Error);
            errors.AddRange(envReport.Scripts
                .Where(s => s.Error is not null)
                .Select(s => $"{envKey}/{s.RelativePath}: {s.Error}"));
            envsTouched.Add(envKey);
            envsRan++;
        }

        sw.Stop();
        _logger.LogInformation(
            "DataPackRunner finished: envsConsidered={Considered} envsRan={Ran} envsSkipped={Skipped} scripts={Scripts} batches={Batches} failures={Failures} elapsed={Elapsed}",
            envsConsidered, envsRan, envsSkipped, scriptsExecuted,
            batchesExecuted, failures, sw.Elapsed);

        LatestReport = new DataPackStartupReport(
            DateTime.UtcNow, rootAbsolute, rootExists, sw.Elapsed, envReports);

        return new DataPackRunSummary(
            envsConsidered, envsRan, envsSkipped,
            scriptsExecuted, batchesExecuted, failures,
            sw.Elapsed, envsTouched, errors);
    }

    private async Task<DataPackEnvReport> RunEnvAsync(
        DataPackEnvPlan envPlan, string connectionString, CancellationToken ct)
    {
        int totalScripts = envPlan.Phases.Sum(p => p.Scripts.Count);
        var scriptReports = new List<DataPackScriptReport>(totalScripts);

        SqlConnection? conn = null;
        try
        {
            conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to open SQL connection: {ex.Message}";
            _logger.LogError(ex,
                "DataPackRunner: env={Env} failed to open SQL connection — {Msg}",
                envPlan.EnvKey, ex.Message);
            conn?.Dispose();

            // Mark every planned script as Skipped because we never opened a connection.
            foreach (var phase in envPlan.Phases)
                foreach (var script in phase.Scripts)
                    scriptReports.Add(new DataPackScriptReport(
                        phase.Name, script.Subfolder, script.RelativePath,
                        DataPackScriptStatus.Skipped, 0, 0,
                        "Skipped because the SQL connection could not be opened."));

            return new DataPackEnvReport(
                envPlan.EnvKey, DataPackEnvStatus.ConnectionFailed,
                null, msg, totalScripts, 0, 0, 1, scriptReports);
        }

        int scriptsExecuted = 0;
        int batchesExecuted = 0;
        int failures = 0;
        bool aborted = false;

        try
        {
            foreach (var phase in envPlan.Phases)
            {
                foreach (var script in phase.Scripts)
                {
                    if (aborted)
                    {
                        scriptReports.Add(new DataPackScriptReport(
                            phase.Name, script.Subfolder, script.RelativePath,
                            DataPackScriptStatus.Skipped, 0, 0,
                            "Skipped because an earlier script in this env failed."));
                        continue;
                    }

                    var scriptSw = Stopwatch.StartNew();
                    string text;
                    try
                    {
                        text = await File.ReadAllTextAsync(script.FullPath, ct);
                    }
                    catch (Exception ex)
                    {
                        scriptSw.Stop();
                        var msg = $"Failed to read script: {ex.Message}";
                        _logger.LogError(ex,
                            "DataPackRunner: env={Env} phase={Phase} script={Script} read failed — {Msg}",
                            envPlan.EnvKey, phase.Name, script.RelativePath, ex.Message);
                        scriptReports.Add(new DataPackScriptReport(
                            phase.Name, script.Subfolder, script.RelativePath,
                            DataPackScriptStatus.Failed, 0, scriptSw.ElapsedMilliseconds, msg));
                        failures++;
                        aborted = true;
                        continue;
                    }

                    var fileBatches = SqlBatchSplitter.Split(StripBom(text));
                    int batchIndex = 0;
                    string? batchError = null;
                    foreach (var batch in fileBatches)
                    {
                        try
                        {
                            await using var cmd = new SqlCommand(batch, conn);
                            cmd.CommandTimeout = 0;
                            await cmd.ExecuteNonQueryAsync(ct);
                            batchesExecuted++;
                            batchIndex++;
                        }
                        catch (Exception ex)
                        {
                            batchError = $"Batch #{batchIndex}: {ex.Message}";
                            _logger.LogError(ex,
                                "DataPackRunner: env={Env} phase={Phase} script={Script} batch#{Batch} failed — {Msg}",
                                envPlan.EnvKey, phase.Name, script.RelativePath,
                                batchIndex, ex.Message);
                            break;
                        }
                    }

                    scriptSw.Stop();

                    if (batchError is null)
                    {
                        scriptsExecuted++;
                        scriptReports.Add(new DataPackScriptReport(
                            phase.Name, script.Subfolder, script.RelativePath,
                            DataPackScriptStatus.Success,
                            fileBatches.Count, scriptSw.ElapsedMilliseconds, null));
                        _logger.LogInformation(
                            "datapacks: env={Env} phase={Phase} script={Script} batches={Batches} elapsedMs={ElapsedMs}",
                            envPlan.EnvKey, phase.Name, script.RelativePath,
                            fileBatches.Count, scriptSw.ElapsedMilliseconds);
                    }
                    else
                    {
                        failures++;
                        aborted = true;
                        scriptReports.Add(new DataPackScriptReport(
                            phase.Name, script.Subfolder, script.RelativePath,
                            DataPackScriptStatus.Failed,
                            batchIndex, scriptSw.ElapsedMilliseconds, batchError));
                    }
                }
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }

        return new DataPackEnvReport(
            envPlan.EnvKey, DataPackEnvStatus.Ran,
            null, null, totalScripts, scriptsExecuted, batchesExecuted, failures, scriptReports);
    }

    private static DataPackEnvReport SkippedReport(
        string envKey, string status, string reason, int totalScripts)
    {
        return new DataPackEnvReport(
            envKey, status, reason, null, totalScripts, 0, 0, 0,
            Array.Empty<DataPackScriptReport>());
    }

    private string ResolveRoot()
    {
        var path = string.IsNullOrWhiteSpace(_config.DataPacksPath)
            ? "datapacks"
            : _config.DataPacksPath;

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string StripBom(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            return text.Substring(1);
        return text;
    }
}
