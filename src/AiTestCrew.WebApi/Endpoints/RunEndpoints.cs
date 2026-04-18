using System.Text.Json;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", async (RunRequest request, IRunTracker tracker, IModuleRunTracker moduleRunTracker,
            TestOrchestrator orchestrator, ITestSetRepository tsRepo,
            IRunQueueRepository? queueRepo, HttpContext ctx,
            ILogger<TestOrchestrator> logger) =>
        {
            // Validate request
            if (string.IsNullOrWhiteSpace(request.Mode))
                return Results.BadRequest(new { error = "mode is required" });

            if (!Enum.TryParse<RunMode>(request.Mode, true, out var mode) || mode == RunMode.List)
                return Results.BadRequest(new { error = "mode must be Normal, Reuse, Rebaseline, or VerifyOnly" });

            if (mode is RunMode.Reuse or RunMode.VerifyOnly && string.IsNullOrWhiteSpace(request.TestSetId))
                return Results.BadRequest(new { error = "testSetId is required for Reuse/VerifyOnly mode" });

            if (mode is not RunMode.Reuse and not RunMode.VerifyOnly && string.IsNullOrWhiteSpace(request.Objective))
                return Results.BadRequest(new { error = "objective is required for Normal/Rebaseline mode" });

            if (mode == RunMode.VerifyOnly && string.IsNullOrWhiteSpace(request.ObjectiveId))
                return Results.BadRequest(new { error = "objectiveId is required for VerifyOnly mode" });

            // Module-scoped Normal mode requires both moduleId and testSetId
            if (!string.IsNullOrWhiteSpace(request.ModuleId) && string.IsNullOrWhiteSpace(request.TestSetId))
                return Results.BadRequest(new { error = "testSetId is required when moduleId is specified" });

            // Single-objective execution requires testSetId and Reuse or Rebaseline mode
            if (!string.IsNullOrWhiteSpace(request.ObjectiveId))
            {
                if (string.IsNullOrWhiteSpace(request.TestSetId))
                    return Results.BadRequest(new { error = "testSetId is required when objectiveId is specified" });
                if (mode is not RunMode.Reuse and not RunMode.Rebaseline and not RunMode.VerifyOnly)
                    return Results.BadRequest(new { error = "objectiveId is only supported in Reuse, Rebaseline, or VerifyOnly mode" });
            }

            // Load the test set once (needed for rebaseline guard + dispatch decision)
            PersistedTestSet? testSet = null;
            if (mode is RunMode.Reuse or RunMode.Rebaseline or RunMode.VerifyOnly
                && !string.IsNullOrWhiteSpace(request.TestSetId))
            {
                testSet = !string.IsNullOrWhiteSpace(request.ModuleId)
                    ? await tsRepo.LoadAsync(request.ModuleId, request.TestSetId!)
                    : await tsRepo.LoadAsync(request.TestSetId!);
            }

            // Recorded objectives cannot be rebaselined
            if (mode == RunMode.Rebaseline && !string.IsNullOrWhiteSpace(request.ObjectiveId))
            {
                if (testSet is null)
                    return Results.NotFound(new { error = $"Test set '{request.TestSetId}' not found" });
                var obj = testSet.TestObjectives.Find(o => o.Id == request.ObjectiveId);
                if (obj is null)
                    return Results.NotFound(new { error = $"Objective '{request.ObjectiveId}' not found in test set" });
                if (obj.Source == "Recorded")
                    return Results.BadRequest(new { error = "Recorded objectives cannot be rebaselined — only AI-generated objectives support rebaseline" });
            }

            // Prevent running the same test set concurrently (different test sets can run in parallel)
            if (request.TestSetId is not null && tracker.HasActiveRunForTestSet(request.TestSetId))
                return Results.Conflict(new { error = $"Test set '{request.TestSetId}' already has an active run" });
            if (request.ModuleId is not null && moduleRunTracker.HasActiveModuleRunForModule(request.ModuleId))
                return Results.Conflict(new { error = $"Module '{request.ModuleId}' already has an active run" });

            var runId = Guid.NewGuid().ToString("N")[..12];
            var objective = mode is RunMode.Reuse or RunMode.VerifyOnly ? "" : request.Objective!;
            var user = ctx.Items["User"] as User;

            // ── Decide: run in-process or enqueue for a local agent ──
            string? agentTarget = testSet is not null && queueRepo is not null
                ? RunDispatchHelper.GetAgentRequiredTarget(testSet, request.ObjectiveId)
                : null;

            if (agentTarget is not null && queueRepo is not null)
            {
                // Enqueue for a local agent to pick up
                var entry = new RunQueueEntry
                {
                    Id = runId,
                    ModuleId = request.ModuleId ?? testSet!.ModuleId ?? "",
                    TestSetId = request.TestSetId!,
                    ObjectiveId = request.ObjectiveId,
                    TargetType = agentTarget,
                    Mode = mode.ToString(),
                    RequestedBy = user?.Id,
                    RequestJson = JsonSerializer.Serialize(request, _jsonOpts),
                    CreatedAt = DateTime.UtcNow
                };
                await queueRepo.EnqueueAsync(entry);
                tracker.Create(runId, objective, request.Mode, request.TestSetId);
                // Mark it Queued explicitly so the dashboard shows the right state
                var status = tracker.Get(runId);
                if (status is not null) status.Status = "Queued";

                return Results.Accepted($"/api/runs/{runId}/status", new
                {
                    runId,
                    status = "Queued",
                    targetType = agentTarget,
                    startedAt = DateTime.UtcNow
                });
            }

            tracker.Create(runId, objective, request.Mode, request.TestSetId);

            // Fire and forget — the orchestrator persists results via ExecutionHistoryRepository
            _ = Task.Run(async () =>
            {
                try
                {
                    var reuseId = mode is RunMode.Reuse or RunMode.VerifyOnly ? request.TestSetId : null;
                    var result = await orchestrator.RunAsync(
                        objective, mode, reuseId,
                        externalRunId: runId,
                        moduleId: request.ModuleId,
                        targetTestSetId: request.TestSetId,
                        objectiveName: request.ObjectiveName,
                        objectiveId: request.ObjectiveId,
                        apiStackKey: request.ApiStackKey,
                        apiModule: request.ApiModule,
                        verificationWaitOverride: request.VerificationWaitOverride,
                        environmentKey: request.EnvironmentKey);
                    var testSetId = mode is RunMode.Reuse or RunMode.VerifyOnly
                        ? request.TestSetId!
                        : !string.IsNullOrWhiteSpace(request.TestSetId)
                            ? request.TestSetId!
                            : SlugHelper.ToSlug(result.Objective);
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

        group.MapGet("/{runId}/status", async (string runId, IRunTracker tracker, IRunQueueRepository? queueRepo) =>
        {
            // Queue takes precedence — it's the source of truth for agent-dispatched runs
            if (queueRepo is not null)
            {
                var job = await queueRepo.GetByIdAsync(runId);
                if (job is not null)
                {
                    return Results.Ok(new RunStatus
                    {
                        RunId = job.Id,
                        Objective = "",
                        Mode = job.Mode,
                        TestSetId = job.TestSetId,
                        Status = job.Status,
                        StartedAt = job.CreatedAt,
                        CompletedAt = job.CompletedAt,
                        Error = job.Error
                    });
                }
            }

            var status = tracker.Get(runId);
            if (status is null) return Results.NotFound(new { error = $"Run '{runId}' not found" });
            return Results.Ok(status);
        });

        // GET /api/runs/active — check for any active run (module-level or individual)
        group.MapGet("/active", (IRunTracker tracker, IModuleRunTracker moduleRunTracker) =>
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

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

public record RunRequest(string? Objective, string? ObjectiveName, string Mode, string? TestSetId, string? ModuleId, string? ObjectiveId, string? ApiStackKey = null, string? ApiModule = null, int? VerificationWaitOverride = null, string? EnvironmentKey = null);
