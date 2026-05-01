namespace AiTestCrew.WebApi.Models.Chat;

/// <summary>One turn in the chat conversation, as exchanged on the wire.</summary>
public record ChatMessage(string Role, string Content);

/// <summary>Caller-provided page context so fuzzy references like "this test set" resolve.</summary>
public record ChatRequestContext(string? ModuleId, string? TestSetId);

/// <summary>
/// Body of POST /api/chat/message.
/// <para>
/// <c>ConversationId</c> targets an existing persisted thread. When null/empty
/// the server creates a fresh conversation (auto-titling from the first user
/// message) and returns its id in <see cref="ChatResponse.ConversationId"/>.
/// </para>
/// <para>
/// <c>Messages</c> may be omitted by the client when <c>ConversationId</c> is
/// supplied — the server loads history from the DB. When provided, the LAST
/// entry must be the new user message; older entries are ignored (the DB is
/// the source of truth).
/// </para>
/// </summary>
public record ChatRequest(
    List<ChatMessage>? Messages,
    ChatRequestContext? Context,
    string? ConversationId = null,
    string? Message = null);

/// <summary>
/// Single action the client should execute after rendering the reply.
/// Kinds: <c>navigate</c>, <c>showData</c>, <c>confirmRun</c>, <c>confirmCreate</c>,
/// <c>confirmRecord</c>, <c>confirmCreatePostStep</c>.
/// </summary>
public class ChatAction
{
    public string Kind { get; set; } = "";
    public string? Path { get; set; }      // navigate
    public string? Title { get; set; }     // showData
    public object? Data { get; set; }      // showData payload or confirm* payload
    public string? Summary { get; set; }   // one-line human description on confirm cards
}

/// <summary>Response returned by POST /api/chat/message.</summary>
public class ChatResponse
{
    public string Reply { get; set; } = "";
    public List<ChatAction> Actions { get; set; } = new();

    /// <summary>The persisted conversation this turn belongs to. Always set on success.</summary>
    public string? ConversationId { get; set; }
}

/// <summary>Light-weight conversation summary for the picker / list endpoint.</summary>
public record ConversationSummary(
    string Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int MessageCount);

/// <summary>Full conversation transcript returned by GET /api/chat/conversations/{id}.</summary>
public record ConversationDetail(
    string Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int MessageCount,
    List<PersistedChatMessage> Messages);

/// <summary>One persisted message — includes the original action cards so the UI can re-render them.</summary>
public record PersistedChatMessage(
    string Id,
    string Role,
    string Content,
    List<ChatAction>? Actions,
    DateTime CreatedAt);

/// <summary>POST /api/chat/conversations body.</summary>
public record CreateConversationRequest(string? Title);

/// <summary>PATCH /api/chat/conversations/{id} body.</summary>
public record RenameConversationRequest(string Title);
