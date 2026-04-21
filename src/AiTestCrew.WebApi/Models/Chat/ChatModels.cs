namespace AiTestCrew.WebApi.Models.Chat;

/// <summary>One turn in the chat conversation held client-side and resent each request.</summary>
public record ChatMessage(string Role, string Content);

/// <summary>Caller-provided page context so fuzzy references like "this test set" resolve.</summary>
public record ChatRequestContext(string? ModuleId, string? TestSetId);

/// <summary>Body of POST /api/chat/message.</summary>
public record ChatRequest(List<ChatMessage> Messages, ChatRequestContext? Context);

/// <summary>
/// Single action the client should execute after rendering the reply.
/// Kinds: <c>navigate</c>, <c>showData</c>, <c>confirmRun</c>, <c>confirmCreate</c>.
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
}
