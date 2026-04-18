using AiTestCrew.Core.Interfaces;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class QueueEndpoints
{
    public static RouteGroupBuilder MapQueueEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/queue/next?agentId=...&capabilities=UI_Web_Blazor,UI_Web_MVC
        group.MapGet("/next", async (string agentId, string capabilities,
            IRunQueueRepository queueRepo, IAgentRepository agentRepo) =>
        {
            if (string.IsNullOrWhiteSpace(agentId))
                return Results.BadRequest(new { error = "agentId is required" });
            if (string.IsNullOrWhiteSpace(capabilities))
                return Results.BadRequest(new { error = "capabilities is required" });

            var agent = await agentRepo.GetByIdAsync(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent '{agentId}' not registered" });

            var caps = capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var job = await queueRepo.ClaimNextAsync(agentId, caps);
            if (job is null) return Results.NoContent();

            return Results.Ok(new
            {
                jobId = job.Id,
                job.ModuleId, job.TestSetId, job.ObjectiveId, job.TargetType, job.Mode,
                requestJson = job.RequestJson
            });
        });

        // POST /api/queue/{jobId}/progress — flip Claimed → Running
        group.MapPost("/{jobId}/progress", async (string jobId, IRunQueueRepository queueRepo,
            IRunTracker runTracker) =>
        {
            var job = await queueRepo.GetByIdAsync(jobId);
            if (job is null) return Results.NotFound(new { error = $"Job '{jobId}' not found" });
            await queueRepo.MarkRunningAsync(jobId);
            // Mirror into RunTracker so dashboard polling sees "Running" status
            if (runTracker.Get(jobId) is null)
                runTracker.Create(jobId, "", job.Mode, job.TestSetId);
            return Results.Ok(new { status = "Running" });
        });

        // POST /api/queue/{jobId}/result — terminal state
        group.MapPost("/{jobId}/result", async (string jobId, JobResultRequest request,
            IRunQueueRepository queueRepo, IRunTracker runTracker) =>
        {
            var job = await queueRepo.GetByIdAsync(jobId);
            if (job is null) return Results.NotFound(new { error = $"Job '{jobId}' not found" });

            await queueRepo.MarkCompletedAsync(jobId, request.Success, request.Error);
            // Mirror into RunTracker so the dashboard sees the terminal status
            if (request.Success)
                runTracker.Complete(jobId, job.TestSetId);
            else
                runTracker.Fail(jobId, request.Error ?? "Agent reported failure");
            return Results.Ok(new { status = request.Success ? "Completed" : "Failed" });
        });

        // GET /api/queue — list recent queue entries
        group.MapGet("/", async (IRunQueueRepository queueRepo) =>
        {
            var entries = await queueRepo.ListRecentAsync();
            return Results.Ok(entries.Select(e => new
            {
                e.Id, e.ModuleId, e.TestSetId, e.ObjectiveId, e.TargetType, e.Mode,
                e.Status, e.ClaimedBy, e.RequestedBy, e.ClaimedAt, e.CompletedAt,
                e.CreatedAt, e.Error
            }));
        });

        // DELETE /api/queue/{jobId} — cancel a Queued job
        group.MapDelete("/{jobId}", async (string jobId, IRunQueueRepository queueRepo,
            IRunTracker runTracker) =>
        {
            var job = await queueRepo.GetByIdAsync(jobId);
            if (job is null) return Results.NotFound(new { error = $"Job '{jobId}' not found" });
            if (job.Status != "Queued")
                return Results.Conflict(new { error = $"Job is {job.Status} — only Queued jobs can be cancelled" });

            var cancelled = await queueRepo.CancelAsync(jobId);
            if (!cancelled) return Results.Conflict(new { error = "Job no longer Queued" });
            runTracker.Fail(jobId, "Cancelled by user");
            return Results.NoContent();
        });

        return group;
    }
}

public record JobResultRequest(bool Success, string? Error);
