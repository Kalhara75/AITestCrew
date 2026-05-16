using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Abstraction for agent storage (registered Runner instances).
/// </summary>
public interface IAgentRepository
{
    /// <summary>
    /// Registers a new agent or re-registers an existing one by id (agent-generated GUID).
    /// Updates name, capabilities, version, status=Online, last_seen_at=now.
    /// </summary>
    Task<Agent> UpsertAsync(Agent agent);

    Task<Agent?> GetByIdAsync(string id);
    Task<List<Agent>> ListAllAsync();

    /// <summary>Updates last_seen_at and status on each heartbeat.</summary>
    Task HeartbeatAsync(string id, string status);

    /// <summary>Graceful deregister -- removes the agent entirely.</summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// Marks agents Offline when their last heartbeat is older than timeout.
    /// Called periodically by AgentHeartbeatMonitor.
    /// </summary>
    Task<int> MarkStaleOfflineAsync(TimeSpan timeout);

    /// <summary>
    /// Sets or clears the force-quit flag. When set, the agent"s next heartbeat response
    /// carries shouldExit = true and the Runner terminates via Environment.Exit.
    /// </summary>
    Task SetForceQuitAsync(string id, bool requested);

    /// <summary>
    /// Sets or clears the is_shared flag on an existing agent.
    /// Admin-only -- enforced at the endpoint layer, not here.
    /// </summary>
    Task SetSharedAsync(string id, bool isShared);
}
