namespace AiTestCrew.Core.Models;

/// <summary>
/// A registered Runner instance that can claim and execute queued jobs.
/// Agents are long-lived — the Runner registers on startup and sends periodic heartbeats.
/// </summary>
public class Agent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>User who owns the API key used to register this agent.</summary>
    public string? UserId { get; set; }

    /// <summary>Target types this agent can execute — e.g. ["UI_Web_Blazor", "UI_Desktop_WinForms"].</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>Runner version reported on registration (diagnostics only).</summary>
    public string? Version { get; set; }

    /// <summary>Online | Offline | Busy.</summary>
    public string Status { get; set; } = "Offline";

    public DateTime LastSeenAt { get; set; }
    public DateTime RegisteredAt { get; set; }

    /// <summary>Run queue entry currently claimed by this agent, if any. Populated on read only.</summary>
    public string? CurrentJobId { get; set; }

    /// <summary>
    /// When true, the agent should terminate itself on the next heartbeat response.
    /// Set by the <c>POST /api/agents/{id}/force-quit</c> endpoint so a stuck recording
    /// can be killed remotely from the dashboard. Cleared on the next successful registration.
    /// </summary>
    public bool ForceQuitRequested { get; set; }
}
