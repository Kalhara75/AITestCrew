using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Common;
using AiTestCrew.Agents.EventAssertAgent.Body;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Executes Azure Service Bus event assertions as post-steps. Only invoked
/// via the post-step pathway — never as a top-level test agent — because an
/// event assertion without a preceding action that should have caused the
/// event has no signal (same shape rule as <c>DbCheckAgent</c>).
///
/// <para>
/// Per <see cref="EventAssertStepDefinition"/>: resolves the connection
/// against <c>ServiceBusConnections.&lt;key&gt;</c> (per-env override → top-level
/// fallback), opens a receiver, runs a receive loop bounded by
/// <c>TimeoutSeconds</c> / <c>MaxMessages</c> / per-mode early-stop,
/// evaluates each criterion against each message via
/// <see cref="MessageFieldResolver"/> + <see cref="ScalarOperatorEvaluator"/>,
/// folds the per-message vector into a verdict via
/// <see cref="MatchModeEvaluator"/>, captures values from the first passing
/// message into <c>Metadata["capturedTokens"]</c>, settles messages on
/// <see cref="ReceiveMode.PeekLock"/> per <c>CompleteOnPass</c>, and emits
/// up to 10 received-message summaries for the run-detail UI under
/// <c>Metadata["serviceBusReceived"]</c>.
/// </para>
/// </summary>
public class AzureServiceBusEventAgent : BaseTestAgent
{
    private const int MaxDiagnosticMessages = 10;
    private const int BodyPreviewMaxBytes = 2048;
    private static readonly TimeSpan ReceiveBatchTimeout = TimeSpan.FromSeconds(1);

    private readonly IEnvironmentResolver _envResolver;
    private readonly IServiceBusReceiverFactory _receiverFactory;

    public override string Name => "Azure Service Bus Event Agent";
    public override string Role =>
        "Senior Integration Test Engineer who asserts that domain events were enqueued onto Azure Service Bus after a feature invocation.";

    public AzureServiceBusEventAgent(
        Kernel kernel,
        ILogger<AzureServiceBusEventAgent> logger,
        IEnvironmentResolver envResolver,
        IServiceBusReceiverFactory receiverFactory,
        PostStepOrchestrator postStepOrchestrator)
        : base(kernel, logger, postStepOrchestrator)
    {
        _envResolver = envResolver;
        _receiverFactory = receiverFactory;
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.Event_AzureServiceBus);

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        task.Parameters.TryGetValue("EnvironmentKey", out var rawEnvKey);
        var envKey = rawEnvKey as string;

        Logger.LogInformation("[{Agent}] Starting event-assert task: {Desc} (env: {Env})",
            Name, task.Description, envKey ?? "default");

        if (!task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
            || preloaded is not List<EventAssertStepDefinition> checks
            || checks.Count == 0)
        {
            steps.Add(TestStep.Err("event-assert",
                "AzureServiceBusEventAgent requires a PreloadedTestCases list of EventAssertStepDefinition. " +
                "Event asserts are only invoked as post-steps, never standalone."));
            return Build(task, steps, TestStatus.Error, "No event-assert definitions supplied.", sw);
        }

        for (var i = 0; i < checks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RunOneAsync(checks[i], i + 1, envKey, steps, ct);
        }

        var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
        var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
        var status = hasErrors ? TestStatus.Error
                   : hasFails ? TestStatus.Failed
                   : TestStatus.Passed;
        var summary = status == TestStatus.Passed
            ? $"{checks.Count} event assertion(s) passed."
            : $"{steps.Count(s => s.Status == TestStatus.Failed)} of {checks.Count} event assertion(s) failed.";
        return Build(task, steps, status, summary, sw);
    }

    /// <summary>
    /// Synchronous drain of a queue / subscription before the parent step runs.
    /// Invoked by <c>PostStepOrchestrator.RunPreParentDrainsAsync</c> when a
    /// post-step has <see cref="EventAssertStepDefinition.DrainBeforeParent"/>=true.
    /// Uses <see cref="ReceiveMode.ReceiveAndDelete"/>; receive-loops until the
    /// entity reports zero messages within a 2s idle window OR 10s ceiling.
    /// </summary>
    public async Task<int> DrainAsync(
        string connectionKey, ServiceBusEntity entity, string? envKey, CancellationToken ct)
    {
        var connection = _envResolver.ResolveServiceBusConnection(connectionKey, envKey);
        if (connection is null)
            throw new InvalidOperationException(
                $"Service Bus connection key '{connectionKey}' is not configured for env '{envKey ?? "default"}'");

        await using var receiver = await _receiverFactory.OpenAsync(
            connection, entity, ReceiveMode.ReceiveAndDelete, sessionId: null, ct);

        var idleStarted = DateTime.UtcNow;
        var hardDeadline = DateTime.UtcNow.AddSeconds(10);
        var totalDrained = 0;

        while (DateTime.UtcNow < hardDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await receiver.ReceiveBatchAsync(50, TimeSpan.FromSeconds(2), ct);
            if (batch.Count == 0)
            {
                if ((DateTime.UtcNow - idleStarted) > TimeSpan.FromSeconds(2))
                    break;
                continue;
            }
            totalDrained += batch.Count;
            idleStarted = DateTime.UtcNow;
        }

        Logger.LogInformation(
            "[{Agent}] Drained {Count} stale message(s) from {Entity} (connection={Key}, env={Env})",
            Name, totalDrained, FormatEntity(entity), connectionKey, envKey ?? "default");
        return totalDrained;
    }

    // ── Per-step pipeline ──────────────────────────────────────────────

    private async Task RunOneAsync(
        EventAssertStepDefinition def,
        int index,
        string? envKey,
        List<TestStep> steps,
        CancellationToken ct)
    {
        var action = $"event-assert[{index}] {def.Name}";

        var connection = _envResolver.ResolveServiceBusConnection(def.ConnectionKey, envKey);
        if (connection is null)
        {
            steps.Add(TestStep.Err(action,
                $"Service Bus connection key '{def.ConnectionKey}' is not configured for env '{envKey ?? "default"}'."));
            return;
        }

        if (string.IsNullOrWhiteSpace(def.Entity.Name))
        {
            steps.Add(TestStep.Err(action,
                "Entity name is empty — set Entity.Name to the queue or topic to receive from."));
            return;
        }
        if (def.Entity.Type == ServiceBusEntityType.Topic
            && string.IsNullOrWhiteSpace(def.Entity.SubscriptionName))
        {
            steps.Add(TestStep.Err(action,
                $"Topic '{def.Entity.Name}' requires Entity.SubscriptionName to be set."));
            return;
        }

        IServiceBusReceiverHandle receiver;
        try
        {
            receiver = await _receiverFactory.OpenAsync(
                connection, def.Entity, def.ReceiveMode, def.SessionId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(action,
                $"Failed to open receiver for {FormatEntity(def.Entity)} on '{def.ConnectionKey}': {ex.Message}"));
            return;
        }

        await using var _ = receiver;

        try
        {
            await EvaluateAsync(def, action, receiver, steps, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(action,
                $"Event assertion threw: {ex.Message} ({FormatEntity(def.Entity)})"));
        }
    }

    private async Task EvaluateAsync(
        EventAssertStepDefinition def,
        string action,
        IServiceBusReceiverHandle receiver,
        List<TestStep> steps,
        CancellationToken ct)
    {
        // ── Receive loop ──
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, def.TimeoutSeconds));
        var maxMessages = Math.Max(1, def.MaxMessages);

        var allMessages = new List<ReceivedMessageView>(Math.Min(maxMessages, 64));
        var perMessageResults = new List<MessageEvaluation>(Math.Min(maxMessages, 64));
        var passCount = 0;

        while (allMessages.Count < maxMessages
            && DateTime.UtcNow < deadline
            && !MatchModeEvaluator.CanShortCircuit(
                def.MatchMode, allMessages.Count, passCount, def.ExpectedCount, def.MaxCount))
        {
            ct.ThrowIfCancellationRequested();

            var remainingTime = deadline - DateTime.UtcNow;
            if (remainingTime <= TimeSpan.Zero) break;
            var perCall = remainingTime < ReceiveBatchTimeout ? remainingTime : ReceiveBatchTimeout;

            var remainingCapacity = maxMessages - allMessages.Count;
            var batch = await receiver.ReceiveBatchAsync(remainingCapacity, perCall, ct);
            if (batch.Count == 0) continue;

            foreach (var msg in batch)
            {
                if (allMessages.Count >= maxMessages) break;

                if (!string.IsNullOrEmpty(def.CorrelationFilter)
                    && !string.Equals(msg.CorrelationId, def.CorrelationFilter, StringComparison.Ordinal))
                {
                    // Out-of-scope message — abandon so other consumers (or this
                    // same receiver on a later loop) can pick it up. Don't count
                    // it against MaxMessages or evaluate criteria.
                    if (def.ReceiveMode == ReceiveMode.PeekLock)
                        await receiver.AbandonAsync(msg, ct);
                    continue;
                }

                allMessages.Add(msg);
                var eval = EvaluateMessage(msg, def);
                perMessageResults.Add(eval);
                if (eval.Passed) passCount++;
            }
        }

        // ── Verdict ──
        var verdict = MatchModeEvaluator.Evaluate(
            def.MatchMode, allMessages.Count, passCount, def.ExpectedCount, def.MaxCount);

        // ── Captures (only on green) ──
        Dictionary<string, string>? captured = null;
        string? captureFailure = null;
        if (verdict.Passed && def.Captures.Count > 0)
        {
            var firstPassIdx = perMessageResults.FindIndex(r => r.Passed);
            if (firstPassIdx >= 0)
            {
                var capResult = EvaluateCaptures(
                    def.Captures, allMessages[firstPassIdx],
                    perMessageResults[firstPassIdx].EffectiveBodyFormat,
                    perMessageResults[firstPassIdx].EffectiveBody,
                    action);
                if (capResult.Failure is not null)
                {
                    captureFailure = capResult.Failure;
                }
                else if (capResult.Captured.Count > 0)
                {
                    captured = capResult.Captured;
                }
            }
        }

        // ── Settlement (PeekLock) ──
        if (def.ReceiveMode == ReceiveMode.PeekLock)
        {
            await SettleAsync(def, verdict.Passed && captureFailure is null,
                allMessages, perMessageResults, receiver, ct);
        }

        // ── Step record ──
        if (captureFailure is not null)
        {
            var failStep = new TestStep
            {
                Action = action,
                Status = TestStatus.Failed,
                Summary = $"Capture failed: {captureFailure}. {verdict.Reason} ({FormatEntity(def.Entity)})",
            };
            failStep.Metadata["serviceBusReceived"] = BuildDiagnostics(def, allMessages, perMessageResults);
            steps.Add(failStep);
            return;
        }

        if (verdict.Passed)
        {
            var passStep = new TestStep
            {
                Action = action,
                Status = TestStatus.Passed,
                Summary = $"{verdict.Reason} ({FormatEntity(def.Entity)})",
            };
            if (captured is not null && captured.Count > 0)
                passStep.Metadata["capturedTokens"] = captured;
            // Include a small diagnostic payload even on green so the UI can
            // surface what was received under an "expand details" affordance.
            passStep.Metadata["serviceBusReceived"] = BuildDiagnostics(def, allMessages, perMessageResults);
            steps.Add(passStep);
            return;
        }

        var failed = new TestStep
        {
            Action = action,
            Status = TestStatus.Failed,
            Summary = $"{verdict.Reason} ({FormatEntity(def.Entity)})",
        };
        failed.Metadata["serviceBusReceived"] = BuildDiagnostics(def, allMessages, perMessageResults);
        steps.Add(failed);
    }

    private static MessageEvaluation EvaluateMessage(ReceivedMessageView msg, EventAssertStepDefinition def)
    {
        // Unwrap framework-applied compression (Rebus / NServiceBus / MassTransit
        // gzip-compress bodies above a size threshold; the producer signals it via
        // rbs2-content-encoding / Content-Encoding). When the body is gzipped JSON,
        // Body.<jsonpath> would otherwise see compressed garbage and fail with
        // "body is not JSON". Decompress once per message; reuse for criteria,
        // captures, format detection, and diagnostics.
        var decompression = BodyDecompressor.MaybeDecompress(msg.Body, msg.ApplicationProperties);
        var effectiveBody = decompression.Body;
        var effectiveFormat = BodyFormatDetector.Resolve(def.BodyFormat, msg.ContentType, effectiveBody);
        var perCriterion = new List<CriterionResult>(def.Criteria.Count);
        var passed = true;

        foreach (var c in def.Criteria)
        {
            var extract = MessageFieldResolver.Resolve(msg, c.Field, effectiveFormat, effectiveBody);
            if (extract.Status == ExtractStatus.Failed)
            {
                perCriterion.Add(new CriterionResult(c.Field, c.Operator.ToString(), false, extract.Error ?? "extraction failed"));
                passed = false;
                continue;
            }

            var isNull = extract.Status == ExtractStatus.FoundNull;
            var actual = extract.Value;

            var label = c.Field;
            var op = ScalarOperatorEvaluator.Evaluate(
                c.Operator, label, actual, isNull,
                c.Expected, c.Expected2, c.IgnoreCase,
                c.ToleranceSeconds, c.ToleranceDelta);

            perCriterion.Add(new CriterionResult(c.Field, c.Operator.ToString(), op.Passed, op.Reason));
            if (!op.Passed) passed = false;
        }

        return new MessageEvaluation(passed, effectiveFormat, perCriterion,
            effectiveBody, decompression.WasDecompressed, decompression.AppliedEncoding);
    }

    private CaptureEvaluation EvaluateCaptures(
        List<EventCapture> captures,
        ReceivedMessageView message,
        BodyFormat effectiveFormat,
        byte[] effectiveBody,
        string action)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in captures)
        {
            if (string.IsNullOrWhiteSpace(cap.As))
            {
                if (cap.Required)
                    return new CaptureEvaluation(captured,
                        $"capture for field '{cap.Field}' has no 'As' token name");
                continue;
            }

            var result = MessageFieldResolver.Resolve(message, cap.Field, effectiveFormat, effectiveBody);
            if (result.Status == ExtractStatus.Found)
            {
                captured[cap.As] = result.Value ?? "";
                continue;
            }

            if (result.Status == ExtractStatus.FoundNull)
            {
                if (cap.Required)
                    return new CaptureEvaluation(captured,
                        $"capture for field '{cap.Field}' is null");
                Logger.LogWarning(
                    "{Action}: optional capture for field '{Field}' is null — token '{{{{{Tok}}}}}' left undefined",
                    action, cap.Field, cap.As);
                continue;
            }

            // Failed
            if (cap.Required)
                return new CaptureEvaluation(captured,
                    $"capture for field '{cap.Field}': {result.Error ?? "extraction failed"}");
            Logger.LogWarning(
                "{Action}: optional capture for field '{Field}' did not resolve ({Reason}) — token '{{{{{Tok}}}}}' left undefined",
                action, cap.Field, result.Error, cap.As);
        }
        return new CaptureEvaluation(captured, null);
    }

    private static async Task SettleAsync(
        EventAssertStepDefinition def,
        bool overallPassed,
        IReadOnlyList<ReceivedMessageView> messages,
        IReadOnlyList<MessageEvaluation> perMessage,
        IServiceBusReceiverHandle receiver,
        CancellationToken ct)
    {
        // Settlement matrix:
        //   Pass + CompleteOnPass=true  → Complete every PASSING message; Abandon every FAILING.
        //   Pass + CompleteOnPass=false → Abandon all (debug-friendly — leaves messages in place).
        //   Fail                         → Abandon all.
        //
        // Use a per-message try/catch so a stale lock on one message doesn't prevent settling the rest.
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var passedThisOne = i < perMessage.Count && perMessage[i].Passed;

            try
            {
                if (overallPassed && def.CompleteOnPass && passedThisOne)
                    await receiver.CompleteAsync(msg, ct);
                else
                    await receiver.AbandonAsync(msg, ct);
            }
            catch
            {
                // Swallow settlement errors — message lock may have expired or
                // it may be a peek-only message (RawMessage null). The receiver's
                // dispose will abandon any remaining locks.
            }
        }
    }

    private static object BuildDiagnostics(
        EventAssertStepDefinition def,
        IReadOnlyList<ReceivedMessageView> messages,
        IReadOnlyList<MessageEvaluation> perMessage)
    {
        var summaries = new List<object>(Math.Min(messages.Count, MaxDiagnosticMessages));
        for (var i = 0; i < Math.Min(messages.Count, MaxDiagnosticMessages); i++)
        {
            var msg = messages[i];
            var eval = i < perMessage.Count ? perMessage[i] : null;
            // Prefer the decompressed (effective) body for the preview so the
            // run-detail panel shows readable JSON / XML on Rebus / NServiceBus
            // wrapped messages. Fall back to the raw body when the agent didn't
            // run (e.g. a peek path that didn't go through EvaluateMessage).
            var previewBody = eval?.EffectiveBody ?? msg.Body;
            summaries.Add(new
            {
                index = i,
                messageId = msg.MessageId,
                correlationId = msg.CorrelationId,
                contentType = msg.ContentType,
                enqueuedTimeUtc = msg.EnqueuedTimeUtc,
                applicationProperties = msg.ApplicationProperties.ToDictionary(
                    kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
                bodyPreview = TruncateBody(previewBody, eval?.EffectiveBodyFormat ?? def.BodyFormat),
                bodyFormat = (eval?.EffectiveBodyFormat ?? def.BodyFormat).ToString(),
                bodyLength = previewBody.Length,
                bodyEncoding = eval?.AppliedEncoding,        // "gzip" / "deflate" / null
                wasDecompressed = eval?.WasDecompressed ?? false,
                rawBodyLength = msg.Body.Length,             // pre-decompression
                passed = eval?.Passed,
                criteria = eval?.PerCriterion.Select(c => new
                {
                    field = c.Field,
                    op = c.Operator,
                    passed = c.Passed,
                    reason = c.Reason,
                }).ToList(),
            });
        }
        return new
        {
            totalReceived = messages.Count,
            passCount = perMessage.Count(r => r.Passed),
            matchMode = def.MatchMode.ToString(),
            expectedCount = def.ExpectedCount,
            maxCount = def.MaxCount,
            messages = summaries,
        };
    }

    private static string TruncateBody(byte[] body, BodyFormat fmt)
    {
        if (body.Length == 0) return "";
        if (fmt == BodyFormat.Binary)
            return $"<{body.Length} bytes — binary>";
        try
        {
            var text = Encoding.UTF8.GetString(body);
            if (text.Length > BodyPreviewMaxBytes)
                return text[..BodyPreviewMaxBytes] + "…";
            return text;
        }
        catch
        {
            return $"<{body.Length} bytes — non-UTF8>";
        }
    }

    private static string FormatEntity(ServiceBusEntity entity) =>
        entity.Type == ServiceBusEntityType.Queue
            ? $"queue '{entity.Name}'"
            : $"topic '{entity.Name}', sub '{entity.SubscriptionName}'";

    private TestResult Build(
        TestTask task, List<TestStep> steps, TestStatus status, string summary, Stopwatch sw) =>
        new()
        {
            ObjectiveId = task.Id,
            ObjectiveName = task.Description,
            AgentName = Name,
            Status = status,
            Summary = summary,
            Steps = steps,
            Duration = sw.Elapsed,
        };

    // ── Records ────────────────────────────────────────────────────────

    private sealed record MessageEvaluation(
        bool Passed,
        BodyFormat EffectiveBodyFormat,
        IReadOnlyList<CriterionResult> PerCriterion,
        byte[] EffectiveBody,
        bool WasDecompressed,
        string? AppliedEncoding);

    private sealed record CriterionResult(
        string Field, string Operator, bool Passed, string? Reason);

    private readonly record struct CaptureEvaluation(
        Dictionary<string, string> Captured, string? Failure);
}
