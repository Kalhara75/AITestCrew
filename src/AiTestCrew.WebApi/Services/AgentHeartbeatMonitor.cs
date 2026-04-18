using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Background service that marks agents Offline when their last heartbeat is stale.
/// Runs on a fixed interval independent of the configured timeout, since the DB update is cheap.
/// </summary>
public sealed class AgentHeartbeatMonitor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AgentHeartbeatMonitor> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(30);

    public AgentHeartbeatMonitor(IServiceProvider sp, ILogger<AgentHeartbeatMonitor> logger, TimeSpan timeout)
    {
        _sp = sp;
        _logger = logger;
        _timeout = timeout;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var repo = _sp.GetRequiredService<IAgentRepository>();
                var changed = await repo.MarkStaleOfflineAsync(_timeout);
                if (changed > 0)
                    _logger.LogInformation("Marked {Count} stale agent(s) Offline", changed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AgentHeartbeatMonitor tick failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
