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
        var nextHeartbeat = DateTime.UtcNow + heartbeatInterval;

        var status = "Online";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Heartbeat if due
                if (DateTime.UtcNow >= nextHeartbeat)
                {
                    await SafeHeartbeatAsync(agentId, status);
                    nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
                }

                NextJobResponse? job;
                try
                {
                    job = await _client.NextJobAsync(agentId, _capabilities);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Poll failed: {Message}", ex.Message);
                    await Task.Delay(pollInterval, ct);
                    continue;
                }

                if (job is null)
                {
                    try { await Task.Delay(pollInterval, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }

                // Claimed a job — execute
                status = "Busy";
                await SafeHeartbeatAsync(agentId, status);
                nextHeartbeat = DateTime.UtcNow + heartbeatInterval;

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
                    status = "Online";
                    await SafeHeartbeatAsync(agentId, status);
                    nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
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

    private async Task SafeHeartbeatAsync(string agentId, string status)
    {
        try { await _client.HeartbeatAsync(agentId, status); }
        catch (Exception ex) { _logger.LogWarning("Heartbeat failed: {Message}", ex.Message); }
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
