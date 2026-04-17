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
}
