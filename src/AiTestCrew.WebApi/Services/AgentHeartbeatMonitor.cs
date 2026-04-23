using System.Text.Json;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Background service that keeps queue + agent state healthy:
///   (a) marks agents Offline when their last heartbeat is stale;
///   (b) reclaims Claimed queue entries whose owning agent has gone silent so
///       another agent can re-execute them — critical for deferred-verification
///       retries which would otherwise stall indefinitely;
///   (c) expires deferred-verification rows whose deadline has passed without a
///       final attempt completing, and finalises their parent runs as Failed.
/// Runs on a fixed interval; each sub-task is independent and isolated by try/catch.
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
            await SweepAgentsAsync();
            await SweepStaleQueueClaimsAsync();
            await SweepExpiredPendingVerificationsAsync();

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAgentsAsync()
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
            _logger.LogError(ex, "AgentHeartbeatMonitor: agent sweep failed");
        }
    }

    /// <summary>
    /// Reclaims run_queue entries stuck in <c>Claimed</c> whose owning agent has not
    /// heartbeat within twice the configured timeout. Resets to <c>Queued</c> so any
    /// compatible agent can pick them up. VerifyOnly work is idempotent (assertions
    /// only) so re-running is safe.
    /// </summary>
    private async Task SweepStaleQueueClaimsAsync()
    {
        try
        {
            var queueRepo = _sp.GetService<IRunQueueRepository>();
            if (queueRepo is null) return;  // not in SQLite mode

            // Two heartbeats' grace: a miss followed by a miss.
            var staleAfter = TimeSpan.FromTicks(_timeout.Ticks * 2);
            var stale = await queueRepo.ListStaleClaimsAsync(staleAfter);
            foreach (var entry in stale)
            {
                if (await queueRepo.ReleaseClaimAsync(entry.Id))
                {
                    _logger.LogWarning(
                        "Reclaimed stale queue entry {Id} (agent {Agent} silent since {ClaimedAt:O}) — back to Queued.",
                        entry.Id, entry.ClaimedBy, entry.ClaimedAt);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentHeartbeatMonitor: stale-claim sweep failed");
        }
    }

    /// <summary>
    /// Finds deferred-verification rows whose <c>deadline_at + VerificationMaxLatencySeconds</c>
    /// has passed while still in <c>Pending</c>, marks them Failed, and invokes
    /// the same finalise path that the agent would on a normal deadline miss. Protects
    /// against a run parking indefinitely when every capable agent is offline.
    /// </summary>
    private async Task SweepExpiredPendingVerificationsAsync()
    {
        try
        {
            var pendingRepo = _sp.GetService<IPendingVerificationRepository>();
            var historyRepo = _sp.GetService<IExecutionHistoryRepository>();
            var config = _sp.GetRequiredService<TestEnvironmentConfig>();
            if (pendingRepo is null || historyRepo is null) return;

            var maxLatency = TimeSpan.FromSeconds(Math.Max(60, config.AseXml.VerificationMaxLatencySeconds));
            var cutoff = DateTime.UtcNow - maxLatency;

            // ListExpiredAsync takes a cutoff — rows are expired when deadline_at <= cutoff.
            // We want: deadline_at + maxLatency <= UtcNow, i.e. deadline_at <= UtcNow - maxLatency.
            var expired = await pendingRepo.ListExpiredAsync(cutoff);
            foreach (var p in expired)
            {
                var resultJson = JsonSerializer.Serialize(new
                {
                    objectiveId = p.DeliveryObjectiveId,
                    objectiveName = p.DeliveryObjectiveId,
                    status = "Failed",
                    passedSteps = 0,
                    failedSteps = 1,
                    totalSteps = 1,
                    steps = new[]
                    {
                        new
                        {
                            action = "deferred-verify-timeout",
                            summary = $"No agent claimed this verification within {maxLatency.TotalMinutes:F0} minutes after deadline.",
                            status = "Failed",
                            detail = (string?)null,
                            duration = TimeSpan.Zero,
                            timestamp = DateTime.UtcNow,
                        }
                    },
                });
                await pendingRepo.MarkFailedAsync(p.PendingId, resultJson, p.AttemptLogJson ?? "[]");

                _logger.LogWarning(
                    "Expired deferred-verification {PendingId} for run {RunId} — no agent claimed it in time.",
                    p.PendingId, p.ParentRunId);

                // Finalise the parent run if no more pending rows remain.
                try
                {
                    var stillPending = await pendingRepo.CountPendingForRunAsync(p.ParentRunId);
                    if (stillPending > 0) continue;

                    var run = await historyRepo.GetRunAsync(p.TestSetId, p.ParentRunId);
                    if (run is null) continue;

                    var allForRun = await pendingRepo.ListForRunAsync(p.ParentRunId);
                    foreach (var terminal in allForRun)
                    {
                        if (!string.IsNullOrWhiteSpace(terminal.ResultJson))
                            ApplyExpiredToRun(run, terminal);
                    }

                    var hasError = run.ObjectiveResults.Any(o => o.Status == "Error");
                    var hasFailed = run.ObjectiveResults.Any(o => o.Status == "Failed");
                    run.Status = hasError ? "Error" : hasFailed ? "Failed" : "Passed";
                    run.PassedObjectives = run.ObjectiveResults.Count(o => o.Status == "Passed");
                    run.FailedObjectives = run.ObjectiveResults.Count(o => o.Status == "Failed");
                    run.ErrorObjectives = run.ObjectiveResults.Count(o => o.Status == "Error");
                    run.CompletedAt = DateTime.UtcNow;

                    await historyRepo.SaveAsync(run);
                    _logger.LogInformation(
                        "Finalised run {RunId} as {Status} after janitor expired {Count} deferred verification(s).",
                        p.ParentRunId, run.Status, allForRun.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Janitor failed to finalise run {RunId}", p.ParentRunId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentHeartbeatMonitor: pending-verification sweep failed");
        }
    }

    private static void ApplyExpiredToRun(PersistedExecutionRun run, PendingVerification pending)
    {
        var obj = run.ObjectiveResults
            .FirstOrDefault(o => string.Equals(o.ObjectiveId, pending.DeliveryObjectiveId, StringComparison.OrdinalIgnoreCase));
        if (obj is null) return;

        obj.Steps.RemoveAll(s => string.Equals(s.Status, "AwaitingVerification", StringComparison.OrdinalIgnoreCase));
        obj.Steps.Add(new PersistedStepResult
        {
            Action = "verification-rollup",
            Summary = $"Expired by janitor — {pending.AttemptCount} attempt(s), final status {pending.Status}",
            Status = pending.Status == "Cancelled" ? "Skipped" : "Failed",
            Timestamp = pending.CompletedAt ?? DateTime.UtcNow,
        });

        obj.Status = pending.Status == "Cancelled" ? "Skipped" : "Failed";
        obj.PassedSteps = obj.Steps.Count(s => s.Status == "Passed");
        obj.FailedSteps = obj.Steps.Count(s => s.Status == "Failed");
        obj.TotalSteps = obj.Steps.Count;
        obj.CompletedAt = pending.CompletedAt ?? DateTime.UtcNow;
    }
}
