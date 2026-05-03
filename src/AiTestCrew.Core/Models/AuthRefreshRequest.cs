namespace AiTestCrew.Core.Models;

/// <summary>
/// One outstanding request to refresh authentication context, scoped by
/// <c>(EnvironmentKey, Surface, ApiStackKey, AgentId)</c>. Created when an agent
/// catches <see cref="AiTestCrew.Core.Exceptions.AuthRequiredException"/> and
/// the silent retry-once didn't recover. Multiple paused runs share one row
/// (dedup via the unique partial index on the table) so a single re-auth
/// resumes every dependent run.
/// </summary>
public class AuthRefreshRequest
{
    public string Id { get; set; } = "";

    /// <summary>Customer environment whose creds / storage state need refreshing.</summary>
    public string EnvironmentKey { get; set; } = "default";

    /// <summary>Auth surface — Api / WebBlazor / WebMvc.</summary>
    public AuthSurface Surface { get; set; }

    /// <summary>API stack key (only for <see cref="AuthSurface.Api"/>); null otherwise.</summary>
    public string? ApiStackKey { get; set; }

    /// <summary>
    /// Id of the agent that owns the storage state file / token cache. NULL for
    /// API surface — any agent can refresh by re-acquiring from creds. For UI
    /// surfaces this targets a specific machine, since storage state is local.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>The run that first detected the failure (for telemetry).</summary>
    public string? RequestedByRunId { get; set; }

    /// <summary>Pending | InProgress | Completed | Failed | Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    public int AutoAttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
