using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.EventAssertAgent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Authors a populated <see cref="EventAssertStepDefinition"/> wrapped in a
/// <see cref="VerificationStep"/> with <c>Target = "Event_AzureServiceBus"</c>
/// from a natural-language fragment. On any failure returns an empty-but-valid
/// shell so the import does not fail. (REQ-019 AC#2, AC#7)
/// </summary>
public interface IEventAssertAuthoringService
{
    Task<VerificationStep> AuthorEventAssertPostStepAsync(string fragment, string? envKey, CancellationToken ct = default);
}

public class EventAssertAuthoringService : IEventAssertAuthoringService
{
    private readonly IChatCompletionService _chat;
    private readonly ILogger<EventAssertAuthoringService> _logger;

    public EventAssertAuthoringService(IChatCompletionService chat, ILogger<EventAssertAuthoringService> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<VerificationStep> AuthorEventAssertPostStepAsync(string fragment, string? envKey, CancellationToken ct = default)
    {
        var eventAssert = await AuthorAsync(fragment, ct);
        return new VerificationStep
        {
            Description = fragment,
            Target = "Event_AzureServiceBus",
            WaitBeforeSeconds = 0,
            Role = "Verification",
            EventAssert = eventAssert
        };
    }

    private async Task<EventAssertStepDefinition> AuthorAsync(string fragment, CancellationToken ct)
    {
        try
        {
            var history = new ChatHistory();
            history.AddUserMessage(BuildEventPrompt(fragment));
            var result = await _chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var json = CleanJson(result.Content ?? "{}");
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var def = JsonSerializer.Deserialize<EventAssertStepDefinition>(json, opts);
            if (def is not null && !string.IsNullOrWhiteSpace(def.ConnectionKey))
                return def;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event assert authoring failed for fragment: {Fragment}", fragment);
        }
        return new EventAssertStepDefinition
        {
            Name = fragment.Length > 80 ? fragment[..80] + "..." : fragment,
            ConnectionKey = "",
            Entity = new ServiceBusEntity { Type = ServiceBusEntityType.Queue, Name = "" },
            MatchMode = MatchMode.AnyMessage,
            TimeoutSeconds = 30
        };
    }

    private static string BuildEventPrompt(string fragment)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are authoring an Azure Service Bus Event Assert post-step for AITestCrew.");
        sb.AppendLine("Given the following natural-language test fragment, produce a JSON object matching the EventAssertStepDefinition shape.");
        sb.AppendLine("Fragment: " + fragment);
        sb.AppendLine("Return a JSON object with:");
        sb.AppendLine("  name: short label for the assertion");
        sb.AppendLine("  connectionKey: logical connection key (e.g. \"DefaultBus\") -- use the most likely name; the QA will correct if needed");
        sb.AppendLine("  entity: { type: Queue|Topic, name: \"<queue-or-topic-name>\", subscriptionName: \"<sub-name if Topic>\" }");
        sb.AppendLine("  bodyFormat: Auto|Json|Xml|Text (default Auto)");
        sb.AppendLine("  matchMode: AnyMessage|AllMessages|ExactlyOne|ExactCount|MinCount|MaxCount|CountRange (default AnyMessage)");
        sb.AppendLine("  timeoutSeconds: integer (default 30)");
        sb.AppendLine("  criteria: array of {field, operator, expected}");
        sb.AppendLine("  captures: array of {field, as, required}");
        sb.AppendLine("Rules: Do NOT invent connection strings. Use {{Token}} for runtime substitution.");
        sb.Append("JSON only -- no commentary.");
        return sb.ToString();
    }

    private static string CleanJson(string raw)
    {
        var s = raw.Trim();
        const string fence = "```";
        if (s.StartsWith(fence))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            var last = s.LastIndexOf(fence);
            if (last >= 0) s = s[..last];
        }
        return s.Trim();
    }
}
