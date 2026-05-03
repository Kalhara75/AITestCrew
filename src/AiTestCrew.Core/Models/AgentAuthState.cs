namespace AiTestCrew.Core.Models;

/// <summary>
/// One row in <c>agent_auth_state</c> — represents the freshness of a single
/// cached storage-state file owned by an agent for a given (env, surface)
/// combination. Reported by agents on their heartbeat (cheap file stat) and
/// aggregated server-side by <c>/api/auth-health</c> so the dashboard can warn
/// the user before they kick off a run that's destined to hit a login redirect.
/// </summary>
public class AgentAuthState
{
    public string AgentId { get; set; } = "";
    public string EnvironmentKey { get; set; } = "";

    /// <summary>WebBlazor or WebMvc. API surface is in-memory; no file to track.</summary>
    public AuthSurface Surface { get; set; }

    /// <summary>False when the agent has never run --auth-setup for this scope.</summary>
    public bool FileExists { get; set; }

    /// <summary>Last write time of the cached storage-state file (UTC). Null when <see cref="FileExists"/> is false.</summary>
    public DateTime? FileMtimeUtc { get; set; }

    /// <summary>When the agent last reported this row (UTC). Used to ignore offline agents in the aggregate.</summary>
    public DateTime ReportedAtUtc { get; set; }
}
