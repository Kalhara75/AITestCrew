using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTestCrew.Agents.Llm;

/// <summary>
/// An <see cref="IChatCompletionService"/> that forwards calls to the AITestCrew server's
/// <c>POST /api/llm/chat</c> endpoint instead of calling an LLM provider directly.
/// Allows distributed agents to run without a local LLM API key.
/// </summary>
public sealed class RemoteChatCompletionService : IChatCompletionService
{
    private readonly HttpClient _client;
    private readonly string _serverUrl;
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly ILogger<RemoteChatCompletionService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive  = true,
        DefaultIgnoreCondition       = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyDictionary<string, object?> Attributes { get; }

    public RemoteChatCompletionService(
        HttpClient client,
        string serverUrl,
        string apiKey,
        string defaultModel,
        ILogger<RemoteChatCompletionService> logger)
    {
        _client       = client;
        _serverUrl    = serverUrl.TrimEnd('/');
        _apiKey       = apiKey;
        _defaultModel = defaultModel;
        _logger       = logger;
        Attributes    = new Dictionary<string, object?> { ["model"] = defaultModel };
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatHistory
            .Select(m => new RemoteLlmMessage
            {
                Role    = m.Role == AuthorRole.System    ? "system"
                        : m.Role == AuthorRole.Assistant ? "assistant"
                        : "user",
                Content = m.Content ?? "",
            })
            .ToList();

        int? maxTokens = null;
        decimal? temperature = null;
        if (executionSettings?.ExtensionData is { } ext)
        {
            if (ext.TryGetValue("max_tokens", out var mt) && mt is not null)
                maxTokens = Convert.ToInt32(mt);
            if (ext.TryGetValue("temperature", out var temp) && temp is not null)
                temperature = Convert.ToDecimal(temp);
        }

        var request = new RemoteLlmRequest
        {
            Messages    = messages,
            MaxTokens   = maxTokens,
            Temperature = temperature,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/api/llm/chat");
        req.Headers.Add("X-Api-Key", _apiKey);
        req.Content = JsonContent.Create(request, options: _json);

        HttpResponseMessage resp;
        try
        {
            resp = await _client.SendAsync(req, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException(
                $"RemoteChatCompletionService: failed to reach {_serverUrl}/api/llm/chat — {ex.Message}", ex);
        }

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            string providerError = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                providerError = doc.RootElement.TryGetProperty("providerError", out var pe) ? pe.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(providerError) && doc.RootElement.TryGetProperty("detail", out var det))
                    providerError = det.GetString() ?? "";
            }
            catch { /* ignore parse failure */ }

            _logger.LogError(
                "RemoteChatCompletionService: server returned {Status}. providerError={ProviderError}",
                (int)resp.StatusCode, providerError);

            throw new HttpRequestException(
                $"LLM proxy returned {(int)resp.StatusCode}. providerError: {providerError}");
        }

        var result = JsonSerializer.Deserialize<RemoteLlmResponse>(body, _json)
                     ?? throw new InvalidOperationException("LLM proxy returned an empty response.");

        return [new ChatMessageContent(AuthorRole.Assistant, result.Content ?? "")];
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "RemoteChatCompletionService does not support streaming. " +
            "Use GetChatMessageContentsAsync instead.");
    }

    // ── Private DTOs ─────────────────────────────────────────────────────────

    private sealed class RemoteLlmRequest
    {
        public List<RemoteLlmMessage>? Messages    { get; set; }
        public int?                    MaxTokens   { get; set; }
        public decimal?                Temperature { get; set; }
    }

    private sealed class RemoteLlmMessage
    {
        public string? Role    { get; set; }
        public string? Content { get; set; }
    }

    private sealed class RemoteLlmResponse
    {
        public string? Content      { get; set; }
        public string? Model        { get; set; }
        public string? StopReason   { get; set; }
    }
}
