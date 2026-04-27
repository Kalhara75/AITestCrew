using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.DbAgent;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.PostSteps;

/// <summary>
/// Shared orchestration for post-steps attached to any parent test case
/// (web UI, desktop UI, API, aseXML generate, aseXML delivery).
///
/// Slice 1 added inline dispatch via <see cref="RunInlineAsync"/>.
/// Slice 2 lifts the deferred path out of <c>AseXmlDeliveryAgent</c> so
/// <em>any</em> parent can queue long-wait sub-steps: <see cref="TryEnqueueDeferredAsync"/>
/// enqueues a <see cref="DeferredVerificationRequest"/> and pending row, and
/// <see cref="RunDeferredAttemptAsync"/> replays one claim attempt against the
/// snapshotted context, retrying via re-enqueue or finalising the parent run.
///
/// Token substitution on each post-step's payload is delegated to
/// <see cref="StepParameterSubstituter"/> which walks every carrier field
/// (<c>WebUi</c>, <c>DesktopUi</c>, <c>Api</c>, <c>AseXml</c>, <c>AseXmlDeliver</c>,
/// <c>DbCheck</c>).
/// </summary>
public class PostStepOrchestrator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PostStepOrchestrator> _logger;
    private readonly TestEnvironmentConfig _config;

    private static readonly JsonSerializerOptions DeferredJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PostStepOrchestrator(
        IServiceProvider services,
        ILogger<PostStepOrchestrator> logger,
        TestEnvironmentConfig config)
    {
        _services = services;
        _logger = logger;
        _config = config;
    }

    // ─────────────────────────────────────────────────────
    // Inline path
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs every post-step inline, one after another, appending the child's
    /// result steps to <paramref name="stepSink"/>. Each post-step's
    /// <see cref="VerificationStep.WaitBeforeSeconds"/> is honoured as a hard
    /// wait before the step executes.
    ///
    /// <paramref name="callingAgent"/> is the parent agent instance (passed so we
    /// can filter it out when looking for a sibling to dispatch to). Pass
    /// <c>null</c> if no self-filter is required.
    /// </summary>
    public async Task RunInlineAsync(
        IReadOnlyList<VerificationStep> postSteps,
        IReadOnlyDictionary<string, string> context,
        int parentStepIndex,
        List<TestStep> stepSink,
        string? environmentKey,
        ITestAgent? callingAgent,
        CancellationToken ct)
    {
        if (postSteps.Count == 0) return;

        var siblings = ResolveSiblings(callingAgent);

        for (var pIdx = 0; pIdx < postSteps.Count; pIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var post = postSteps[pIdx];
            await RunOneInlineAsync(
                post,
                parentStepIndex,
                pIdx + 1,
                context,
                stepSink,
                environmentKey,
                siblings,
                ct);
        }
    }

    private async Task RunOneInlineAsync(
        VerificationStep postStep,
        int parentStepIndex,
        int postStepIndex,
        IReadOnlyDictionary<string, string> context,
        List<TestStep> stepSink,
        string? environmentKey,
        IReadOnlyList<ITestAgent> siblings,
        CancellationToken ct)
    {
        var waitAction = $"post-wait[{parentStepIndex}.{postStepIndex}]";
        if (postStep.WaitBeforeSeconds > 0)
        {
            stepSink.Add(TestStep.Pass(waitAction,
                $"Waiting {postStep.WaitBeforeSeconds}s before '{postStep.Description}'"));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(postStep.WaitBeforeSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                stepSink.Add(TestStep.Err(waitAction, "Wait cancelled"));
                return;
            }
        }

        var action = $"post[{parentStepIndex}.{postStepIndex}]";

        if (!Enum.TryParse<TestTargetType>(postStep.Target, ignoreCase: true, out var target))
        {
            stepSink.Add(TestStep.Fail(action,
                $"Post-step Target '{postStep.Target}' is not a known TestTargetType."));
            return;
        }

        var syntheticTask = new TestTask
        {
            Description = postStep.Description,
            Target = target,
            Parameters = new Dictionary<string, object>(),
        };
        if (!string.IsNullOrWhiteSpace(environmentKey))
            syntheticTask.Parameters["EnvironmentKey"] = environmentKey!;

        var payloadOk = TryPreloadPayload(postStep, target, context, syntheticTask, stepSink, action);
        if (!payloadOk) return;

        ITestAgent? sibling = null;
        foreach (var candidate in siblings)
        {
            if (await candidate.CanHandleAsync(syntheticTask)) { sibling = candidate; break; }
        }
        if (sibling is null)
        {
            stepSink.Add(TestStep.Err(action,
                $"No registered agent can handle post-step target '{postStep.Target}'. " +
                "Check DI registrations and capability matching."));
            return;
        }

        _logger.LogInformation(
            "Running post-step {Action} '{Desc}' via {Sibling}",
            action, postStep.Description, sibling.Name);

        TestResult childResult;
        try
        {
            childResult = await sibling.ExecuteAsync(syntheticTask, ct);
        }
        catch (Exception ex)
        {
            stepSink.Add(TestStep.Err(action,
                $"Post-step agent '{sibling.Name}' threw: {ex.Message}"));
            return;
        }

        foreach (var childStep in childResult.Steps)
        {
            stepSink.Add(new TestStep
            {
                Action = $"{action} {childStep.Action}",
                Summary = childStep.Summary,
                Status = childStep.Status,
                Detail = childStep.Detail,
                Duration = childStep.Duration,
            });
        }

        if (childResult.Status is TestStatus.Failed or TestStatus.Error
            && childResult.Steps.Count == 0)
        {
            stepSink.Add(new TestStep
            {
                Action = action,
                Summary = childResult.Summary,
                Status = childResult.Status,
            });
        }
    }

    // ─────────────────────────────────────────────────────
    // Deferred path
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether the post-step list should defer to the queue (based on
    /// <see cref="AseXmlConfig.DeferVerifications"/> + the defer threshold)
    /// AND whether the environment has the queue + pending repositories wired.
    /// Used by <see cref="Base.BaseTestAgent.RunPostStepsAsync"/> so any agent
    /// can opt into deferral without duplicating the check.
    ///
    /// The config knob lives under <c>AseXml.DeferVerifications</c> for historical
    /// reasons; since Slice 2 it governs deferral for ALL parent kinds, not
    /// just aseXML delivery. A future tidy rename is deferred work itself.
    /// </summary>
    public bool ShouldDefer(IReadOnlyList<VerificationStep> postSteps)
    {
        if (postSteps.Count == 0) return false;
        if (!_config.AseXml.DeferVerifications) return false;
        if (!postSteps.Any(p => p.WaitBeforeSeconds > _config.AseXml.VerificationDeferThresholdSeconds))
            return false;
        return _services.GetService<IRunQueueRepository>() is not null
               && _services.GetService<IPendingVerificationRepository>() is not null;
    }

    /// <summary>
    /// Enqueues a single deferred queue entry covering all post-steps for this
    /// parent case, plus a row in <c>run_pending_verifications</c>. Emits an
    /// AwaitingVerification synthetic step per post-step onto <paramref name="stepSink"/>
    /// so the parent run shows due-time information in the UI.
    ///
    /// Returns false (and emits no steps) if enqueueing fails — the caller
    /// should then fall back to inline execution so a transient queue-repo
    /// error doesn't lose the post-step.
    ///
    /// <paramref name="parentKind"/> is one of the <c>TestObjective.EnumerateAllPostSteps</c>
    /// parent-kind strings (<c>"WebUi"</c>, <c>"DesktopUi"</c>, <c>"Api"</c>,
    /// <c>"AseXml"</c>, <c>"AseXmlDeliver"</c>). Stored in the payload for
    /// diagnostics / future routing.
    /// </summary>
    public async Task<bool> TryEnqueueDeferredAsync(
        IReadOnlyList<VerificationStep> postSteps,
        IReadOnlyDictionary<string, string> context,
        int parentStepIndex,
        List<TestStep> stepSink,
        string? environmentKey,
        string parentKind,
        string parentRunId,
        string moduleId,
        string testSetId,
        string parentObjectiveId,
        string parentObjectiveName,
        CancellationToken ct)
    {
        if (postSteps.Count == 0) return true;
        try
        {
            var queueRepo = _services.GetRequiredService<IRunQueueRepository>();
            var pendingRepo = _services.GetRequiredService<IPendingVerificationRepository>();

            if (string.IsNullOrEmpty(parentRunId) || string.IsNullOrEmpty(testSetId))
            {
                _logger.LogWarning(
                    "Cannot defer post-steps for '{Objective}': missing RunId or TestSetId. Running inline.",
                    parentObjectiveName);
                return false;
            }

            var now = DateTime.UtcNow;
            var minWait = postSteps.Min(p => p.WaitBeforeSeconds);
            var maxWait = postSteps.Max(p => p.WaitBeforeSeconds);
            var fraction = Math.Clamp(_config.AseXml.VerificationEarlyStartFraction, 0.01, 1.0);
            var firstDue = now.AddSeconds(minWait * fraction);
            var deadline = now.AddSeconds(maxWait + _config.AseXml.VerificationGraceSeconds);

            var pendingId = Guid.NewGuid().ToString("N")[..12];

            var request = new DeferredVerificationRequest
            {
                PendingId = pendingId,
                ParentRunId = parentRunId,
                ModuleId = moduleId,
                TestSetId = testSetId,
                DeliveryObjectiveId = parentObjectiveId,
                DeliveryObjectiveName = parentObjectiveName,
                EnvironmentKey = environmentKey,
                DeliveryCompletedAt = now,
                DeadlineAt = deadline,
                AttemptCount = 0,
                ParentKind = parentKind,
                DeliveryContext = new Dictionary<string, string>(context, StringComparer.OrdinalIgnoreCase),
                Verifications = postSteps.ToList(),
            };

            var firstTarget = postSteps[0].Target;

            var queueEntry = new RunQueueEntry
            {
                Id = pendingId,
                ModuleId = moduleId,
                TestSetId = testSetId,
                ObjectiveId = parentObjectiveId,
                TargetType = firstTarget,
                Mode = "VerifyOnly",
                JobKind = "Run",
                Status = "Queued",
                RequestJson = JsonSerializer.Serialize(request, DeferredJsonOpts),
                NotBeforeAt = firstDue,
                DeadlineAt = deadline,
                AttemptCount = 0,
                ParentRunId = parentRunId,
            };
            await queueRepo.EnqueueAsync(queueEntry);

            await pendingRepo.InsertAsync(new PendingVerification
            {
                PendingId = pendingId,
                ParentRunId = parentRunId,
                CurrentQueueEntryId = queueEntry.Id,
                ModuleId = moduleId,
                TestSetId = testSetId,
                DeliveryObjectiveId = parentObjectiveId,
                FirstDueAt = firstDue,
                DeadlineAt = deadline,
                AttemptCount = 0,
                Status = "Pending",
                AttemptLogJson = "[]",
            });

            var firstDueLocal = firstDue.ToLocalTime();
            var deadlineLocal = deadline.ToLocalTime();
            for (var pIdx = 0; pIdx < postSteps.Count; pIdx++)
            {
                var p = postSteps[pIdx];
                stepSink.Add(new TestStep
                {
                    Action = $"post[{parentStepIndex}.{pIdx + 1}] scheduled",
                    Summary = $"'{p.Description}' — first attempt at {firstDueLocal:HH:mm:ss}, deadline {deadlineLocal:HH:mm:ss} ({p.Target})",
                    Status = TestStatus.AwaitingVerification,
                    Detail = $"pendingId={pendingId}\nparentKind={parentKind}\nwaitSeconds={p.WaitBeforeSeconds}\nfirstDueAtUtc={firstDue:O}\ndeadlineAtUtc={deadline:O}",
                });
            }

            _logger.LogInformation(
                "Deferred {Count} post-step(s) for parent '{ParentKind}/{Objective}' — pendingId={Pid}, firstDue={Due}, deadline={Dl}",
                postSteps.Count, parentKind, parentObjectiveName, pendingId, firstDue, deadline);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enqueue deferred post-steps for parent '{Objective}'; falling back to inline.",
                parentObjectiveName);
            return false;
        }
    }

    /// <summary>
    /// Handles one claim attempt of a deferred post-step queue entry. Uses the
    /// snapshot in the request — does NOT query execution history — so context
    /// is isolated from concurrent parent runs. On success, marks the pending
    /// row Completed and finalises the parent run. On failure before the
    /// deadline, re-enqueues a retry. On deadline-exceeded failure, marks
    /// Failed and finalises.
    ///
    /// Called from <c>JobExecutor</c> when it detects a <c>"DeferredVerification"</c>
    /// queue entry — bypassing the orchestrator → agent roundtrip because the
    /// replay is pure post-step work (the parent's own agent is not involved).
    /// </summary>
    public async Task<DeferredAttemptOutcome> RunDeferredAttemptAsync(
        DeferredVerificationRequest dr,
        CancellationToken ct)
    {
        var attemptStart = DateTime.UtcNow;
        var steps = new List<TestStep>();
        steps.Add(TestStep.Pass("deferred-post-steps",
            $"Attempt {dr.AttemptCount + 1} for '{dr.DeliveryObjectiveName}' " +
            $"(deadline {dr.DeadlineAt:HH:mm:ss}, started at {dr.DeliveryCompletedAt:HH:mm:ss})"));

        var pendingRepo = _services.GetRequiredService<IPendingVerificationRepository>();
        var queueRepo = _services.GetRequiredService<IRunQueueRepository>();
        var historyRepo = _services.GetRequiredService<IExecutionHistoryRepository>();

        var context = new Dictionary<string, string>(dr.DeliveryContext, StringComparer.OrdinalIgnoreCase);

        // First wait was consumed by the queue's not_before_at; later post-steps
        // wait their incremental delta relative to the first post-step's wait.
        var firstWait = dr.Verifications.Count > 0 ? dr.Verifications[0].WaitBeforeSeconds : 0;

        var attemptSteps = new List<TestStep>();
        var allPassed = true;
        var siblings = ResolveSiblings(null);

        for (var pIdx = 0; pIdx < dr.Verifications.Count; pIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var v = dr.Verifications[pIdx];
            var effective = new VerificationStep
            {
                Description = v.Description,
                Target = v.Target,
                Role = v.Role,
                WaitBeforeSeconds = pIdx == 0 ? 0 : Math.Max(0, v.WaitBeforeSeconds - firstWait),
                WebUi = v.WebUi,
                DesktopUi = v.DesktopUi,
                Api = v.Api,
                AseXml = v.AseXml,
                AseXmlDeliver = v.AseXmlDeliver,
                DbCheck = v.DbCheck,
            };

            var preCount = attemptSteps.Count;
            await RunOneInlineAsync(
                effective, parentStepIndex: 1, postStepIndex: pIdx + 1,
                context, attemptSteps, dr.EnvironmentKey, siblings, ct);

            for (var i = preCount; i < attemptSteps.Count; i++)
            {
                if (attemptSteps[i].Status is not TestStatus.Passed)
                {
                    allPassed = false;
                    break;
                }
            }
        }

        var attemptLog = await DeserializeAttemptLogAsync(dr.PendingId, pendingRepo);
        attemptLog.Add(new Dictionary<string, object?>
        {
            ["attempt"] = dr.AttemptCount + 1,
            ["at"] = attemptStart.ToString("O"),
            ["durationMs"] = (long)(DateTime.UtcNow - attemptStart).TotalMilliseconds,
            ["passed"] = allPassed,
        });
        var attemptLogJson = JsonSerializer.Serialize(attemptLog, DeferredJsonOpts);

        if (allPassed)
        {
            steps.AddRange(attemptSteps);
            steps.Add(TestStep.Pass("deferred-post-steps-result",
                $"Passed on attempt {dr.AttemptCount + 1} of {dr.Verifications.Count} post-step(s)."));

            var objResultJson = BuildObjectiveResultJson(dr, attemptSteps, TestStatus.Passed);
            await pendingRepo.MarkCompletedAsync(dr.PendingId, objResultJson, attemptLogJson);
            await TryFinaliseParentRunAsync(historyRepo, pendingRepo, dr);

            return new DeferredAttemptOutcome(
                Status: TestStatus.Passed,
                Summary: $"Deferred post-step(s) passed for '{dr.DeliveryObjectiveName}'.",
                Steps: steps);
        }

        var now = DateTime.UtcNow;
        if (now < dr.DeadlineAt)
        {
            var retryInterval = Math.Max(5, _config.AseXml.VerificationRetryIntervalSeconds);
            var nextDue = now.AddSeconds(retryInterval);
            if (nextDue > dr.DeadlineAt) nextDue = dr.DeadlineAt;

            var retryRequest = new DeferredVerificationRequest
            {
                PendingId = dr.PendingId,
                ParentRunId = dr.ParentRunId,
                ModuleId = dr.ModuleId,
                TestSetId = dr.TestSetId,
                DeliveryObjectiveId = dr.DeliveryObjectiveId,
                DeliveryObjectiveName = dr.DeliveryObjectiveName,
                EnvironmentKey = dr.EnvironmentKey,
                DeliveryCompletedAt = dr.DeliveryCompletedAt,
                DeadlineAt = dr.DeadlineAt,
                AttemptCount = dr.AttemptCount + 1,
                ParentKind = dr.ParentKind,
                DeliveryContext = dr.DeliveryContext,
                Verifications = dr.Verifications,
            };

            var retryEntry = new RunQueueEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                ModuleId = dr.ModuleId,
                TestSetId = dr.TestSetId,
                ObjectiveId = dr.DeliveryObjectiveId,
                TargetType = dr.Verifications[0].Target,
                Mode = "VerifyOnly",
                JobKind = "Run",
                Status = "Queued",
                RequestJson = JsonSerializer.Serialize(retryRequest, DeferredJsonOpts),
                NotBeforeAt = nextDue,
                DeadlineAt = dr.DeadlineAt,
                AttemptCount = dr.AttemptCount + 1,
                ParentQueueEntryId = dr.PendingId,
                ParentRunId = dr.ParentRunId,
            };
            await queueRepo.EnqueueAsync(retryEntry);
            await pendingRepo.UpdateAttemptAsync(dr.PendingId, retryEntry.Id, dr.AttemptCount + 1, attemptLogJson);

            steps.AddRange(attemptSteps);
            steps.Add(new TestStep
            {
                Action = "deferred-post-steps-retry",
                Summary = $"Attempt {dr.AttemptCount + 1} failed — retrying at {nextDue.ToLocalTime():HH:mm:ss} (deadline {dr.DeadlineAt.ToLocalTime():HH:mm:ss})",
                Status = TestStatus.AwaitingVerification,
                Detail = $"nextQueueEntryId={retryEntry.Id}\npendingId={dr.PendingId}\nfirstDueAtUtc={nextDue:O}\ndeadlineAtUtc={dr.DeadlineAt:O}",
            });

            return new DeferredAttemptOutcome(
                Status: TestStatus.AwaitingVerification,
                Summary: $"Retry queued for '{dr.DeliveryObjectiveName}' — next attempt at {nextDue:HH:mm:ss}.",
                Steps: steps);
        }

        steps.AddRange(attemptSteps);
        steps.Add(TestStep.Fail("deferred-post-steps-result",
            $"Deadline exceeded — {dr.AttemptCount + 1} attempts, final attempt failed."));

        var failedResultJson = BuildObjectiveResultJson(dr, attemptSteps, TestStatus.Failed);
        await pendingRepo.MarkFailedAsync(dr.PendingId, failedResultJson, attemptLogJson);
        await TryFinaliseParentRunAsync(historyRepo, pendingRepo, dr);

        return new DeferredAttemptOutcome(
            Status: TestStatus.Failed,
            Summary: $"Deferred post-step(s) failed for '{dr.DeliveryObjectiveName}' after {dr.AttemptCount + 1} attempt(s).",
            Steps: steps);
    }

    private static async Task<List<Dictionary<string, object?>>> DeserializeAttemptLogAsync(
        string pendingId, IPendingVerificationRepository pendingRepo)
    {
        var existing = await pendingRepo.GetByIdAsync(pendingId);
        if (existing is null || string.IsNullOrWhiteSpace(existing.AttemptLogJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                       existing.AttemptLogJson, DeferredJsonOpts)
                   ?? new();
        }
        catch { return new(); }
    }

    private static string BuildObjectiveResultJson(
        DeferredVerificationRequest dr, List<TestStep> attemptSteps, TestStatus status)
    {
        var payload = new
        {
            objectiveId = dr.DeliveryObjectiveId,
            objectiveName = dr.DeliveryObjectiveName,
            status = status.ToString(),
            passedSteps = attemptSteps.Count(s => s.Status == TestStatus.Passed),
            failedSteps = attemptSteps.Count(s => s.Status == TestStatus.Failed),
            totalSteps = attemptSteps.Count,
            steps = attemptSteps.Select(s => new
            {
                action = s.Action,
                summary = s.Summary,
                status = s.Status.ToString(),
                detail = s.Detail,
                duration = s.Duration,
                timestamp = s.Timestamp,
            }).ToList(),
        };
        return JsonSerializer.Serialize(payload, DeferredJsonOpts);
    }

    /// <summary>
    /// When every pending row for a run is terminal, asks the execution-history
    /// repo to merge the collected deferred results into the parent run and
    /// recompute its aggregate status (AwaitingVerification → Passed/Failed).
    /// </summary>
    private async Task TryFinaliseParentRunAsync(
        IExecutionHistoryRepository historyRepo,
        IPendingVerificationRepository pendingRepo,
        DeferredVerificationRequest dr)
    {
        try
        {
            var stillPending = await pendingRepo.CountPendingForRunAsync(dr.ParentRunId);
            if (stillPending > 0)
            {
                _logger.LogInformation(
                    "Pending row marked terminal for run {RunId}, {Remaining} still pending.",
                    dr.ParentRunId, stillPending);
                return;
            }

            var allForRun = await pendingRepo.ListForRunAsync(dr.ParentRunId);
            var run = await historyRepo.GetRunAsync(dr.TestSetId, dr.ParentRunId);
            if (run is null)
            {
                _logger.LogWarning("Cannot finalise run {RunId} — no history record found.", dr.ParentRunId);
                return;
            }

            foreach (var pending in allForRun)
            {
                if (string.IsNullOrWhiteSpace(pending.ResultJson)) continue;
                ApplyDeferredResultToRun(run, pending);
            }

            var hasFailed = run.ObjectiveResults.Any(o =>
                string.Equals(o.Status, "Failed", StringComparison.OrdinalIgnoreCase));
            var hasError = run.ObjectiveResults.Any(o =>
                string.Equals(o.Status, "Error", StringComparison.OrdinalIgnoreCase));
            var hasAwaiting = run.ObjectiveResults.Any(o =>
                string.Equals(o.Status, "AwaitingVerification", StringComparison.OrdinalIgnoreCase));

            run.Status = hasAwaiting ? "AwaitingVerification"
                       : hasError ? "Error"
                       : hasFailed ? "Failed"
                       : "Passed";
            run.PassedObjectives = run.ObjectiveResults.Count(o => o.Status == "Passed");
            run.FailedObjectives = run.ObjectiveResults.Count(o => o.Status == "Failed");
            run.ErrorObjectives = run.ObjectiveResults.Count(o => o.Status == "Error");
            run.CompletedAt = DateTime.UtcNow;

            var verifCount = allForRun.Count;
            run.Summary = run.Status switch
            {
                "Passed" => $"Parent + {verifCount} deferred post-step(s) completed successfully.",
                "Failed" => $"Parent completed but {run.FailedObjectives} objective(s) failed after deferred post-steps.",
                "Error"  => $"Parent completed but {run.ErrorObjectives} objective(s) errored during deferred post-steps.",
                _        => run.Summary,
            };

            await historyRepo.SaveAsync(run);
            _logger.LogInformation(
                "Finalised run {RunId} → {Status} after deferred post-steps.",
                dr.ParentRunId, run.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalise parent run {RunId} after deferred post-steps.", dr.ParentRunId);
        }
    }

    /// <summary>
    /// Overlays a pending verification's stored result onto the matching
    /// objective in <paramref name="run"/>. Objective is located by id; its
    /// steps that are still AwaitingVerification are replaced with the final
    /// attempt's steps plus a rollup summarising the attempt count. Status is
    /// promoted from AwaitingVerification to the pending row's terminal status.
    /// </summary>
    private static void ApplyDeferredResultToRun(PersistedExecutionRun run, PendingVerification pending)
    {
        var obj = run.ObjectiveResults
            .FirstOrDefault(o => string.Equals(o.ObjectiveId, pending.DeliveryObjectiveId, StringComparison.OrdinalIgnoreCase));
        if (obj is null || string.IsNullOrWhiteSpace(pending.ResultJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(pending.ResultJson);
            var root = doc.RootElement;

            obj.Steps.RemoveAll(s => string.Equals(s.Status, "AwaitingVerification", StringComparison.OrdinalIgnoreCase));

            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var stepEl in stepsEl.EnumerateArray())
                {
                    obj.Steps.Add(new PersistedStepResult
                    {
                        Action = stepEl.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                        Summary = stepEl.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                        Status = stepEl.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                        Detail = stepEl.TryGetProperty("detail", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() : null,
                        Duration = stepEl.TryGetProperty("duration", out var du)
                            && TimeSpan.TryParse(du.GetString(), out var parsedDur) ? parsedDur : TimeSpan.Zero,
                        Timestamp = stepEl.TryGetProperty("timestamp", out var ts) ? ts.GetDateTime() : DateTime.UtcNow,
                    });
                }
            }

            var rollupStatus = string.Equals(pending.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                ? "Passed" : pending.Status;
            obj.Steps.Add(new PersistedStepResult
            {
                Action = "post-step-rollup",
                Summary = $"{pending.AttemptCount + 1} attempt(s); final status {rollupStatus} (pending {pending.PendingId})",
                Status = rollupStatus,
                Timestamp = pending.CompletedAt ?? DateTime.UtcNow,
            });

            obj.Status = rollupStatus switch
            {
                "Passed" => "Passed",
                "Cancelled" => "Skipped",
                _ => "Failed",
            };
            obj.PassedSteps = obj.Steps.Count(s => string.Equals(s.Status, "Passed", StringComparison.OrdinalIgnoreCase));
            obj.FailedSteps = obj.Steps.Count(s => string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase));
            obj.TotalSteps = obj.Steps.Count;
            obj.CompletedAt = pending.CompletedAt ?? DateTime.UtcNow;
        }
        catch
        {
            // Leave the objective untouched if the payload is malformed.
        }
    }

    // ─────────────────────────────────────────────────────
    // Payload preload (inline dispatch only)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Fills <see cref="TestTask.Parameters"/>["PreloadedTestCases"] on
    /// <paramref name="syntheticTask"/> with a single substituted test case
    /// matching the post-step's target. Returns false and emits a Fail step
    /// when the payload carrier is null or the target isn't wired yet.
    /// </summary>
    private static bool TryPreloadPayload(
        VerificationStep postStep,
        TestTargetType target,
        IReadOnlyDictionary<string, string> context,
        TestTask syntheticTask,
        List<TestStep> stepSink,
        string action)
    {
        switch (target)
        {
            case TestTargetType.UI_Web_MVC:
            case TestTargetType.UI_Web_Blazor:
                if (postStep.WebUi is null)
                {
                    stepSink.Add(TestStep.Fail(action,
                        $"Target is '{postStep.Target}' but WebUi payload is missing on the post-step."));
                    return false;
                }
                {
                    var clone = StepParameterSubstituter.Apply(postStep.WebUi, context);
                    syntheticTask.Parameters["PreloadedTestCases"] =
                        new List<WebUiTestCase> { clone.ToTestCase(postStep.Description) };
                    return true;
                }

            case TestTargetType.UI_Desktop_WinForms:
                if (postStep.DesktopUi is null)
                {
                    stepSink.Add(TestStep.Fail(action,
                        $"Target is '{postStep.Target}' but DesktopUi payload is missing on the post-step."));
                    return false;
                }
                {
                    var clone = StepParameterSubstituter.Apply(postStep.DesktopUi, context);
                    syntheticTask.Parameters["PreloadedTestCases"] =
                        new List<DesktopUiTestCase> { clone.ToTestCase(postStep.Description) };
                    return true;
                }

            case TestTargetType.API_REST:
                if (postStep.Api is null)
                {
                    stepSink.Add(TestStep.Fail(action,
                        $"Target is '{postStep.Target}' but Api payload is missing on the post-step."));
                    return false;
                }
                {
                    var clone = StepParameterSubstituter.Apply(postStep.Api, context);
                    syntheticTask.Parameters["PreloadedTestCases"] =
                        new List<AiTestCrew.Agents.ApiAgent.ApiTestCase> { clone.ToTestCase(postStep.Description) };
                    return true;
                }

            case TestTargetType.AseXml_Generate:
                if (postStep.AseXml is null)
                {
                    stepSink.Add(TestStep.Fail(action,
                        $"Target is '{postStep.Target}' but AseXml payload is missing on the post-step."));
                    return false;
                }
                {
                    var clone = StepParameterSubstituter.Apply(postStep.AseXml, context);
                    syntheticTask.Parameters["PreloadedTestCases"] =
                        new List<AseXmlTestCase> { clone.ToTestCase(postStep.Description) };
                    return true;
                }

            case TestTargetType.AseXml_Deliver:
                if (postStep.AseXmlDeliver is null)
                {
                    stepSink.Add(TestStep.Fail(action,
                        $"Target is '{postStep.Target}' but AseXmlDeliver payload is missing on the post-step."));
                    return false;
                }
                {
                    var clone = StepParameterSubstituter.Apply(postStep.AseXmlDeliver, context);
                    syntheticTask.Parameters["PreloadedTestCases"] =
                        new List<AseXmlDeliveryTestCase> { clone.ToTestCase(postStep.Description) };
                    return true;
                }

            case TestTargetType.Db_SqlServer:
                if (postStep.DbCheck is null)
                {
                    stepSink.Add(TestStep.Fail(action,
                        $"Target is '{postStep.Target}' but DbCheck payload is missing on the post-step."));
                    return false;
                }
                {
                    var clone = StepParameterSubstituter.Apply(postStep.DbCheck, context);
                    // DbCheckAgent consumes PreloadedTestCases in the same preloaded-list shape
                    // as the UI agents for consistency, even though each post-step only ships one.
                    syntheticTask.Parameters["PreloadedTestCases"] =
                        new List<AiTestCrew.Agents.DbAgent.DbCheckStepDefinition> { clone };
                    return true;
                }

            default:
                stepSink.Add(TestStep.Fail(action,
                    $"Post-step target '{postStep.Target}' is not supported."));
                return false;
        }
    }

    /// <summary>
    /// Resolves registered agents from DI. We include the calling agent in the
    /// candidate list because same-target post-steps are legitimate (e.g. a
    /// WinForms parent case with a WinForms verification post-step). Each
    /// post-step dispatch builds a NEW synthetic task with its own
    /// <c>PreloadedTestCases</c> so the agent runs the sub-case exactly like a
    /// normal reuse invocation — no recursion risk at the orchestrator level.
    ///
    /// Resolved lazily every call because the calling agent is itself registered
    /// as <see cref="ITestAgent"/>, so constructor-injecting the enumerable
    /// would recurse during DI graph construction.
    ///
    /// The <paramref name="callingAgent"/> parameter is kept for signature
    /// compatibility but no longer used for filtering.
    /// </summary>
    private IReadOnlyList<ITestAgent> ResolveSiblings(ITestAgent? callingAgent)
    {
        _ = callingAgent;
        return _services.GetServices<ITestAgent>().ToList();
    }
}

/// <summary>
/// Result of one deferred queue-claim attempt. Steps are the attempt's full
/// step list (including retry-scheduling markers); Status is Passed, Failed,
/// or AwaitingVerification (the last means a retry has been re-enqueued).
/// </summary>
public sealed record DeferredAttemptOutcome(TestStatus Status, string Summary, IReadOnlyList<TestStep> Steps);
