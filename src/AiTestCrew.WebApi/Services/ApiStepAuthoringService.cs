using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Authors a populated <see cref="ApiTestDefinition"/> or API post-step from a
/// natural-language fragment. On any failure returns an empty-but-valid shell
/// so the import does not fail. (REQ-019 AC#3, AC#5, AC#7)
/// </summary>
public interface IApiStepAuthoringService
{
    /// <summary>Draft a top-level ApiTestDefinition from a NL description.</summary>
    Task<ApiTestDefinition> AuthorApiStepAsync(string fragment, string? stackKey, string? moduleKey, string? envKey, CancellationToken ct = default);

    /// <summary>Draft a VerificationStep with Target=API_REST from a NL description.</summary>
    Task<VerificationStep> AuthorApiPostStepAsync(string fragment, string? stackKey, string? moduleKey, string? envKey, CancellationToken ct = default);
}

public class ApiStepAuthoringService : IApiStepAuthoringService
{
    private readonly IChatCompletionService _chat;
    private readonly ILogger<ApiStepAuthoringService> _logger;

    public ApiStepAuthoringService(IChatCompletionService chat, ILogger<ApiStepAuthoringService> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<ApiTestDefinition> AuthorApiStepAsync(string fragment, string? stackKey, string? moduleKey, string? envKey, CancellationToken ct = default)
    {
        try
        {
            var history = new ChatHistory();
            history.AddUserMessage(BuildApiPrompt(fragment, stackKey, moduleKey));
            var result = await _chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var json = CleanJson(result.Content ?? "{}");
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var def = JsonSerializer.Deserialize<ApiTestDefinition>(json, opts);
            if (def is not null && !string.IsNullOrWhiteSpace(def.Endpoint))
                return def;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API step authoring failed for fragment: {Fragment}", fragment);
        }
        return new ApiTestDefinition
        {
            Method = "GET",
            Endpoint = "",
            ApiAssertions = [new ApiAssertion { Source = ApiAssertionSource.Status, Operator = AssertionOperator.Equals, Expected = "200" }]
        };
    }

    public async Task<VerificationStep> AuthorApiPostStepAsync(string fragment, string? stackKey, string? moduleKey, string? envKey, CancellationToken ct = default)
    {
        var apiDef = await AuthorApiStepAsync(fragment, stackKey, moduleKey, envKey, ct);
        return new VerificationStep
        {
            Description = fragment,
            Target = "API_REST",
            WaitBeforeSeconds = 0,
            Role = "Verification",
            Api = apiDef
        };
    }

    private static string BuildApiPrompt(string fragment, string? stackKey, string? moduleKey)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are authoring an API test step for AITestCrew.");
        sb.AppendLine("Given the following natural-language test fragment, produce a JSON object.");
        sb.AppendLine("Fragment: " + fragment);
        if (stackKey is not null) sb.AppendLine("API stack key: " + stackKey);
        if (moduleKey is not null) sb.AppendLine("API module key: " + moduleKey);
        sb.AppendLine("Return a JSON object with these fields:");
        sb.AppendLine("  method: GET|POST|PUT|PATCH|DELETE");
        sb.AppendLine("  endpoint: relative path; use {{Token}} for runtime substitution");
        sb.AppendLine("  body: optional string/JSON for POST/PUT/PATCH");
        sb.AppendLine("  apiAssertions: array of {source,operator,expected} -- at minimum [{source:Status,operator:Equals,expected:200}]");
        sb.AppendLine("  captures: array of {source,jsonPath?,as,required}");
        sb.AppendLine("Rules: method and endpoint are required. Do NOT invent base URLs.");
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
