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

        // Fail-fast: if the primary test case produced any failed/errored steps,
        // skip the entire post-step block. Running follow-ups after the parent
        // failed is never useful — they depend on the primary case having done
        // its thing. Cleanup belongs in TeardownSteps, which runs separately.
        if (parentSteps.Any(s => s.Status is TestStatus.Failed or TestStatus.Error))
        {
            for (var i = 0; i < postSteps.Count; i++)
            {
                stepSink.Add(new TestStep
                {
                    Action = $"post[{parentStepIndex}.{i + 1}]",
                    Summary = "Skipped — parent test case failed",
                    Status = TestStatus.Skipped,
                });
            }
            return;
        }

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

    /// <summary>
    /// Runs the pre-parent drain hook for any post-step on this test case
    /// that has <c>EventAssert.DrainBeforeParent=true</c>. Returns
    /// <c>true</c> when the drain succeeded (or was a no-op), <c>false</c>
    /// when it failed — the caller should append the synthesised Error step
    /// (already added to <paramref name="stepSink"/>) and skip the parent
    /// invocation. Strict-mode contract: running the parent against a
    /// half-drained entity yields misleading verdicts.
    ///
    /// No-op when (a) the orchestrator isn't wired, (b) the post-step list
    /// is empty, or (c) no post-step requested DrainBeforeParent.
    /// </summary>
    protected async Task<bool> TryPreParentDrainsAsync(
        IReadOnlyList<VerificationStep> postSteps,
        int tcIndex,
        List<TestStep> stepSink,
        string? environmentKey,
        IReadOnlyDictionary<string, string> environmentParameters,
        CancellationToken ct)
    {
        if (postSteps.Count == 0 || PostStepOrchestrator is null) return true;
        if (!PostStepOrchestrator.HasDrainBeforeParent(postSteps)) return true;

        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in environmentParameters)
            if (!string.IsNullOrEmpty(v)) context[k] = v;

        try
        {
            await PostStepOrchestrator.RunPreParentDrainsAsync(
                postSteps, context, environmentKey, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            stepSink.Add(TestStep.Err(
                $"pre-parent-drain[{tcIndex}]",
                $"DrainBeforeParent failed: {ex.Message}"));
            return false;
        }
    }

    // ─────────────────────────────────────────────────────
    // VerifyOnly path — generic helper used by non-delivery agents
    // (delivery agent has its own bespoke VerifyOnlyAsync because it needs to
    // reconstruct MessageID/Filename from execution history).
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Skips the parent step entirely and dispatches only the post-steps for
    /// each preloaded test case. Honours <c>task.Parameters["VerifyStepFilter"]</c>
    /// when present so a single post-step can be re-run in isolation.
    ///
    /// Caller is responsible for env-param-substituting <paramref name="testCases"/>
    /// before passing them in (matches the existing per-agent reuse-mode pattern).
    /// </summary>
    protected async Task<TestResult> RunVerifyOnlyAsync<TCase>(
        TestTask task,
        IReadOnlyList<TCase> testCases,
        Func<TCase, IReadOnlyList<VerificationStep>> getPostSteps,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct) where TCase : class
    {
        var steps = new List<TestStep>();
        var envKey = task.Parameters.TryGetValue("EnvironmentKey", out var ek) ? ek as string : null;
        var envParams = AiTestCrew.Agents.Environment.StepParameterSubstituter
            .ReadEnvironmentParameters(task.Parameters);
        var filter = task.Parameters.TryGetValue("VerifyStepFilter", out var fObj)
            ? fObj as VerifyStepFilter : null;
        // Wait overrides: an explicit VerificationWaitOverride (CLI --wait, UI Verify
        // button) wins; a single-step filter ALWAYS forces 0 because the user is
        // re-running an individual step for validation/correction — the original
        // wait was for the parent's side-effects to settle, which already happened
        // on the prior full run.
        int? waitOverride = task.Parameters.TryGetValue("VerificationWaitOverride", out var wo)
            && wo is int w ? w : (int?)null;
        if (filter is not null) waitOverride = 0;

        if (PostStepOrchestrator is null)
        {
            steps.Add(TestStep.Err("verify-only",
                $"PostStepOrchestrator is not wired into {Name}; cannot run VerifyOnly here."));
            return new TestResult
            {
                ObjectiveId = task.Id,
                ObjectiveName = task.Description,
                AgentName = Name,
                Status = TestStatus.Error,
                Summary = "VerifyOnly unsupported on this agent.",
                Steps = steps,
                Duration = sw.Elapsed,
            };
        }

        steps.Add(TestStep.Pass("verify-only-load",
            $"Loaded {testCases.Count} saved test case(s); skipping parent step, running post-steps only."));

        var anyDispatched = false;
        for (var tcIdx = 0; tcIdx < testCases.Count; tcIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var tc = testCases[tcIdx];
            var posts = getPostSteps(tc);
            if (posts.Count == 0)
            {
                steps.Add(TestStep.Pass($"verify-only[{tcIdx + 1}]",
                    "No post-steps defined on this test case."));
                continue;
            }

            var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in envParams)
                if (!string.IsNullOrEmpty(v)) context[k] = v;
            var agentCtx = BuildPostStepContext(tc!, Array.Empty<TestStep>());
            foreach (var (k, v) in agentCtx)
                if (!string.IsNullOrEmpty(v)) context[k] = v;

            var preCount = steps.Count;
            await PostStepOrchestrator.RunInlineAsync(
                posts, context, tcIdx + 1, steps, envKey, this, ct,
                parentKind: PostStepParentKind, filter: filter, waitOverride: waitOverride);
            if (steps.Count > preCount) anyDispatched = true;
        }

        if (filter is not null && !anyDispatched)
        {
            steps.Add(TestStep.Err("verify-only-filter",
                $"VerifyStepFilter ({filter.ParentKind}.{filter.ParentStepIndex}.{filter.PostStepIndex}) " +
                "did not match any post-step on this objective."));
        }

        var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
        var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
        var status = hasErrors ? TestStatus.Error
                   : hasFails ? TestStatus.Failed
                   : TestStatus.Passed;

        return new TestResult
        {
            ObjectiveId = task.Id,
            ObjectiveName = task.Description,
            AgentName = Name,
            Status = status,
            Summary = filter is not null
                ? $"VerifyOnly: ran 1 post-step (filtered)."
                : $"VerifyOnly: ran post-steps for {testCases.Count} test case(s).",
            Steps = steps,
            Duration = sw.Elapsed,
        };
    }
}