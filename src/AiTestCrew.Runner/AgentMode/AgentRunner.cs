using AiTestCrew.Core.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AiTestCrew.Runner.AgentMode;

/// <summary>
/// Long-running worker that polls the server for queued jobs, executes them locally
/// via the existing <see cref="JobExecutor"/>, and reports progress + terminal results.
/// </summary>
internal sealed class AgentRunner
{
    private readonly AgentClient _client;
    private readonly JobExecutor _executor;
    private readonly TestEnvironmentConfig _config;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly string[] _capabilities;
    private readonly string _agentIdFilePath;

    public AgentRunner(AgentClient client, JobExecutor executor, TestEnvironmentConfig config,
        ILogger logger, string name, string[] capabilities)
    {
        _client = client;
        _executor = executor;
        _config = config;
        _logger = logger;
        _name = name;
        _capabilities = capabilities;
        _agentIdFilePath = Path.Combine(AppContext.BaseDirectory, ".agent-id");
    }

    // Shared between the polling loop and the parallel heartbeat loop.
    // The heartbeat loop reads it to report the right status while a job is executing,
    // and writes to it isn't needed from heartbeat's side — only the poll loop mutates it.
    private volatile string _currentStatus = "Online";

    public async Task RunAsync(CancellationToken ct)
    {
        var version = typeof(AgentRunner).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var existingId = ReadAgentId();
        var agentId = await _client.RegisterAsync(existingId, _name, _capabilities, version);
        WriteAgentId(agentId);

        AnsiConsole.MarkupLine($"[green]Registered[/] as [bold]{Markup.Escape(agentId)}[/] ({Markup.Escape(_name)})");
        AnsiConsole.MarkupLine($"[grey]Capabilities:[/] {Markup.Escape(string.Join(", ", _capabilities))}");
        AnsiConsole.MarkupLine($"[grey]Server:[/] {Markup.Escape(_config.ServerUrl)}");
        AnsiConsole.MarkupLine("[grey]Polling for jobs — press Ctrl+C to stop.[/]\n");

        var heartbeatInterval = TimeSpan.FromSeconds(_config.AgentHeartbeatIntervalSeconds > 0
            ? _config.AgentHeartbeatIntervalSeconds : 30);
        var pollInterval = TimeSpan.FromSeconds(_config.AgentPollIntervalSeconds > 0
            ? _config.AgentPollIntervalSeconds : 10);

        // Parallel heartbeat loop — keeps ticking even when the polling loop is blocked
        // inside a stuck recording. This is what makes force-quit from the dashboard work:
        // a stuck agent still calls heartbeat, sees shouldExit=true, and self-terminates.
        _ = Task.Run(() => HeartbeatLoopAsync(agentId, heartbeatInterval, ct), ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                NextJobResponse? job;
                try
                {
                    job = await _client.NextJobAsync(agentId, _capabilities);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Poll failed: {Message}", ex.Message);
                    try { await Task.Delay(pollInterval, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }

                if (job is null)
                {
                    try { await Task.Delay(pollInterval, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }

                // Claimed a job — execute
                _currentStatus = "Busy";

                var kindLabel = string.IsNullOrWhiteSpace(job.JobKind) || job.JobKind == "Run" ? job.Mode : job.JobKind;
                AnsiConsole.MarkupLine($"[cyan]Claimed job[/] {Markup.Escape(job.JobId)} " +
                    $"([bold]{Markup.Escape(job.TargetType)}[/] / {Markup.Escape(kindLabel)}) " +
                    $"testset=[yellow]{Markup.Escape(job.TestSetId)}[/]");

                try
                {
                    await _client.ReportProgressAsync(job.JobId);
                    var outcome = await _executor.ExecuteAsync(job, ct);
                    await _client.ReportResultAsync(job.JobId, outcome.Success, outcome.Error);
                    AnsiConsole.MarkupLine(outcome.Success
                        ? $"[green]✓ Job {Markup.Escape(job.JobId)} completed[/] — {Markup.Escape(outcome.Summary)}"
                        : $"[red]✗ Job {Markup.Escape(job.JobId)} failed[/] — {Markup.Escape(outcome.Summary)}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job {JobId} execution failed", job.JobId);
                    try { await _client.ReportResultAsync(job.JobId, false, ex.Message); }
                    catch (Exception reportEx) { _logger.LogWarning("Failed to report result: {Msg}", reportEx.Message); }
                    AnsiConsole.MarkupLine($"[red]✗ Job {Markup.Escape(job.JobId)} error:[/] {Markup.Escape(ex.Message)}");
                }
                finally
                {
                    _currentStatus = "Online";
                }
            }
        }
        finally
        {
            try
            {
                AnsiConsole.MarkupLine("\n[grey]Deregistering agent...[/]");
                await _client.DeregisterAsync(agentId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Deregister failed: {Message}", ex.Message);
            }
        }
    }

    // Runs on its own task so a blocked Playwright/FlaUI session cannot stop heartbeats.
    // This is the only reliable way to receive a "shouldExit" signal while a recording hangs.
    private async Task HeartbeatLoopAsync(string agentId, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _client.HeartbeatAsync(agentId, _currentStatus);
                if (res.ShouldExit)
                {
                    AnsiConsole.MarkupLine("\n[red]Force-quit received from dashboard — terminating now.[/]");
                    _logger.LogWarning("Agent {AgentId} force-quit from server; Environment.Exit(1)", agentId);
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Heartbeat failed: {Message}", ex.Message);
            }

            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private string? ReadAgentId()
    {
        try
        {
            return File.Exists(_agentIdFilePath) ? File.ReadAllText(_agentIdFilePath).Trim() : null;
        }
        catch { return null; }
    }

    private void WriteAgentId(string agentId)
    {
        try { File.WriteAllText(_agentIdFilePath, agentId); }
        catch (Exception ex) { _logger.LogWarning("Could not persist agent id: {Message}", ex.Message); }
    }
}
