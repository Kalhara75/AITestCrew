using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.PostSteps;
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

    /// <summary>
    /// Constructor overload for agents that support post-steps on their test cases.
    /// Pass the DI-injected <see cref="PostStepOrchestrator"/> through and it'll be
    /// used by <see cref="RunPostStepsAsync"/> automatically.
    /// </summary>
    protected BaseTestAgent(Kernel kernel, ILogger logger, PostStepOrchestrator postStepOrchestrator)
        : this(kernel, logger)
    {
        PostStepOrchestrator = postStepOrchestrator;
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

    // ─────────────────────────────────────────────────────
    // Post-step hooks (Slice 1 of the generalized post-step work)
    //
    // Any agent that wants its test cases to support post-steps (sub-actions /
    // sub-verifications) overrides BuildPostStepContext to publish the
    // {{Token}} values its parent test case contributes, then calls
    // RunPostStepsAsync from its per-case execution loop.
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// DI-injected <see cref="PostStepOrchestrator"/> used by <see cref="RunPostStepsAsync"/>.
    /// Set via constructor in subclasses that support post-steps (web/desktop/api/aseXml).
    /// Null when an agent hasn't been wired yet — <c>RunPostStepsAsync</c> is a no-op in
    /// that case so partial adoption during Slice 1 doesn't break existing runs.
    /// </summary>
    protected PostStepOrchestrator? PostStepOrchestrator { get; set; }

    /// <summary>
    /// Publishes the {{Token}} values this test case contributes to its post-steps.
    /// Default: empty dictionary. Each agent overrides to populate keys relevant
    /// to its parent case (URL, response body, generated IDs, etc.).
    /// </summary>
    protected virtual IDictionary<string, string> BuildPostStepContext(
        object parentTestCase,
        IReadOnlyList<TestStep> parentSteps)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parent-kind string used when this agent enqueues deferred post-steps.
    /// Override to one of <c>"WebUi"</c>, <c>"DesktopUi"</c>, <c>"Api"</c>,
    /// <c>"AseXml"</c>, <c>"AseXmlDeliver"</c>. Default <c>""</c> means deferral
    /// is disabled on this agent (inline-only).
    /// </summary>
    protected virtual string PostStepParentKind => "";

    /// <summary>
    /// Runs all post-steps for a parent test case. Prefers deferred queueing
    /// when (a) the orchestrator says they qualify (enabled + wait &gt; threshold +
    /// queue repos wired) AND (b) the task carries RunId + TestSetId so we can
    /// finalise the parent run later. Otherwise runs inline.
    ///
    /// Merges the agent's environment parameters into the context so post-steps
    /// can reference env-scoped tokens the parent case could reference.
    ///
    /// No-op when the orchestrator hasn't been injected or the post-step list
    /// is empty.
    /// </summary>
    protected async Task RunPostStepsAsync(
        IReadOnlyList<VerificationStep> postSteps,
        object parentTestCase,
        IReadOnlyList<TestStep> parentSteps,
        int parentStepIndex,
        List<TestStep> stepSink,
        string? environmentKey,
        IReadOnlyDictionary<string, string> environmentParameters,
        CancellationToken ct,
        TestTask? task = null)
    {
        if (postSteps.Count == 0 || PostStepOrchestrator is null) return;

        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in environmentParameters)
            if (!string.IsNullOrEmpty(v)) context[k] = v;

        var agentCtx = BuildPostStepContext(parentTestCase, parentSteps);
        foreach (var (k, v) in agentCtx)
            if (!string.IsNullOrEmpty(v)) context[k] = v;

        // Try deferred path first when the agent opts in via PostStepParentKind
        // and the wait thresholds qualify.
        if (task is not null
            && !string.IsNullOrEmpty(PostStepParentKind)
            && PostStepOrchestrator.ShouldDefer(postSteps))
        {
            var runId = GetTaskParam<string>(task, "RunId") ?? "";
            var testSetId = GetTaskParam<string>(task, "TestSetId") ?? "";
            var moduleId = GetTaskParam<string>(task, "ModuleId") ?? "";
            var objectiveId = GetTaskParam<string>(task, "ObjectiveId") ?? task.Id;
            var objectiveName = GetTaskParam<string>(task, "ObjectiveName") ?? task.Description;

            var enqueued = await PostStepOrchestrator.TryEnqueueDeferredAsync(
                postSteps, context, parentStepIndex, stepSink, environmentKey,
                PostStepParentKind, runId, moduleId, testSetId,
                objectiveId, objectiveName, ct);

            if (enqueued) return;
            // Fell through to inline below — log the reason once so the UX is diagnosable.
            Logger.LogWarning(
                "[{Agent}] Deferred enqueue failed for parent '{Objective}'; falling back to inline.",
                Name, objectiveName);
        }

        await PostStepOrchestrator.RunInlineAsync(
            postSteps, context, parentStepIndex,
            stepSink, environmentKey, this, ct);
    }

    private static T? GetTaskParam<T>(TestTask task, string key) where T : class
    {
        return task.Parameters.TryGetValue(key, out var v) && v is T typed ? typed : null;
    }
}