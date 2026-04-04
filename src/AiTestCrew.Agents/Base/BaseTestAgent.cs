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

    private static readonly JsonSerializerOptions JsonOpts = LlmJsonHelper.JsonOpts;

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
        var result = LlmJsonHelper.DeserializeLlmResponse<T>(raw);
        if (result is null)
        {
            Logger.LogWarning("[{Agent}] Failed to parse LLM JSON response. Raw: {Raw}",
                Name, raw[..Math.Min(500, raw.Length)]);
        }
        return result;
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

    protected static string CleanJsonResponse(string raw) =>
        LlmJsonHelper.CleanJsonResponse(raw);

    protected static JsonSerializerOptions GetJsonOptions() => JsonOpts;
}