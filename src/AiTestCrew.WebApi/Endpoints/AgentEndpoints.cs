using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/agents/register
        // REQ-012: if IsShared=true, caller must be Admin.
        group.MapPost("/register", async (RegisterAgentRequest request, IAgentRepository repo,
            IRunQueueRepository queueRepo, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (request.Capabilities is null || request.Capabilities.Length == 0)
                return Results.BadRequest(new { error = "capabilities is required" });

            var me = ctx.Items["User"] as User;
            var wantsShared = request.IsShared == true;

            if (wantsShared && me is not null && me.Role != "Admin")
                return Results.Problem(
                    title: "Forbidden",
                    detail: "Only admin users can register a shared agent",
                    statusCode: 403);

            var agent = new Agent
            {
                Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N")[..12] : request.Id!,
                Name = request.Name,
                UserId = me?.Id,
                Capabilities = request.Capabilities.ToList(),
                Version = request.Version,
                Status = "Online",
                LastSeenAt = DateTime.UtcNow,
                RegisteredAt = DateTime.UtcNow,
                Role = string.IsNullOrWhiteSpace(request.Role) ? "Both" : request.Role,
                Tags = request.Tags is null ? new() : request.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                IsShared = wantsShared,
            };
            await repo.UpsertAsync(agent);
            return Results.Ok(new { agentId = agent.Id });
        });

        // POST /api/agents/{id}/heartbeat
        group.MapPost("/{id}/heartbeat", async (string id, HeartbeatRequest request,
            IAgentRepository repo, IRunQueueRepository queueRepo,
            IAgentAuthStateRepository? authStateRepo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"Agent '{id}' not registered" });

            if (existing.ForceQuitRequested)
            {
                return Results.Ok(new
                {
                    status = "Offline",
                    activeJobId = (string?)null,
                    activeJobStatus = (string?)null,
                    shouldExit = true
                });
            }

            var status = string.IsNullOrWhiteSpace(request.Status) ? "Online" : request.Status;
            await repo.HeartbeatAsync(id, status);

            if (authStateRepo is not null && request.AuthStateFiles is not null)
            {
                var entries = request.AuthStateFiles
                    .Where(f => !string.IsNullOrEmpty(f.EnvKey) && !string.IsNullOrEmpty(f.Surface))
                    .Select(f => new AgentAuthState
                    {
                        AgentId = id,
                        EnvironmentKey = f.EnvKey,
                        Surface = Enum.TryParse<AuthSurface>(f.Surface, out var s) ? s : AuthSurface.WebBlazor,
                        FileExists = f.FileExists,
                        FileMtimeUtc = f.FileMtimeUtc,
                    })
                    .ToList();
                await authStateRepo.ReplaceForAgentAsync(id, entries);
            }

            var activeJob = await queueRepo.GetActiveForAgentAsync(id);
            return Results.Ok(new
            {
                status,
                activeJobId = activeJob?.Id,
                activeJobStatus = activeJob?.Status,
                shouldExit = false
            });
        });

        // POST /api/agents/{id}/force-quit
        group.MapPost("/{id}/force-quit", async (string id, IAgentRepository repo, IRunQueueRepository queueRepo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"Agent '{id}' not registered" });

            await repo.SetForceQuitAsync(id, true);

            var activeJob = await queueRepo.GetActiveForAgentAsync(id);
            if (activeJob is not null)
            {
                await queueRepo.MarkCompletedAsync(activeJob.Id, success: false,
                    error: "Agent force-quit from dashboard.");
            }
            return Results.Ok(new { forceQuitRequested = true, cancelledJobId = activeJob?.Id });
        });

        // DELETE /api/agents/{id}
        group.MapDelete("/{id}", async (string id, IAgentRepository repo,
            IAgentAuthStateRepository? authStateRepo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"Agent '{id}' not registered" });
            await repo.DeleteAsync(id);
            if (authStateRepo is not null) await authStateRepo.DeleteForAgentAsync(id);
            return Results.NoContent();
        });

        // GET /api/agents
        group.MapGet("/", async (IAgentRepository repo, IRunQueueRepository queueRepo, IUserRepository? userRepo) =>
        {
            var agents = await repo.ListAllAsync();
            var users = userRepo is not null ? (await userRepo.ListAllAsync()).ToDictionary(u => u.Id) : new();
            var result = new List<object>();
            foreach (var a in agents)
            {
                var currentJob = await queueRepo.GetActiveForAgentAsync(a.Id);
                string? ownerName = null;
                if (a.UserId is not null && users.TryGetValue(a.UserId, out var u))
                    ownerName = u.Name;

                result.Add(new
                {
                    a.Id, a.Name,
                    a.UserId,
                    ownerName,
                    a.Capabilities, a.Version, a.Status,
                    a.Role, a.Tags,
                    a.IsShared,
                    a.LastSeenAt, a.RegisteredAt,
                    currentJob = currentJob is null ? null : new
                    {
                        currentJob.Id, currentJob.TestSetId, currentJob.ObjectiveId,
                        currentJob.TargetType, currentJob.Status
                    }
                });
            }
            return Results.Ok(result);
        });

        // PUT /api/agents/{id}/shared -- Admin-only
        group.MapPut("/{id}/shared", async (string id, SetSharedRequest request,
            IAgentRepository repo, HttpContext ctx) =>
        {
            var me = ctx.Items["User"] as User;
            if (me is not null && me.Role != "Admin")
                return Results.Problem(title: "Forbidden", detail: "Only admins can change the shared flag", statusCode: 403);

            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"Agent '{id}' not found" });

            await repo.SetSharedAsync(id, request.IsShared);
            return Results.Ok(new { id, isShared = request.IsShared });
        });

        return group;
    }
}

public record RegisterAgentRequest(string? Id, string Name, string[] Capabilities, string? Version, string? Role = null, string[]? Tags = null, bool? IsShared = null);

public record HeartbeatRequest(string? Status, AuthStateFileReport[]? AuthStateFiles = null);

public record AuthStateFileReport(string EnvKey, string Surface, bool FileExists, DateTime? FileMtimeUtc);

public record SetSharedRequest(bool IsShared);
