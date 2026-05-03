using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Stores the latest reported freshness of each (agent, env, surface) cached
/// storage-state file. Written by the heartbeat handler; read by the
/// pre-flight auth-health endpoint.
/// </summary>
public interface IAgentAuthStateRepository
{
    /// <summary>
    /// Replaces all rows reported by <paramref name="agentId"/> with
    /// <paramref name="entries"/> in a single transaction. Authoritative — any
    /// (env, surface) row not present in the new list is removed for this
    /// agent (e.g. the user deleted a storage-state file).
    /// </summary>
    Task ReplaceForAgentAsync(string agentId, IReadOnlyList<AgentAuthState> entries);

    /// <summary>
    /// Returns one row per (agent_id, env_key, surface) for non-Offline agents.
    /// The endpoint aggregates these into per-(env, surface) tiles.
    /// </summary>
    Task<List<AgentAuthState>> ListForOnlineAgentsAsync();

    /// <summary>Removes all rows for an agent — used when the agent deregisters.</summary>
    Task DeleteForAgentAsync(string agentId);
}
