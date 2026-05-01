namespace AiTestCrew.Core.Models.Chat;

/// <summary>
/// One persisted turn within a <see cref="ChatConversation"/>.
/// <c>ActionsJson</c> is the verbatim serialised list of action cards
/// (navigate / showData / confirmRun / confirmCreate / confirmRecord /
/// confirmCreatePostStep) so the UI can re-render them after a refresh.
/// </summary>
public class ChatMessageRecord
{
    public string Id { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Role { get; set; } = "";   // "user" | "assistant"
    public string Content { get; set; } = "";
    public string? ActionsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
