using System.Diagnostics;
using System.Text.Json;
using AiTestCrew.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTestCrew.WebApi.Endpoints;

public static class LlmEndpoints
{
    private static readonly JsonSerializerOptions _camelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static RouteGroupBuilder MapLlmEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/llm/chat — proxy LLM call from a remote agent to the server's
        // configured IChatCompletionService. Agents authenticated by X-Api-Key carry
        // no local LLM key; this endpoint lets them use the server's key transparently.
        group.MapPost("/chat", async (
            LlmChatRequest? request,
            IChatCompletionService chatService,
            HttpContext ctx,
            ILogger<LlmChatRequest> logger,
            CancellationToken ct) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null)
                return Results.Unauthorized();

            if (request is null || request.Messages is not { Count: > 0 })
                return Results.BadRequest(new { error = "messages array is required and must not be empty" });

            var sw = Stopwatch.StartNew();

            try
            {
                var history = new ChatHistory();
                foreach (var msg in request.Messages)
                {
                    switch (msg.Role?.ToLowerInvariant())
                    {
                        case "system":    history.AddSystemMessage(msg.Content ?? "");    break;
                        case "assistant": history.AddAssistantMessage(msg.Content ?? ""); break;
                        default:          history.AddUserMessage(msg.Content ?? "");      break;
                    }
                }

                PromptExecutionSettings? settings = null;
                if (request.MaxTokens.HasValue || request.Temperature.HasValue)
                {
                    settings = new PromptExecutionSettings
                    {
                        ExtensionData = new Dictionary<string, object>()
                    };
                    if (request.MaxTokens.HasValue)
                        settings.ExtensionData["max_tokens"] = request.MaxTokens.Value;
                    if (request.Temperature.HasValue)
                        settings.ExtensionData["temperature"] = (double)request.Temperature.Value;
                }

                var response = await chatService.GetChatMessageContentAsync(
                    history, settings, cancellationToken: ct);

                sw.Stop();

                var content = response.Content ?? "";
                var metadata = response.Metadata;
                var inputTokens  = metadata?.TryGetValue("InputTokenCount",  out var it) == true ? (int?)Convert.ToInt32(it) : null;
                var outputTokens = metadata?.TryGetValue("OutputTokenCount", out var ot) == true ? (int?)Convert.ToInt32(ot) : null;

                logger.LogInformation(
                    "LLM proxy: user={UserId} agent={AgentId} model={Model} inputTokens={InputTokens} outputTokens={OutputTokens} latencyMs={LatencyMs}",
                    user.Id,
                    ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "unknown",
                    request.Model ?? "(server default)",
                    inputTokens,
                    outputTokens,
                    sw.ElapsedMilliseconds);

                return Results.Ok(new LlmChatResponse
                {
                    Content = content,
                    Model   = request.Model ?? (response.ModelId ?? ""),
                    Usage   = new LlmUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "LLM proxy error for user {UserId}", user.Id);
                return Results.Problem(
                    title: "LLM call failed",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        return group;
    }
}

// ── Request / response models ────────────────────────────────────────────────

public record LlmChatRequest
{
    public string?         Model       { get; init; }
    public List<LlmMessage>? Messages  { get; init; }
    public int?            MaxTokens   { get; init; }
    public decimal?        Temperature { get; init; }
}

public record LlmMessage
{
    public string? Role    { get; init; }
    public string? Content { get; init; }
}

public record LlmChatResponse
{
    public string   Content { get; init; } = "";
    public string   Model   { get; init; } = "";
    public LlmUsage? Usage  { get; init; }
}

public record LlmUsage
{
    public int? InputTokens  { get; init; }
    public int? OutputTokens { get; init; }
}
