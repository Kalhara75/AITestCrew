namespace AiTestCrew.Core.Models.Chat;

/// <summary>
/// A persisted Assistant conversation thread, owned by a single user.
/// Messages are stored separately in <see cref="ChatMessageRecord"/>.
/// </summary>
public class ChatConversation
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
