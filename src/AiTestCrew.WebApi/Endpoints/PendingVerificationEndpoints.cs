using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

/// <summary>
/// HTTP surface for the <c>run_pending_verifications</c> table. Exposed so remote
/// agents (Runner in <c>--agent</c> mode against a Dockerised WebApi) can insert,
/// update, and read pending-verification rows without touching the server DB
/// directly. All operations are thin wrappers over
/// <see cref="IPendingVerificationRepository"/>.
/// </summary>
public static class PendingVerificationEndpoints
{
    public static RouteGroupBuilder MapPendingVerificationEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/pending-verifications — insert a new pending row.
        group.MapPost("/", async (PendingVerification body, IPendingVerificationRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(body.PendingId) || string.IsNullOrWhiteSpace(body.ParentRunId))
                return Results.BadRequest(new { error = "pendingId and parentRunId are required" });
            await repo.InsertAsync(body);
            return Results.Created($"/api/pending-verifications/{body.PendingId}", new { id = body.PendingId });
        });

        // GET /api/pending-verifications/{pendingId}
        group.MapGet("/{pendingId}", async (string pendingId, IPendingVerificationRepository repo) =>
        {
            var p = await repo.GetByIdAsync(pendingId);
            return p is null
                ? Results.NotFound(new { error = $"pending '{pendingId}' not found" })
                : Results.Ok(p);
        });

        // POST /api/pending-verifications/{pendingId}/attempt — record a re-enqueued retry.
        group.MapPost("/{pendingId}/attempt", async (
            string pendingId, AttemptUpdate body, IPendingVerificationRepository repo) =>
        {
            await repo.UpdateAttemptAsync(pendingId, body.NewQueueEntryId, body.AttemptCount, body.AttemptLogJson ?? "[]");
            return Results.NoContent();
        });

        // POST /api/pending-verifications/{pendingId}/complete — terminal success.
        group.MapPost("/{pendingId}/complete", async (
            string pendingId, TerminalUpdate body, IPendingVerificationRepository repo) =>
        {
            await repo.MarkCompletedAsync(pendingId, body.ResultJson ?? "{}", body.AttemptLogJson ?? "[]");
            return Results.NoContent();
        });

        // POST /api/pending-verifications/{pendingId}/fail — terminal deadline-fail.
        group.MapPost("/{pendingId}/fail", async (
            string pendingId, TerminalUpdate body, IPendingVerificationRepository repo) =>
        {
            await repo.MarkFailedAsync(pendingId, body.ResultJson ?? "{}", body.AttemptLogJson ?? "[]");
            return Results.NoContent();
        });

        // GET /api/pending-verifications/by-run/{runId}
        group.MapGet("/by-run/{runId}", async (string runId, IPendingVerificationRepository repo) =>
        {
            var rows = await repo.ListForRunAsync(runId);
            return Results.Ok(rows);
        });

        // GET /api/pending-verifications/count?runId=...
        group.MapGet("/count", async (string runId, IPendingVerificationRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(runId))
                return Results.BadRequest(new { error = "runId is required" });
            var n = await repo.CountPendingForRunAsync(runId);
            return Results.Ok(new { runId, count = n });
        });

        return group;
    }
}

public record AttemptUpdate(string NewQueueEntryId, int AttemptCount, string? AttemptLogJson);
public record TerminalUpdate(string? ResultJson, string? AttemptLogJson);
