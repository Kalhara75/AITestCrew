using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

/// <summary>
/// HTTP surface for the run_auth_refreshes table. Mirrors the
/// pending-verification endpoints -- agents post a request when they catch
/// AuthRequiredException, the dashboard polls active refreshes and
/// dispatches the AuthSetup job on the user's click, and the agent reports
/// the outcome. Dedup-by-scope is handled inside the repository.
/// </summary>
public static class AuthRefreshEndpoints
{
    public static RouteGroupBuilder MapAuthRefreshEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/auth-refreshes -- insert (or join an existing active row at the same scope).
        group.MapPost("/", async (AuthRefreshRequest body, IAuthRefreshRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(body.EnvironmentKey))
                body.EnvironmentKey = "default";
            var row = await repo.InsertOrJoinAsync(body);
            return Results.Ok(row);
        });

        // GET /api/auth-refreshes/active -- dashboard banner data.
        group.MapGet("/active", async (IAuthRefreshRepository repo) =>
        {
            var rows = await repo.ListActiveAsync();
            return Results.Ok(rows);
        });

        // GET /api/auth-refreshes/{id}
        group.MapGet("/{id}", async (string id, IAuthRefreshRepository repo) =>
        {
            var r = await repo.GetByIdAsync(id);
            return r is null
                ? Results.NotFound(new { error = $"auth-refresh '{id}' not found" })
                : Results.Ok(r);
        });

        // POST /api/auth-refreshes/{id}/start -- UI button click.
        // Transitions the request to InProgress and enqueues an AuthSetup job
        // targeted at the right capability so an agent runs --auth-setup
        // (browser flow) on the right machine.
        // Defence-in-depth: verify the caller owns or is authorised to reach the
        // target agent before enqueueing (REQ-012). The panel filter is cosmetic --
        // a crafted request can bypass it without this server-side check.
        group.MapPost("/{id}/start", async (
            string id,
            IAuthRefreshRepository repo,
            IRunQueueRepository queueRepo,
            IAgentRepository agentRepo,
            HttpContext ctx) =>
        {
            var r = await repo.GetByIdAsync(id);
            if (r is null)
                return Results.NotFound(new { error = $"auth-refresh '{id}' not found" });
            if (r.Status is "Completed" or "Failed" or "Cancelled")
                return Results.BadRequest(new { error = $"refresh already terminal: {r.Status}" });

            // Authorisation: the caller must be able to see the target agent.
            var me = ctx.Items["User"] as User;
            if (me is not null && r.AgentId is not null)
            {
                var targetAgent = await agentRepo.GetByIdAsync(r.AgentId);
                if (targetAgent is not null && !AuthHealthEndpoints.IsVisibleToUser(targetAgent, me))
                    return Results.Forbid();
            }

            await repo.MarkInProgressAsync(id);

            // Map surface -> capability the agent advertises. The Runner agent
            // mode runs --auth-setup locally for any UI surface.
            var targetType = r.Surface switch
            {
                AuthSurface.WebBlazor => "UI_Web_Blazor",
                AuthSurface.WebMvc => "UI_Web_MVC",
                _ => "UI_Web_Blazor", // API surface is recovered server-side; UI fallback for safety
            };

            // For API surface, we don't need to dispatch to an agent -- the WebApi
            // can re-acquire from creds in-process. The simplest implementation
            // is still to mark Completed immediately; the next agent attempt
            // will pick up the fresh token via LoginTokenProvider auto-refresh.
            if (r.Surface == AuthSurface.Api)
            {
                await repo.MarkCompletedAsync(id);
                return Results.Ok(new { id, status = "Completed", note = "API token will be re-acquired on next request" });
            }

            // UI surface: enqueue AuthSetup job for an agent on the right machine.
            // RequestJson field names must match AuthSetupRequest (Target, EnvironmentKey).
            var entry = new RunQueueEntry
            {
                ModuleId = "auth-refresh",
                TestSetId = id,
                TargetType = targetType,
                JobKind = "AuthSetup",
                Mode = "",
                Status = "Queued",
                RequestJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    kind = "AuthSetup",
                    authRefreshId = id,
                    target = targetType,
                    environmentKey = r.EnvironmentKey,
                }),
                CreatedAt = DateTime.UtcNow,
            };
            await queueRepo.EnqueueAsync(entry);
            return Results.Ok(new { id, status = "InProgress", queueEntryId = entry.Id });
        });

        // POST /api/auth-refreshes/{id}/complete -- agent reports success.
        group.MapPost("/{id}/complete", async (string id, IAuthRefreshRepository repo) =>
        {
            await repo.MarkCompletedAsync(id);
            return Results.NoContent();
        });

        // POST /api/auth-refreshes/{id}/fail -- agent reports failure.
        group.MapPost("/{id}/fail", async (string id, AuthRefreshFailBody body, IAuthRefreshRepository repo) =>
        {
            await repo.MarkFailedAsync(id, body.Error ?? "Auth refresh failed");
            return Results.NoContent();
        });

        // POST /api/auth-refreshes/{id}/cancel
        group.MapPost("/{id}/cancel", async (string id, IAuthRefreshRepository repo) =>
        {
            var ok = await repo.CancelAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        return group;
    }
}

public record AuthRefreshFailBody(string? Error);
