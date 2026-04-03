using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.Base;

/// <summary>
/// Base class for all test agents. Provides LLM reasoning capabilities
/// through Semantic Kernel. Subclasses implement the actual test execution.
/// </summary>
public abstract class BaseTestAgent : ITestAgent
{
    protected readonly Kernel Kernel;
    protected readonly ILogger Logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public abstract string Name { get; }
    public abstract string Role { get; }

    protected BaseTestAgent(Kernel kernel, ILogger logger)
    {
        Kernel = kernel;
        Logger = logger;
    }

    public abstract Task<bool> CanHandleAsync(TestTask task);
    public abstract Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct);

    // ─────────────────────────────────────────────────────
    // LLM Helper Methods (used by all agents)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Send a prompt to the LLM and get a raw string response.
    /// </summary>
    protected async Task<string> AskLlmAsync(string prompt, CancellationToken ct = default)
    {
        var chatService = Kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage($"""
            You are a {Role}. You are part of an automated AI testing crew.
            You always respond with valid JSON when asked for structured data.
            You never include markdown fences or explanation outside the JSON.
            Be precise, technical, and thorough.
            """);
        history.AddUserMessage(prompt);

        Logger.LogDebug("[{Agent}] LLM prompt: {Prompt}", Name, prompt[..Math.Min(200, prompt.Length)]);

        var response = await chatService.GetChatMessageContentAsync(
            history, cancellationToken: ct);

        var result = response.Content ?? "";
        Logger.LogDebug("[{Agent}] LLM response: {Response}", Name, result[..Math.Min(200, result.Length)]);

        return result;
    }

    /// <summary>
    /// Send a prompt to the LLM and parse the response as JSON.
    /// Handles common issues: markdown fences, extra text before/after JSON.
    /// </summary>
    protected async Task<T?> AskLlmForJsonAsync<T>(string prompt, CancellationToken ct = default)
    {
        var raw = await AskLlmAsync(prompt, ct);
        var cleaned = CleanJsonResponse(raw);

        try
        {
            return JsonSerializer.Deserialize<T>(cleaned, JsonOpts);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning("[{Agent}] Failed to parse LLM JSON: {Error}\nRaw: {Raw}",
                Name, ex.Message, cleaned[..Math.Min(500, cleaned.Length)]);
            return default;
        }
    }

    /// <summary>
    /// Ask the LLM to summarise test results into a human-readable string.
    /// </summary>
    protected async Task<string> SummariseResultsAsync(
        List<TestStep> steps, CancellationToken ct = default)
    {
        var stepsJson = JsonSerializer.Serialize(steps.Select(s => new
        {
            s.Action,
            s.Summary,
            Status = s.Status.ToString()
        }), JsonOpts);

        var prompt = $"""
            Summarise these test results in 2-3 sentences.
            Focus on what passed, what failed, and the likely root cause of any failures.
            Respond with plain text, not JSON.

            Steps:
            {stepsJson}
            """;

        return await AskLlmAsync(prompt, ct);
    }

    // ─────────────────────────────────────────────────────
    // JSON cleaning utilities
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Strips markdown fences, leading/trailing text, and extracts
    /// the JSON array or object from an LLM response.
    /// </summary>
    protected static string CleanJsonResponse(string raw)
    {
        // Remove markdown code fences
        var cleaned = Regex.Replace(raw, @"```(?:json)?\s*", "");
        cleaned = cleaned.Replace("```", "").Trim();

        // Find the first [ or { and the last ] or }
        var firstBracket = cleaned.IndexOfAny(['{', '[']);
        var lastBracket = cleaned.LastIndexOfAny(['}', ']']);

        if (firstBracket >= 0 && lastBracket > firstBracket)
        {
            cleaned = cleaned[firstBracket..(lastBracket + 1)];
        }

        return cleaned;
    }

    protected static JsonSerializerOptions GetJsonOptions() => JsonOpts;
}