namespace AiTestCrew.Core.Models;

/// <summary>
/// Represents a platform user identified by an API key.
/// </summary>
public class User
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Fixed-enum role governing what the user can see and action.
    /// Values: "User" (default) | "AuthSteward" | "Admin".
    /// User        - see and manage own agents only; own auth states only.
    /// AuthSteward - User rights plus shared agents in auth-health panel.
    /// Admin       - full access; mark agents shared; promote/demote user roles.
    /// </summary>
    public string Role { get; set; } = "User";
}
