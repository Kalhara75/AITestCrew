using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

/// <summary>
/// Pre-flight health for cached UI storage-state files. Returns one tile
/// per environment, each containing the surfaces (Blazor / MVC) that need
/// attention. Matches the user's mental model of "is auth healthy for THIS
/// environment?" with a separate refresh affordance per surface.
///
/// Envs hidden via <see cref="EnvironmentConfig.AuthHealthEnabled"/> = false
/// are filtered out -- even if old agent_auth_state rows still exist
/// for them -- so the panel never offers refresh for an env the user has
/// opted out of.
///
/// Per-surface entries that are Fresh OR already covered by an active row
/// in run_auth_refreshes are also dropped from the env's surface
/// list (the reactive AuthRefreshBanner shows in-flight ones). A
/// tile is suppressed entirely when its surface list ends up empty.
///
/// Scoping (REQ-012): only agents visible to the current user are considered.
///   User       -- sees own agents only (agent.user_id == me.id).
///   AuthSteward -- sees own agents plus shared agents (is_shared = true).
///   Admin      -- sees all agents (legacy behaviour).
/// The panel renders "All your agents' auth states are fresh." when the
/// filtered set has nothing actionable -- instead of an empty panel or
/// confusing tiles for machines the user cannot reach.
/// </summary>
public static class AuthHealthEndpoints
{
    public static RouteGroupBuilder MapAuthHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            IAgentAuthStateRepository authStateRepo,
            IAuthRefreshRepository refreshRepo,
            IAgentRepository agentRepo,
            IEnvironmentResolver envResolver,
            TestEnvironmentConfig config,
            HttpContext ctx) =>
        {
            var me = ctx.Items["User"] as User;

            var allAgents = (await agentRepo.ListAllAsync()).ToDictionary(a => a.Id);

            // Scope: which agent ids are visible to the current user?
            var visibleAgentIds = allAgents.Values
                .Where(a => IsVisibleToUser(a, me))
                .Select(a => a.Id)
                .ToHashSet();

            var states = (await authStateRepo.ListForOnlineAgentsAsync())
                .Where(s => visibleAgentIds.Contains(s.AgentId))
                .ToList();

            var activeRefreshes = await refreshRepo.ListActiveAsync();

            var blazorTtl = Math.Max(1, config.BraveCloudUiStorageStateMaxAgeHours);
            var mvcTtl = Math.Max(1, config.LegacyWebUiStorageStateMaxAgeHours);
            var warnHours = Math.Max(0, config.Auth.ExpiryWarningHours);
            var now = DateTime.UtcNow;

            var activeScopes = activeRefreshes
                .Select(r => (r.EnvironmentKey, r.Surface))
                .ToHashSet();

            // Step 1: per (env, surface) status from agent reports.
            var perEnvSurface = states
                .Where(s => envResolver.ResolveAuthHealthEnabled(s.EnvironmentKey))
                .GroupBy(s => (s.EnvironmentKey, s.Surface))
                .Select(g =>
                {
                    var ttl = g.Key.Surface == AuthSurface.WebBlazor ? blazorTtl : mvcTtl;
                    var perAgent = g.Select(s => new
                    {
                        agentId = s.AgentId,
                        agentName = allAgents.TryGetValue(s.AgentId, out var a) ? a.Name : s.AgentId,
                        s.FileExists,
                        ageHours = s.FileMtimeUtc is null ? (double?)null
                            : Math.Round((now - s.FileMtimeUtc.Value).TotalHours, 2),
                    }).ToList();

                    string status;
                    if (perAgent.All(p => !p.FileExists)) status = "Missing";
                    else if (perAgent.Any(p => p.FileExists && p.ageHours >= ttl)) status = "Stale";
                    else if (perAgent.Any(p => p.FileExists && p.ageHours >= ttl - warnHours)) status = "ExpiringSoon";
                    else status = "Fresh";

                    var maxAge = perAgent
                        .Where(p => p.ageHours.HasValue)
                        .Select(p => p.ageHours!.Value)
                        .DefaultIfEmpty(0)
                        .Max();

                    return new
                    {
                        envKey = g.Key.EnvironmentKey,
                        surface = g.Key.Surface,
                        status,
                        ageHours = maxAge,
                        ttlHours = ttl,
                        hasActiveRefresh = activeScopes.Contains((g.Key.EnvironmentKey, g.Key.Surface)),
                        agentReports = perAgent,
                    };
                })
                .ToList();

            // Step 2: group by env, keep only surfaces needing attention.
            var tiles = perEnvSurface
                .GroupBy(e => e.envKey)
                .Select(g =>
                {
                    var actionableSurfaces = g
                        .Where(e => e.status != "Fresh" && !e.hasActiveRefresh)
                        .OrderBy(e => e.surface.ToString())
                        .Select(e => new
                        {
                            surface = e.surface.ToString(),
                            e.status,
                            e.ageHours,
                            e.ttlHours,
                            e.agentReports,
                        })
                        .ToList();

                    return new
                    {
                        envKey = g.Key,
                        envDisplayName = envResolver.ResolveDisplayName(g.Key),
                        surfaces = actionableSurfaces,
                    };
                })
                .Where(t => t.surfaces.Count > 0)
                .OrderBy(t => t.envKey)
                .ToList();

            return Results.Ok(tiles);
        });

        return group;
    }

    /// <summary>
    /// Returns true when the agent is visible to the given user based on role.
    /// Null user (file-based storage mode with no auth) sees everything.
    /// </summary>
    public static bool IsVisibleToUser(Agent agent, User? me)
    {
        if (me is null) return true; // no auth mode -- show everything
        return me.Role switch
        {
            "Admin" => true,
            "AuthSteward" => agent.UserId == me.Id || agent.IsShared,
            _ => agent.UserId == me.Id,
        };
    }
}
