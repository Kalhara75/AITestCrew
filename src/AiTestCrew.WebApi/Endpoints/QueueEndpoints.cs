using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class QueueEndpoints
{
    public static RouteGroupBuilder MapQueueEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/queue — enqueue a new entry (used by remote PC agents that
        // can't touch the server's DB directly; they POST the entry over HTTP and
        // the server persists it. This is how an agent schedules a deferred
        // post-delivery verification into the same queue the dashboard feeds.)
        group.MapPost("/", async (EnqueueRequest req, IRunQueueRepository queueRepo) =>
        {
            if (string.IsNullOrWhiteSpace(req.TargetType))
                return Results.BadRequest(new { error = "targetType is required" });
            if (string.IsNullOrWhiteSpace(req.Mode))
                return Results.BadRequest(new { error = "mode is required" });

            var entry = new RunQueueEntry
            {
                Id = string.IsNullOrEmpty(req.Id) ? Guid.NewGuid().ToString("N")[..12] : req.Id,
                ModuleId = req.ModuleId ?? "",
                TestSetId = req.TestSetId ?? "",
                ObjectiveId = req.ObjectiveId,
                TargetType = req.TargetType,
                Mode = req.Mode,
                JobKind = string.IsNullOrWhiteSpace(req.JobKind) ? "Run" : req.JobKind,
                RequestedBy = req.RequestedBy,
                RequestJson = req.RequestJson ?? "{}",
                NotBeforeAt = req.NotBeforeAt,
                DeadlineAt = req.DeadlineAt,
                AttemptCount = req.AttemptCount ?? 0,
                ParentQueueEntryId = req.ParentQueueEntryId,
                ParentRunId = req.ParentRunId,
            };
            await queueRepo.EnqueueAsync(entry);
            return Results.Created($"/api/queue/{entry.Id}", new { id = entry.Id });
        });

        // GET /api/queue/{jobId} — fetch a single entry (for remote agents wanting
        // to read their own claimed/queued entry state).
        group.MapGet("/{jobId}", async (string jobId, IRunQueueRepository queueRepo) =>
        {
            var job = await queueRepo.GetByIdAsync(jobId);
            if (job is null) return Results.NotFound(new { error = $"Job '{jobId}' not found" });
            return Results.Ok(Serialize(job));
        });

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
                job.ModuleId, job.TestSetId, job.ObjectiveId, job.TargetType, job.Mode, job.JobKind,
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
            IRunQueueRepository queueRepo, IRunTracker runTracker,
            IPendingVerificationRepository? pendingRepo) =>
        {
            var job = await queueRepo.GetByIdAsync(jobId);
            if (job is null) return Results.NotFound(new { error = $"Job '{jobId}' not found" });

            await queueRepo.MarkCompletedAsync(jobId, request.Success, request.Error);

            // When the agent successfully finishes a run but that run enqueued a
            // deferred verification, we must NOT move the RunTracker to 'Completed'
            // — the dashboard needs to see 'AwaitingVerification' until the deferred
            // row finalises. The pending repo is the source of truth.
            var hasPending = request.Success
                && pendingRepo is not null
                && await pendingRepo.CountPendingForRunAsync(jobId) > 0;

            if (request.Success && hasPending)
                runTracker.MarkAwaitingVerification(jobId, job.TestSetId);
            else if (request.Success)
                runTracker.Complete(jobId, job.TestSetId);
            else
                runTracker.Fail(jobId, request.Error ?? "Agent reported failure");

            return Results.Ok(new {
                status = hasPending ? "AwaitingVerification"
                       : request.Success ? "Completed" : "Failed",
            });
        });

        // GET /api/queue — list recent queue entries (includes v6 deferred fields)
        group.MapGet("/", async (IRunQueueRepository queueRepo) =>
        {
            var entries = await queueRepo.ListRecentAsync();
            return Results.Ok(entries.Select(Serialize));
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

    // Centralised projection so list + by-id responses stay in sync and include
    // every field the PC agent + UI need (notably v6 deferred-verification columns).
    private static object Serialize(RunQueueEntry e) => new
    {
        e.Id, e.ModuleId, e.TestSetId, e.ObjectiveId, e.TargetType, e.Mode, e.JobKind,
        e.Status, e.ClaimedBy, e.RequestedBy, e.ClaimedAt, e.CompletedAt,
        e.CreatedAt, e.Error, e.RequestJson,
        e.NotBeforeAt, e.DeadlineAt, e.AttemptCount, e.ParentQueueEntryId, e.ParentRunId
    };
}

public record JobResultRequest(bool Success, string? Error);

public record EnqueueRequest(
    string? Id,
    string? ModuleId,
    string? TestSetId,
    string? ObjectiveId,
    string TargetType,
    string Mode,
    string? JobKind,
    string? RequestedBy,
    string? RequestJson,
    DateTime? NotBeforeAt,
    DateTime? DeadlineAt,
    int? AttemptCount,
    string? ParentQueueEntryId,
    string? ParentRunId);
