using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", (RunRequest request, RunTracker tracker,
            TestOrchestrator orchestrator, ILogger<TestOrchestrator> logger) =>
        {
            // Validate request
            if (string.IsNullOrWhiteSpace(request.Mode))
                return Results.BadRequest(new { error = "mode is required" });

            if (!Enum.TryParse<RunMode>(request.Mode, true, out var mode) || mode == RunMode.List)
                return Results.BadRequest(new { error = "mode must be Normal, Reuse, or Rebaseline" });

            if (mode == RunMode.Reuse && string.IsNullOrWhiteSpace(request.TestSetId))
                return Results.BadRequest(new { error = "testSetId is required for Reuse mode" });

            if (mode != RunMode.Reuse && string.IsNullOrWhiteSpace(request.Objective))
                return Results.BadRequest(new { error = "objective is required for Normal/Rebaseline mode" });

            // Only one run at a time
            if (tracker.HasActiveRun())
                return Results.Conflict(new { error = "A test run is already in progress" });

            var runId = Guid.NewGuid().ToString("N")[..12];
            var objective = mode == RunMode.Reuse ? "" : request.Objective!;
            tracker.Create(runId, objective, request.Mode, request.TestSetId);

            // Fire and forget — the orchestrator persists results via ExecutionHistoryRepository
            _ = Task.Run(async () =>
            {
                try
                {
                    var reuseId = mode == RunMode.Reuse ? request.TestSetId : null;
                    var result = await orchestrator.RunAsync(objective, mode, reuseId, externalRunId: runId);
                    var testSetId = mode == RunMode.Reuse
                        ? request.TestSetId!
                        : TestSetRepository.SlugFromObjective(result.Objective);
                    tracker.Complete(runId, testSetId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background test run {RunId} failed", runId);
                    tracker.Fail(runId, ex.Message);
                }
            });

            return Results.Accepted($"/api/runs/{runId}/status", new
            {
                runId,
                status = "Running",
                startedAt = DateTime.UtcNow
            });
        });

        group.MapGet("/{runId}/status", (string runId, RunTracker tracker) =>
        {
            var status = tracker.Get(runId);
            if (status is null) return Results.NotFound(new { error = $"Run '{runId}' not found" });
            return Results.Ok(status);
        });

        return group;
    }
}

public record RunRequest(string? Objective, string Mode, string? TestSetId);
