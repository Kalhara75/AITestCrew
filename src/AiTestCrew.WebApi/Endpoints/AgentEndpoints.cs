using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/agents/register — register a new agent or re-register an existing one
        group.MapPost("/register", async (RegisterAgentRequest request, IAgentRepository repo,
            IRunQueueRepository queueRepo, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (request.Capabilities is null || request.Capabilities.Length == 0)
                return Results.BadRequest(new { error = "capabilities is required" });

            var agent = new Agent
            {
                Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N")[..12] : request.Id!,
                Name = request.Name,
                UserId = (ctx.Items["User"] as User)?.Id,
                Capabilities = request.Capabilities.ToList(),
                Version = request.Version,
                Status = "Online",
                LastSeenAt = DateTime.UtcNow,
                RegisteredAt = DateTime.UtcNow
            };
            await repo.UpsertAsync(agent);
            return Results.Ok(new { agentId = agent.Id });
        });

        // POST /api/agents/{id}/heartbeat — keep-alive
        group.MapPost("/{id}/heartbeat", async (string id, HeartbeatRequest request,
            IAgentRepository repo, IRunQueueRepository queueRepo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"Agent '{id}' not registered" });

            var status = string.IsNullOrWhiteSpace(request.Status) ? "Online" : request.Status;
            await repo.HeartbeatAsync(id, status);

            // Include any job the server thinks this agent is actively running
            var activeJob = await queueRepo.GetActiveForAgentAsync(id);
            return Results.Ok(new
            {
                status,
                activeJobId = activeJob?.Id,
                activeJobStatus = activeJob?.Status
            });
        });

        // DELETE /api/agents/{id} — graceful deregister (Ctrl+C on Runner)
        group.MapDelete("/{id}", async (string id, IAgentRepository repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"Agent '{id}' not registered" });
            await repo.DeleteAsync(id);
            return Results.NoContent();
        });

        // GET /api/agents — list all agents (for dashboard)
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

        return group;
    }
}

public record RegisterAgentRequest(string? Id, string Name, string[] Capabilities, string? Version);
public record HeartbeatRequest(string? Status);
