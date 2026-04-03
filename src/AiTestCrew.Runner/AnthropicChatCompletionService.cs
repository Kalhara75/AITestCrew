using System.Runtime.CompilerServices;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTestCrew.Runner;

/// <summary>
/// Bridges Anthropic.SDK's native API to Semantic Kernel's IChatCompletionService.
/// Avoids the Microsoft.Extensions.AI bridge which has version-compatibility issues
/// between Anthropic.SDK and Microsoft.SemanticKernel.
/// </summary>
internal sealed class AnthropicChatCompletionService : IChatCompletionService
{
    private readonly MessagesEndpoint _messages;
    private readonly string _model;

    public IReadOnlyDictionary<string, object?> Attributes { get; }

    public AnthropicChatCompletionService(string apiKey, string model)
    {
        var client = new AnthropicClient(new APIAuthentication(apiKey));
        _messages = client.Messages;
        _model = model;
        Attributes = new Dictionary<string, object?> { ["model"] = model };
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var (systemMessages, messages) = MapHistory(chatHistory);

        var parameters = new MessageParameters
        {
            Model = _model,
            MaxTokens = 8096,
            Stream = false,
            Temperature = 1.0m,
            Messages = messages,
            System = systemMessages,
        };

        var response = await _messages.GetClaudeMessageAsync(parameters, cancellationToken);

        var text = response.Content?.OfType<Anthropic.SDK.Messaging.TextContent>().FirstOrDefault()?.Text
                   ?? response.Message.ToString()
                   ?? string.Empty;

        return [new ChatMessageContent(AuthorRole.Assistant, text)];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (systemMessages, messages) = MapHistory(chatHistory);

        var parameters = new MessageParameters
        {
            Model = _model,
            MaxTokens = 8096,
            Stream = true,
            Temperature = 1.0m,
            Messages = messages,
            System = systemMessages,
        };

        await foreach (var chunk in _messages.StreamClaudeMessageAsync(parameters, cancellationToken))
        {
            if (chunk.Delta?.Text is { Length: > 0 } text)
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, text);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (List<SystemMessage> system, List<Message> messages) MapHistory(ChatHistory history)
    {
        var system = history
            .Where(m => m.Role == AuthorRole.System)
            .Select(m => new SystemMessage(m.Content ?? string.Empty))
            .ToList();

        var messages = history
            .Where(m => m.Role != AuthorRole.System)
            .Select(m => new Message(
                m.Role == AuthorRole.User ? RoleType.User : RoleType.Assistant,
                m.Content ?? string.Empty))
            .ToList();

        return (system, messages);
    }
}
