using AiTestCrew.Core.Models.Chat;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Per-user storage for Assistant conversations. Every method takes the
/// owning <c>userId</c> and the implementation MUST include it in the
/// <c>WHERE</c> clause — defence-in-depth ring-fencing so a stolen
/// conversation id alone cannot read another user's thread.
/// </summary>
public interface IChatConversationRepository
{
    Task<ChatConversation> CreateAsync(string userId, string title, int maxConversationsPerUser, CancellationToken ct = default);
    Task<IReadOnlyList<ChatConversation>> ListByUserAsync(string userId, CancellationToken ct = default);
    Task<ChatConversation?> GetAsync(string conversationId, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string conversationId, string userId, CancellationToken ct = default);
    Task AppendMessageAsync(string conversationId, string userId, ChatMessageRecord message, CancellationToken ct = default);
    Task DeleteAsync(string conversationId, string userId, CancellationToken ct = default);
    Task RenameAsync(string conversationId, string userId, string title, CancellationToken ct = default);
}
