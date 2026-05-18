using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Authors a populated <see cref="DbCheckStepDefinition"/> wrapped in a
/// <see cref="VerificationStep"/> with <c>Target = "Db_SqlServer"</c> from a
/// natural-language fragment. On any failure returns an empty-but-valid shell
/// so the import does not fail. (REQ-019 AC#1, AC#7)
/// </summary>
public interface IDbCheckAuthoringService
{
    Task<VerificationStep> AuthorDbCheckPostStepAsync(string fragment, string? envKey, CancellationToken ct = default);
}

public class DbCheckAuthoringService : IDbCheckAuthoringService
{
    private readonly IChatCompletionService _chat;
    private readonly ILogger<DbCheckAuthoringService> _logger;

    public DbCheckAuthoringService(IChatCompletionService chat, ILogger<DbCheckAuthoringService> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<VerificationStep> AuthorDbCheckPostStepAsync(string fragment, string? envKey, CancellationToken ct = default)
    {
        var dbCheck = await AuthorAsync(fragment, ct);
        return new VerificationStep
        {
            Description = fragment,
            Target = "Db_SqlServer",
            WaitBeforeSeconds = 0,
            Role = "Verification",
            DbCheck = dbCheck
        };
    }

    private async Task<DbCheckStepDefinition> AuthorAsync(string fragment, CancellationToken ct)
    {
        try
        {
            var history = new ChatHistory();
            history.AddUserMessage(BuildDbPrompt(fragment));
            var result = await _chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var json = CleanJson(result.Content ?? "{}");
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var def = JsonSerializer.Deserialize<DbCheckStepDefinition>(json, opts);
            if (def is not null && !string.IsNullOrWhiteSpace(def.Sql))
                return def;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB check authoring failed for fragment: {Fragment}", fragment);
        }
        return new DbCheckStepDefinition
        {
            Name = fragment.Length > 80 ? fragment[..80] + "..." : fragment,
            ConnectionKey = "BravoDb",
            Sql = "",
            TimeoutSeconds = 15
        };
    }

    private static string BuildDbPrompt(string fragment)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are authoring a DB Assert post-step for AITestCrew.");
        sb.AppendLine("Given the following natural-language test fragment, produce a JSON object matching the DbCheckStepDefinition shape.");
        sb.AppendLine("Fragment: " + fragment);
        sb.AppendLine("Return a JSON object with:");
        sb.AppendLine("  name: short label for the check");
        sb.AppendLine("  connectionKey: always \"BravoDb\" unless the fragment explicitly mentions a different DB");
        sb.AppendLine("  sql: a single READ-ONLY SELECT statement; use {{Token}} placeholders for runtime substitution");
        sb.AppendLine("  expectedRowCount: (optional int) for simple row-existence checks");
        sb.AppendLine("  columnAssertions: (optional array) of {column, operator, expected} for per-column checks");
        sb.AppendLine("  captures: (optional array) of {column, as, required} to capture values for downstream steps");
        sb.AppendLine("  timeoutSeconds: integer (default 15)");
        sb.AppendLine("Rules:");
        sb.AppendLine("  - sql MUST be a single SELECT; no INSERT/UPDATE/DELETE/DDL.");
        sb.AppendLine("  - Do NOT invent table names -- use generic placeholders if unsure.");
        sb.AppendLine("  - Use {{Token}} placeholders for NMI, MessageID, and other runtime values.");
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
