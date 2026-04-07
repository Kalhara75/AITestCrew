using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", (RunRequest request, RunTracker tracker, ModuleRunTracker moduleRunTracker,
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

            // Module-scoped Normal mode requires both moduleId and testSetId
            if (!string.IsNullOrWhiteSpace(request.ModuleId) && string.IsNullOrWhiteSpace(request.TestSetId))
                return Results.BadRequest(new { error = "testSetId is required when moduleId is specified" });

            // Single-objective execution requires testSetId and Reuse mode
            if (!string.IsNullOrWhiteSpace(request.ObjectiveId))
            {
                if (string.IsNullOrWhiteSpace(request.TestSetId))
                    return Results.BadRequest(new { error = "testSetId is required when objectiveId is specified" });
                if (mode != RunMode.Reuse)
                    return Results.BadRequest(new { error = "objectiveId is only supported in Reuse mode" });
            }

            // Only one run at a time
            if (tracker.HasActiveRun() || moduleRunTracker.HasActiveModuleRun())
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
                    var result = await orchestrator.RunAsync(
                        objective, mode, reuseId,
                        externalRunId: runId,
                        moduleId: request.ModuleId,
                        targetTestSetId: request.TestSetId,
                        objectiveName: request.ObjectiveName,
                        objectiveId: request.ObjectiveId);
                    var testSetId = mode == RunMode.Reuse
                        ? request.TestSetId!
                        : !string.IsNullOrWhiteSpace(request.TestSetId)
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

        // GET /api/runs/active — check for any active run (module-level or individual)
        group.MapGet("/active", (RunTracker tracker, ModuleRunTracker moduleRunTracker) =>
        {
            var moduleRun = moduleRunTracker.GetActiveRun();
            if (moduleRun is not null)
                return Results.Ok(new { type = "module", moduleRun, run = (RunStatus?)null });

            var activeRun = tracker.GetActiveRun();
            if (activeRun is not null)
                return Results.Ok(new { type = "testset", moduleRun = (ModuleRunStatus?)null, run = activeRun });

            return Results.Ok(new { type = (string?)null, moduleRun = (ModuleRunStatus?)null, run = (RunStatus?)null });
        });

        return group;
    }
}

public record RunRequest(string? Objective, string? ObjectiveName, string Mode, string? TestSetId, string? ModuleId, string? ObjectiveId);
