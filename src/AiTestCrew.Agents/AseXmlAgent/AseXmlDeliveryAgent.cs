using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.AseXmlAgent.Templates;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Utilities;

namespace AiTestCrew.Agents.AseXmlAgent;

/// <summary>
/// Renders an aseXML payload and uploads it to a Bravo inbound drop location
/// resolved from <c>mil.V2_MIL_EndPoint</c> by <c>EndPointCode</c>.
///
/// Self-contained: does not depend on a preceding Generate task. On reuse,
/// the rendered payload is produced fresh (new MessageID / TransactionID /
/// timestamps) and uploaded to the same endpoint.
///
/// Phase 3: after a successful upload, the agent optionally runs UI verification
/// steps (Legacy MVC, Blazor, or WinForms) attached to the delivery test case.
/// Values from the render (NMI, MessageID, TransactionID, filename, any template
/// field) are injected into every UI step field via <c>{{Token}}</c> substitution
/// at playback. Siblings are discovered via <see cref="CanHandleAsync"/> dispatch.
/// </summary>
public class AseXmlDeliveryAgent : BaseTestAgent
{
    private readonly TestEnvironmentConfig _config;
    private readonly TemplateRegistry _templates;
    private readonly IEndpointResolver _endpoints;
    private readonly DropTargetFactory _dropFactory;
    private readonly IServiceProvider _services;
    // Lazily-materialised sibling agent list. We can't take IEnumerable<ITestAgent>
    // in the constructor because this agent is itself registered as ITestAgent —
    // that would cause DI to recurse when resolving the enumerable. Instead we
    // keep the service provider and resolve siblings (filtering self) on first use.
    private IReadOnlyList<ITestAgent>? _siblingsCache;

    public override string Name => "aseXML Delivery Agent";
    public override string Role => "Senior AEMO B2B Test Engineer";

    public AseXmlDeliveryAgent(
        Kernel kernel,
        ILogger<AseXmlDeliveryAgent> logger,
        TestEnvironmentConfig config,
        TemplateRegistry templates,
        IEndpointResolver endpoints,
        DropTargetFactory dropFactory,
        IServiceProvider services) : base(kernel, logger)
    {
        _config = config;
        _templates = templates;
        _endpoints = endpoints;
        _dropFactory = dropFactory;
        _services = services;
    }

    private IReadOnlyList<ITestAgent> Siblings =>
        _siblingsCache ??= _services.GetServices<ITestAgent>()
            .Where(a => a is not AseXmlDeliveryAgent)
            .ToList();

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.AseXml_Deliver);

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();
        var deliveries = new List<Dictionary<string, object?>>();

        _currentTask = task;
        _currentTaskId = task.Id;

        task.Parameters.TryGetValue("EnvironmentKey", out var rawEnvKey);
        var envKey = rawEnvKey as string;
        var envParams = Environment.StepParameterSubstituter.ReadEnvironmentParameters(task.Parameters);

        Logger.LogInformation("[{Agent}] Starting task: {Desc} (env: {Env})",
            Name, task.Description, envKey ?? "default");

        try
        {
            // ── Load test cases ────────────────────────────────────────────
            List<AseXmlDeliveryTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<AseXmlDeliveryTestCase> saved)
            {
                testCases = envParams.Count > 0
                    ? saved.Select(tc => Environment.StepParameterSubstituter.Apply(tc, envParams)).ToList()
                    : saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved delivery case(s) (reuse mode — skipping LLM generation)"));
            }

            if (testCases is null)
            {
                if (_templates.All().Count == 0)
                {
                    steps.Add(TestStep.Err("catalogue",
                        $"No aseXML templates loaded. Place template+manifest pairs under '{_templates.TemplatesPath}'."));
                    return BuildResult(task, steps, TestStatus.Error,
                        "No aseXML templates available.", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
                }

                testCases = await GenerateTestCasesAsync(task, ct);
                if (testCases is null || testCases.Count == 0)
                {
                    steps.Add(TestStep.Err("generate-cases",
                        "LLM did not return any aseXML delivery cases for this objective."));
                    return BuildResult(task, steps, TestStatus.Error,
                        "LLM failed to produce delivery test cases.", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
                }

                steps.Add(TestStep.Pass("generate-cases",
                    $"LLM produced {testCases.Count} delivery case(s)"));
            }

            // ── Verify-only mode: skip render/upload, re-run verifications only ──
            var verifyOnly = task.Parameters.TryGetValue("VerifyOnly", out var vo) && vo is true;
            if (verifyOnly)
                return await VerifyOnlyAsync(task, testCases!, steps, sw, ct);

            // ── Apply CLI / test-set-level endpoint override ──────────────
            var endpointOverride = task.Parameters.TryGetValue("EndpointCode", out var ec)
                ? ec as string : null;
            if (!string.IsNullOrWhiteSpace(endpointOverride))
            {
                foreach (var tc in testCases)
                {
                    if (string.IsNullOrWhiteSpace(tc.EndpointCode))
                        tc.EndpointCode = endpointOverride!;
                }
            }

            // ── Resolve run output dir (for local debug copies) ───────────
            var runOutputDir = ResolveRunOutputDir(task.Id);
            Directory.CreateDirectory(runOutputDir);

            // ── For each case: render → resolve → [package] → upload ──────
            var caseIndex = 0;
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                caseIndex++;
                await DeliverOneAsync(tc, caseIndex, runOutputDir, steps, deliveries, envKey, ct);
            }

            var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
            var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
            var hasAwaiting = steps.Any(s => s.Status == TestStatus.AwaitingVerification);
            var status = hasErrors ? TestStatus.Error
                       : hasFails ? TestStatus.Failed
                       : hasAwaiting ? TestStatus.AwaitingVerification
                       : TestStatus.Passed;

            var summary = status switch
            {
                TestStatus.Passed => $"Delivered {testCases.Count} aseXML payload(s). Debug copies at {runOutputDir}.",
                TestStatus.AwaitingVerification => $"Delivered {testCases.Count} aseXML payload(s). Verification(s) queued — run will finalise when they complete.",
                _ => $"Delivery attempted for {testCases.Count} case(s) with issues; see step detail.",
            };

            var definitions = testCases.Select(AseXmlDeliveryTestDefinition.FromTestCase).ToList();
            return BuildResult(task, steps, status, summary, sw, definitions, deliveries, runOutputDir);
        }
        catch (OperationCanceledException)
        {
            return BuildResult(task, steps, TestStatus.Error,
                "Test execution was cancelled", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Unhandled error", Name);
            steps.Add(TestStep.Err("fatal", $"Unhandled exception: {ex.Message}"));
            return BuildResult(task, steps, TestStatus.Error,
                $"Agent error: {ex.Message}", sw, new List<AseXmlDeliveryTestDefinition>(), deliveries);
        }
    }

    // ─────────────────────────────────────────────────────
    // Verify-only mode — skip render/upload, replay verifications
    // ─────────────────────────────────────────────────────

    private async Task<TestResult> VerifyOnlyAsync(
        TestTask task,
        List<AseXmlDeliveryTestCase> testCases,
        List<TestStep> steps,
        Stopwatch sw,
        CancellationToken ct)
    {
        var deliveries = new List<Dictionary<string, object?>>();

        // ── Deferred path: we were claimed from a run_queue entry carrying a
        //    self-contained DeliveryContext snapshot. Use it directly — do NOT
        //    query history (that races with concurrent deliveries for the same
        //    test set and is the reason this feature exists).
        if (task.Parameters.TryGetValue("DeferredVerificationRequest", out var drObj)
            && drObj is DeferredVerificationRequest dr)
        {
            return await DeferredVerifyAsync(task, dr, steps, sw, ct);
        }

        var historyRepo = _services.GetRequiredService<IExecutionHistoryRepository>();
        var testSetId = task.Parameters.TryGetValue("TestSetId", out var tsId) ? tsId as string : null;
        var moduleId = task.Parameters.TryGetValue("ModuleId", out var mId) ? mId as string : null;
        var envKey = task.Parameters.TryGetValue("EnvironmentKey", out var ek) ? ek as string : null;
        int? waitOverride = task.Parameters.TryGetValue("VerificationWaitOverride", out var wo) && wo is int w ? w : (int?)null;

        if (string.IsNullOrEmpty(testSetId))
        {
            steps.Add(TestStep.Err("verify-only",
                "VerifyOnly mode requires a testSetId in task parameters."));
            return BuildResult(task, steps, TestStatus.Error,
                "Missing testSetId for verify-only.", sw, [], deliveries);
        }

        var historyCtx = await historyRepo.GetLatestDeliveryContextAsync(testSetId, moduleId, task.Id);
        if (historyCtx is null)
        {
            steps.Add(TestStep.Err("verify-only",
                $"No prior successful delivery found for objective '{task.Id}'. " +
                "Run the full delivery at least once before using --verify-only."));
            return BuildResult(task, steps, TestStatus.Error,
                "No delivery history available for verify-only mode.", sw, [], deliveries);
        }

        steps.Add(TestStep.Pass("verify-only-context",
            $"Reconstructed delivery context from history — MessageID={historyCtx.GetValueOrDefault("MessageID")}, " +
            $"EndpointCode={historyCtx.GetValueOrDefault("EndpointCode")}"));

        var totalVerifications = 0;

        for (var caseIndex = 0; caseIndex < testCases.Count; caseIndex++)
        {
            var tc = testCases[caseIndex];
            ct.ThrowIfCancellationRequested();

            if (tc.PostDeliveryVerifications.Count == 0)
            {
                steps.Add(TestStep.Pass($"verify-only[{caseIndex + 1}]",
                    "No post-delivery verifications defined — nothing to re-run."));
                continue;
            }

            // Build full verification context: user field values + delivery history
            var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in tc.FieldValues)
                if (!string.IsNullOrEmpty(v)) context[k] = v;
            foreach (var (k, v) in historyCtx)
                if (!string.IsNullOrEmpty(v)) context[k] = v;

            var remoteFileName = historyCtx.GetValueOrDefault("Filename") ?? "";

            for (var vIdx = 0; vIdx < tc.PostDeliveryVerifications.Count; vIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var verification = tc.PostDeliveryVerifications[vIdx];
                totalVerifications++;

                // Apply wait override if specified (e.g. --wait 0 to skip delays)
                var originalWait = verification.WaitBeforeSeconds;
                if (waitOverride.HasValue)
                    verification.WaitBeforeSeconds = waitOverride.Value;

                try
                {
                    await RunVerificationAsync(
                        verification,
                        caseIndex + 1, vIdx + 1,
                        remoteFileName,
                        context,
                        steps,
                        envKey,
                        ct);
                }
                finally
                {
                    // Restore original wait (test case may be reused in-process)
                    verification.WaitBeforeSeconds = originalWait;
                }
            }
        }

        var hasFails = steps.Any(s => s.Status == TestStatus.Failed);
        var hasErrors = steps.Any(s => s.Status == TestStatus.Error);
        var status = hasErrors ? TestStatus.Error
                   : hasFails ? TestStatus.Failed
                   : TestStatus.Passed;

        var summary = status == TestStatus.Passed
            ? $"Verify-only: all {totalVerifications} verification(s) passed."
            : $"Verify-only: some verifications failed; see step detail.";

        var definitions = testCases.Select(AseXmlDeliveryTestDefinition.FromTestCase).ToList();
        return BuildResult(task, steps, status, summary, sw, definitions, deliveries);
    }

    // ─────────────────────────────────────────────────────
    // Deferred verification handler
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Handles a claimed deferred-verification queue entry. Uses the snapshot from
    /// <see cref="DeferredVerificationRequest"/> so context is isolated from later
    /// deliveries against the same test set. On any failure before the deadline,
    /// re-enqueues a fresh attempt and returns — the claim's agent slot is freed.
    /// On success or deadline-exceeded failure, marks the pending row terminal and
    /// asks the execution-history repo to finalise the parent run.
    /// </summary>
    private async Task<TestResult> DeferredVerifyAsync(
        TestTask task,
        DeferredVerificationRequest dr,
        List<TestStep> steps,
        Stopwatch sw,
        CancellationToken ct)
    {
        var attemptStart = DateTime.UtcNow;
        steps.Add(TestStep.Pass("deferred-verify",
            $"Attempt {dr.AttemptCount + 1} for '{dr.DeliveryObjectiveName}' " +
            $"(deadline {dr.DeadlineAt:HH:mm:ss}, delivered at {dr.DeliveryCompletedAt:HH:mm:ss})"));

        var pendingRepo = _services.GetRequiredService<IPendingVerificationRepository>();
        var queueRepo = _services.GetRequiredService<IRunQueueRepository>();
        var historyRepo = _services.GetRequiredService<IExecutionHistoryRepository>();

        var context = new Dictionary<string, string>(dr.DeliveryContext, StringComparer.OrdinalIgnoreCase);
        var remoteFileName = context.TryGetValue("Filename", out var fn) ? fn : "";

        // ── Run each verification. First wait is already consumed by not_before_at;
        //    later verifications use their delta relative to the FIRST verification's wait.
        var firstWait = dr.Verifications.Count > 0 ? dr.Verifications[0].WaitBeforeSeconds : 0;

        var attemptSteps = new List<TestStep>();
        var allPassed = true;
        for (var vIdx = 0; vIdx < dr.Verifications.Count; vIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var v = dr.Verifications[vIdx];
            var effective = new VerificationStep
            {
                Description = v.Description,
                Target = v.Target,
                WebUi = v.WebUi,
                DesktopUi = v.DesktopUi,
                // First verification's wait was honoured by the queue; subsequent ones
                // wait only their incremental delta. Negative/zero = no extra wait.
                WaitBeforeSeconds = vIdx == 0 ? 0 : Math.Max(0, v.WaitBeforeSeconds - firstWait),
            };

            var preCount = attemptSteps.Count;
            await RunVerificationAsync(
                effective,
                deliveryIndex: 1,
                verifyIndex: vIdx + 1,
                remoteFileName,
                context,
                attemptSteps,
                dr.EnvironmentKey,
                ct);

            // Treat anything other than Passed as failure for this attempt.
            for (var i = preCount; i < attemptSteps.Count; i++)
            {
                if (attemptSteps[i].Status is not TestStatus.Passed)
                {
                    allPassed = false;
                    break;
                }
            }
        }

        // ── Attempt outcome ──
        var attemptLogList = DeserializeAttemptLog(dr.PendingId, pendingRepo);
        var newLogEntry = new Dictionary<string, object?>
        {
            ["attempt"] = dr.AttemptCount + 1,
            ["at"] = attemptStart.ToString("O"),
            ["durationMs"] = (long)sw.Elapsed.TotalMilliseconds,
            ["passed"] = allPassed,
        };
        attemptLogList.Add(newLogEntry);
        var attemptLogJson = JsonSerializer.Serialize(attemptLogList, DeferredJsonOpts);

        if (allPassed)
        {
            steps.AddRange(attemptSteps);
            steps.Add(TestStep.Pass("deferred-verify-result",
                $"Passed on attempt {dr.AttemptCount + 1} of {dr.Verifications.Count} verification(s)."));

            var objResultJson = BuildObjectiveResultJson(dr, attemptSteps, TestStatus.Passed);
            await pendingRepo.MarkCompletedAsync(dr.PendingId, objResultJson, attemptLogJson);
            await TryFinaliseParentRunAsync(historyRepo, pendingRepo, dr);

            return BuildResult(task, steps, TestStatus.Passed,
                $"Deferred verification passed for '{dr.DeliveryObjectiveName}'.",
                sw, [], []);
        }

        // ── Failed this attempt ──
        var now = DateTime.UtcNow;
        if (now < dr.DeadlineAt)
        {
            // Re-enqueue a fresh attempt.
            var retryInterval = Math.Max(5, _config.AseXml.VerificationRetryIntervalSeconds);
            var nextDue = now.AddSeconds(retryInterval);
            // Don't overshoot the deadline — if retry interval pushes past deadline,
            // schedule the final attempt right at the deadline instead.
            if (nextDue > dr.DeadlineAt) nextDue = dr.DeadlineAt;

            var retryRequest = new DeferredVerificationRequest
            {
                ParentRunId = dr.ParentRunId,
                PendingId = dr.PendingId,
                ModuleId = dr.ModuleId,
                TestSetId = dr.TestSetId,
                DeliveryObjectiveId = dr.DeliveryObjectiveId,
                DeliveryObjectiveName = dr.DeliveryObjectiveName,
                EnvironmentKey = dr.EnvironmentKey,
                DeliveryCompletedAt = dr.DeliveryCompletedAt,
                DeadlineAt = dr.DeadlineAt,
                AttemptCount = dr.AttemptCount + 1,
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
                Action = "deferred-verify-retry",
                Summary = $"Attempt {dr.AttemptCount + 1} failed — retrying at {nextDue.ToLocalTime():HH:mm:ss} (deadline {dr.DeadlineAt.ToLocalTime():HH:mm:ss})",
                Status = TestStatus.AwaitingVerification,
                Detail = $"nextQueueEntryId={retryEntry.Id}\npendingId={dr.PendingId}\nfirstDueAtUtc={nextDue:O}\ndeadlineAtUtc={dr.DeadlineAt:O}",
            });

            return BuildResult(task, steps, TestStatus.AwaitingVerification,
                $"Retry queued for '{dr.DeliveryObjectiveName}' — next attempt at {nextDue:HH:mm:ss}.",
                sw, [], []);
        }

        // ── Deadline exceeded → final failure ──
        steps.AddRange(attemptSteps);
        steps.Add(TestStep.Fail("deferred-verify-result",
            $"Deadline exceeded — {dr.AttemptCount + 1} attempts, final attempt failed."));

        var failedResultJson = BuildObjectiveResultJson(dr, attemptSteps, TestStatus.Failed);
        await pendingRepo.MarkFailedAsync(dr.PendingId, failedResultJson, attemptLogJson);
        await TryFinaliseParentRunAsync(historyRepo, pendingRepo, dr);

        return BuildResult(task, steps, TestStatus.Failed,
            $"Deferred verification failed for '{dr.DeliveryObjectiveName}' after {dr.AttemptCount + 1} attempt(s).",
            sw, [], []);
    }

    private static List<Dictionary<string, object?>> DeserializeAttemptLog(
        string pendingId, IPendingVerificationRepository pendingRepo)
    {
        var existing = pendingRepo.GetByIdAsync(pendingId).GetAwaiter().GetResult();
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
    /// When every pending row for a run is terminal, asks the execution-history repo
    /// to merge the collected deferred results into the parent run and recompute its
    /// aggregate status (AwaitingVerification → Passed/Failed).
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
                Logger.LogInformation(
                    "Pending row marked terminal for run {RunId}, {Remaining} still pending.",
                    dr.ParentRunId, stillPending);
                return;
            }

            // All pending rows for this run are terminal — merge their results and finalise.
            var allForRun = await pendingRepo.ListForRunAsync(dr.ParentRunId);
            var run = await historyRepo.GetRunAsync(dr.TestSetId, dr.ParentRunId);
            if (run is null)
            {
                Logger.LogWarning("Cannot finalise run {RunId} — no history record found.", dr.ParentRunId);
                return;
            }

            foreach (var pending in allForRun)
            {
                if (string.IsNullOrWhiteSpace(pending.ResultJson)) continue;
                ApplyDeferredResultToRun(run, pending);
            }

            // Recompute run-level aggregate status
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

            // Replace the provisional "Inconclusive / awaiting verification" summary
            // generated while the run was mid-flight with a short factual summary of
            // the final outcome. The UI sometimes renders this alongside the status
            // pill; keeping the stale LLM text would confuse the user.
            var verifCount = allForRun.Count;
            run.Summary = run.Status switch
            {
                "Passed" => $"Delivery + {verifCount} deferred verification(s) completed successfully.",
                "Failed" => $"Delivery completed but {run.FailedObjectives} objective(s) failed after deferred verification.",
                "Error"  => $"Delivery completed but {run.ErrorObjectives} objective(s) errored during deferred verification.",
                _        => run.Summary,
            };

            await historyRepo.SaveAsync(run);
            Logger.LogInformation(
                "Finalised run {RunId} → {Status} after deferred verifications.",
                dr.ParentRunId, run.Status);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to finalise parent run {RunId} after deferred verification.", dr.ParentRunId);
        }
    }

    /// <summary>
    /// Overlays a pending verification's stored result onto the matching objective
    /// in <paramref name="run"/>. The objective is looked up by id; its steps are
    /// replaced with the final attempt's steps (plus a rollup), and its status is
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

            // Replace steps that are still AwaitingVerification with the attempt's steps
            // plus a single rollup step summarising the attempt count.
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
                Action = "verification-rollup",
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
            // If the payload is malformed, leave the objective untouched so we fail loudly
            // rather than silently overwriting with garbage.
        }
    }

    // ─────────────────────────────────────────────────────
    // Core delivery pipeline
    // ─────────────────────────────────────────────────────

    private async Task DeliverOneAsync(
        AseXmlDeliveryTestCase tc,
        int index,
        string runOutputDir,
        List<TestStep> steps,
        List<Dictionary<string, object?>> deliveries,
        string? environmentKey,
        CancellationToken ct)
    {
        // 1) Render
        var renderAction = $"render[{index}] {tc.TemplateId}";
        var template = _templates.Get(tc.TemplateId);
        if (template is null)
        {
            var known = string.Join(", ", _templates.All().Select(t => t.Manifest.TemplateId));
            steps.Add(TestStep.Fail(renderAction,
                $"Template '{tc.TemplateId}' not found. Known templates: {known}"));
            return;
        }

        string xml;
        Dictionary<string, string> resolvedFields;
        try
        {
            var result = AseXmlRenderer.Render(template.Manifest, template.Body, tc.FieldValues);
            xml = result.Xml;
            resolvedFields = result.ResolvedFields;
        }
        catch (AseXmlRenderException ex)
        {
            steps.Add(TestStep.Fail(renderAction, ex.Message));
            return;
        }

        var messageId = resolvedFields.TryGetValue("MessageID", out var mid) ? mid : "";
        var transactionId = resolvedFields.TryGetValue("TransactionID", out var tid) ? tid : "";
        var safeName = MakeFileSafe(string.IsNullOrWhiteSpace(tc.Name) ? tc.TemplateId : tc.Name);
        var xmlLocalPath = Path.Combine(runOutputDir, $"{index:00}-{safeName}.xml");
        File.WriteAllText(xmlLocalPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        steps.Add(new TestStep
        {
            Action = renderAction,
            Summary = $"Rendered '{tc.TemplateId}'"
                + (string.IsNullOrEmpty(messageId) ? "" : $" (MessageID {messageId})"),
            Status = TestStatus.Passed,
            Detail = BuildRenderDetail(tc, template.Manifest.TransactionType, xmlLocalPath, resolvedFields, xml),
        });

        // 2) Resolve endpoint
        var resolveAction = $"resolve-endpoint[{index}]";
        if (string.IsNullOrWhiteSpace(tc.EndpointCode))
        {
            steps.Add(TestStep.Fail(resolveAction,
                "No EndpointCode supplied — set it in the objective or pass --endpoint <code>."));
            return;
        }

        BravoEndpoint? endpoint;
        try
        {
            endpoint = await _endpoints.ResolveAsync(tc.EndpointCode, environmentKey, ct);
        }
        catch (InvalidOperationException ex)  // e.g. connection string not configured
        {
            steps.Add(TestStep.Err(resolveAction, ex.Message));
            return;
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(resolveAction,
                $"Failed to query mil.V2_MIL_EndPoint for '{tc.EndpointCode}': {ex.Message}"));
            return;
        }

        if (endpoint is null)
        {
            steps.Add(TestStep.Fail(resolveAction,
                $"EndPointCode '{tc.EndpointCode}' not found in mil.V2_MIL_EndPoint."));
            return;
        }

        steps.Add(TestStep.Pass(resolveAction,
            $"Endpoint '{endpoint.EndPointCode}' → host={endpoint.FtpServer}, outbox={endpoint.OutBoxUrl}, zipped={endpoint.IsOutboundFilesZipped}"));

        // 3) Determine remote file name + payload (zip if configured)
        var baseFileName = !string.IsNullOrEmpty(messageId) ? messageId : safeName;
        string remoteFileName;
        Stream uploadContent;
        long uploadBytes;
        string uploadedAs;

        if (endpoint.IsOutboundFilesZipped)
        {
            var packageAction = $"package[{index}]";
            MemoryStream zipStream;
            try
            {
                zipStream = XmlZipPackager.Package(xml, $"{baseFileName}.xml");
            }
            catch (Exception ex)
            {
                steps.Add(TestStep.Err(packageAction, $"Failed to build zip: {ex.Message}"));
                return;
            }

            var uncompressed = Encoding.UTF8.GetByteCount(xml);
            var compressed = zipStream.Length;
            var ratio = uncompressed == 0 ? 0 : (double)compressed / uncompressed;
            steps.Add(TestStep.Pass(packageAction,
                $"Packaged XML into zip — {compressed} bytes (uncompressed {uncompressed}, ratio {ratio:P0})"));

            // Write local zip copy for developer inspection.
            var zipLocalPath = Path.Combine(runOutputDir, $"{index:00}-{safeName}.zip");
            File.WriteAllBytes(zipLocalPath, zipStream.ToArray());

            zipStream.Position = 0;
            uploadContent = zipStream;
            uploadBytes = compressed;
            remoteFileName = $"{baseFileName}.zip";
            uploadedAs = "zip";
        }
        else
        {
            var xmlBytes = Encoding.UTF8.GetBytes(xml);
            uploadContent = new MemoryStream(xmlBytes);
            uploadBytes = xmlBytes.LongLength;
            remoteFileName = $"{baseFileName}.xml";
            uploadedAs = "xml";
        }

        // 4) Upload
        var uploadAction = $"upload[{index}]";
        DeliveryReceipt receipt;
        try
        {
            using (uploadContent)
            {
                var target = _dropFactory.Create(endpoint);
                var schemeNote = DropTargetFactory.DetectScheme(endpoint.OutBoxUrl, endpoint.FtpServer).ToUpperInvariant();

                using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                uploadCts.CancelAfter(TimeSpan.FromSeconds(_config.AseXml.DeliveryTimeoutSeconds));

                receipt = await target.UploadAsync(endpoint, remoteFileName, uploadContent, uploadCts.Token);

                steps.Add(TestStep.Pass(uploadAction,
                    $"Uploaded {uploadedAs} via {schemeNote} → {receipt.RemotePath} ({receipt.BytesWritten} bytes, {receipt.Duration.TotalMilliseconds:F0} ms)"));
            }
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Fail(uploadAction,
                $"Upload failed for {remoteFileName}: {ex.Message}"));
            return;
        }

        deliveries.Add(new Dictionary<string, object?>
        {
            ["messageId"] = messageId,
            ["transactionId"] = transactionId,
            ["endpointCode"] = endpoint.EndPointCode,
            ["remotePath"] = receipt.RemotePath,
            ["uploadedAs"] = uploadedAs,
            ["bytes"] = uploadBytes,
            ["status"] = "Delivered",
        });

        // 5) Post-delivery UI verifications (Phase 3)
        if (tc.PostDeliveryVerifications.Count > 0)
        {
            var context = BuildVerificationContext(
                messageId, transactionId, remoteFileName, uploadedAs,
                endpoint.EndPointCode, resolvedFields);

            // Decide whether to defer: enabled in config AND at least one verification
            // has a wait longer than the defer threshold. Short waits run inline because
            // queueing overhead isn't worth it.
            var canDefer =
                _config.AseXml.DeferVerifications
                && tc.PostDeliveryVerifications.Any(v => v.WaitBeforeSeconds > _config.AseXml.VerificationDeferThresholdSeconds);

            // The deferred path requires SQLite-backed run_queue + run_pending_verifications
            // tables. When storage is File (or repos aren't wired), we can't persist a deferred
            // job and must fall back to inline Task.Delay execution.
            var queueRepoAvailable = _services.GetService<IRunQueueRepository>() is not null
                                      && _services.GetService<IPendingVerificationRepository>() is not null;

            if (canDefer && queueRepoAvailable
                && TryEnqueueDeferredVerifications(
                        tc, index, remoteFileName, context, environmentKey, steps, out var enqueued))
            {
                // Deferred path — steps list already carries AwaitingVerification markers.
                // Do not run verifications inline; the agent slot is released here.
                return;
            }

            // ── Explain WHY we fell back to inline so the UX is diagnosable. Appears
            //    as a grey step alongside the wait message.
            if (_config.AseXml.DeferVerifications
                && tc.PostDeliveryVerifications.Any(v => v.WaitBeforeSeconds > _config.AseXml.VerificationDeferThresholdSeconds))
            {
                var reason = !queueRepoAvailable
                    ? "deferred verification requires TestEnvironment.StorageProvider=\"Sqlite\" — current provider does not register IRunQueueRepository/IPendingVerificationRepository. Running inline."
                    : "enqueue failed — see preceding logs. Running inline.";
                Logger.LogWarning("[{Agent}] {Reason}", Name, reason);
                steps.Add(new TestStep
                {
                    Action = $"verify[{index}] inline",
                    Summary = $"Running verification(s) inline (not deferred): {reason}",
                    Status = TestStatus.Skipped,  // grey step — informational, not a failure
                });
            }

            for (var vIdx = 0; vIdx < tc.PostDeliveryVerifications.Count; vIdx++)
            {
                ct.ThrowIfCancellationRequested();
                await RunVerificationAsync(
                    tc.PostDeliveryVerifications[vIdx],
                    index, vIdx + 1,
                    remoteFileName,
                    context,
                    steps,
                    environmentKey,
                    ct);
            }
        }
    }

    /// <summary>
    /// Enqueues a single deferred <c>VerifyOnly</c> queue entry covering all
    /// post-delivery verifications for this test case, plus a row in
    /// <c>run_pending_verifications</c>. Emits an AwaitingVerification synthetic
    /// step per verification onto <paramref name="steps"/> so the parent run
    /// shows due-time information in the UI.
    /// Returns false (and emits no steps) if enqueueing fails — the caller then
    /// falls back to inline execution so a transient queue-repo error doesn't
    /// lose the verification.
    /// </summary>
    private bool TryEnqueueDeferredVerifications(
        AseXmlDeliveryTestCase tc,
        int deliveryIndex,
        string remoteFileName,
        IReadOnlyDictionary<string, string> context,
        string? environmentKey,
        List<TestStep> steps,
        out int enqueuedCount)
    {
        enqueuedCount = 0;
        try
        {
            var queueRepo = _services.GetRequiredService<IRunQueueRepository>();
            var pendingRepo = _services.GetRequiredService<IPendingVerificationRepository>();

            var parentRunId = GetTaskParam<string>("RunId") ?? "";
            var moduleId = GetTaskParam<string>("ModuleId") ?? "";
            var testSetId = GetTaskParam<string>("TestSetId") ?? "";
            var objectiveId = GetTaskParam<string>("ObjectiveId") ?? _currentTaskId ?? "";
            var objectiveName = GetTaskParam<string>("ObjectiveName") ?? "";
            if (string.IsNullOrEmpty(parentRunId) || string.IsNullOrEmpty(testSetId))
            {
                Logger.LogWarning(
                    "Cannot defer verifications for '{Objective}': missing RunId or TestSetId on task parameters. " +
                    "Running inline.", objectiveName);
                return false;
            }

            var now = DateTime.UtcNow;
            var minWait = tc.PostDeliveryVerifications.Min(v => v.WaitBeforeSeconds);
            var maxWait = tc.PostDeliveryVerifications.Max(v => v.WaitBeforeSeconds);
            var fraction = Math.Clamp(_config.AseXml.VerificationEarlyStartFraction, 0.01, 1.0);
            var firstDue = now.AddSeconds(minWait * fraction);
            var deadline = now.AddSeconds(maxWait + _config.AseXml.VerificationGraceSeconds);

            var request = new DeferredVerificationRequest
            {
                ParentRunId = parentRunId,
                ModuleId = moduleId,
                TestSetId = testSetId,
                DeliveryObjectiveId = objectiveId,
                DeliveryObjectiveName = objectiveName,
                EnvironmentKey = environmentKey,
                DeliveryCompletedAt = now,
                DeadlineAt = deadline,
                AttemptCount = 0,
                DeliveryContext = new Dictionary<string, string>(context, StringComparer.OrdinalIgnoreCase),
                Verifications = tc.PostDeliveryVerifications,
            };

            // Pick the claim capability from the FIRST verification's target (typical case:
            // all verifications share a target; handler validates individually at replay).
            var firstTarget = tc.PostDeliveryVerifications[0].Target;

            var pendingId = Guid.NewGuid().ToString("N")[..12];
            request.PendingId = pendingId;

            var queueEntry = new RunQueueEntry
            {
                Id = pendingId,  // first entry id == pending_id for stable identity across retries
                ModuleId = moduleId,
                TestSetId = testSetId,
                ObjectiveId = objectiveId,
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
            queueRepo.EnqueueAsync(queueEntry).GetAwaiter().GetResult();

            pendingRepo.InsertAsync(new PendingVerification
            {
                PendingId = pendingId,
                ParentRunId = parentRunId,
                CurrentQueueEntryId = queueEntry.Id,
                ModuleId = moduleId,
                TestSetId = testSetId,
                DeliveryObjectiveId = objectiveId,
                FirstDueAt = firstDue,
                DeadlineAt = deadline,
                AttemptCount = 0,
                Status = "Pending",
                AttemptLogJson = "[]",
            }).GetAwaiter().GetResult();

            var firstDueLocal = firstDue.ToLocalTime();
            var deadlineLocal = deadline.ToLocalTime();
            for (var vIdx = 0; vIdx < tc.PostDeliveryVerifications.Count; vIdx++)
            {
                var v = tc.PostDeliveryVerifications[vIdx];
                steps.Add(new TestStep
                {
                    Action = $"verify[{deliveryIndex}.{vIdx + 1}] scheduled",
                    Summary = $"'{v.Description}' — first attempt at {firstDueLocal:HH:mm:ss}, deadline {deadlineLocal:HH:mm:ss} ({v.Target})",
                    Status = TestStatus.AwaitingVerification,
                    // ISO UTC timestamps stay in detail so the UI countdown can parse them
                    // deterministically regardless of browser timezone. Don't rely on the
                    // summary string for timing.
                    Detail = $"pendingId={pendingId}\nremoteFile={remoteFileName}\nwaitSeconds={v.WaitBeforeSeconds}\nfirstDueAtUtc={firstDue:O}\ndeadlineAtUtc={deadline:O}",
                });
            }

            enqueuedCount = tc.PostDeliveryVerifications.Count;
            Logger.LogInformation(
                "[{Agent}] Deferred {Count} verification(s) for delivery {Idx} — pendingId={Pid}, firstDue={Due}, deadline={Dl}",
                Name, enqueuedCount, deliveryIndex, pendingId, firstDue, deadline);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to enqueue deferred verifications; falling back to inline execution.");
            return false;
        }
    }

    private T? GetTaskParam<T>(string key) where T : class
    {
        if (_currentTask is null) return null;
        return _currentTask.Parameters.TryGetValue(key, out var v) && v is T typed ? typed : null;
    }

    private static readonly JsonSerializerOptions DeferredJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Current task context — stashed at the top of ExecuteAsync so helpers on this
    // instance can read task parameters without being passed the task everywhere.
    private TestTask? _currentTask;
    private string? _currentTaskId;

    // ─────────────────────────────────────────────────────
    // Phase 3 — verifications
    // ─────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildVerificationContext(
        string messageId, string transactionId, string remoteFileName,
        string uploadedAs, string endpointCode,
        IReadOnlyDictionary<string, string> resolvedFields)
    {
        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MessageID"]     = messageId,
            ["TransactionID"] = transactionId,
            ["Filename"]      = remoteFileName,
            ["EndpointCode"]  = endpointCode,
            ["UploadedAs"]    = uploadedAs,
        };
        // Every resolved field (NMI, MeterSerial, DateIdentified, etc.) joins the context.
        // Dedicated keys above win if the render happens to share a name.
        foreach (var (k, v) in resolvedFields)
        {
            if (!ctx.ContainsKey(k)) ctx[k] = v;
        }
        return ctx;
    }

    private async Task RunVerificationAsync(
        VerificationStep v,
        int deliveryIndex,
        int verifyIndex,
        string remoteFileName,
        IReadOnlyDictionary<string, string> context,
        List<TestStep> steps,
        string? environmentKey,
        CancellationToken ct)
    {
        var waitAction = $"wait[{deliveryIndex}.{verifyIndex}]";
        if (v.WaitBeforeSeconds > 0)
        {
            steps.Add(TestStep.Pass(waitAction,
                $"Waiting {v.WaitBeforeSeconds}s for Bravo to process {remoteFileName} before '{v.Description}'"));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(v.WaitBeforeSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                steps.Add(TestStep.Err(waitAction, "Wait cancelled"));
                return;
            }
        }

        var verifyAction = $"verify[{deliveryIndex}.{verifyIndex}]";

        if (!Enum.TryParse<TestTargetType>(v.Target, ignoreCase: true, out var target))
        {
            steps.Add(TestStep.Fail(verifyAction,
                $"VerificationStep.Target '{v.Target}' is not a known TestTargetType."));
            return;
        }

        // Build a synthetic task for the sibling UI agent and preload a substituted test case.
        var syntheticTask = new TestTask
        {
            Description = v.Description,
            Target = target,
            Parameters = new Dictionary<string, object>(),
        };
        if (!string.IsNullOrWhiteSpace(environmentKey))
            syntheticTask.Parameters["EnvironmentKey"] = environmentKey!;

        if (target is TestTargetType.UI_Web_MVC or TestTargetType.UI_Web_Blazor)
        {
            if (v.WebUi is null)
            {
                steps.Add(TestStep.Fail(verifyAction,
                    $"Target is '{v.Target}' but WebUi steps are missing on the VerificationStep."));
                return;
            }
            var clone = CloneAndSubstitute(v.WebUi, context);
            syntheticTask.Parameters["PreloadedTestCases"] = new List<WebUiTestCase> { clone.ToTestCase(v.Description) };
        }
        else if (target is TestTargetType.UI_Desktop_WinForms)
        {
            if (v.DesktopUi is null)
            {
                steps.Add(TestStep.Fail(verifyAction,
                    $"Target is '{v.Target}' but DesktopUi steps are missing on the VerificationStep."));
                return;
            }
            var clone = CloneAndSubstitute(v.DesktopUi, context);
            syntheticTask.Parameters["PreloadedTestCases"] = new List<DesktopUiTestCase> { clone.ToTestCase(v.Description) };
        }
        else
        {
            steps.Add(TestStep.Fail(verifyAction,
                $"Verification target '{v.Target}' is not a UI target; only UI_Web_MVC / UI_Web_Blazor / UI_Desktop_WinForms are supported."));
            return;
        }

        ITestAgent? sibling = null;
        foreach (var candidate in Siblings)
        {
            if (await candidate.CanHandleAsync(syntheticTask)) { sibling = candidate; break; }
        }
        if (sibling is null)
        {
            steps.Add(TestStep.Err(verifyAction,
                $"No registered agent can handle target '{v.Target}'. Is the UI agent registered in DI?"));
            return;
        }

        Logger.LogInformation(
            "[{Agent}] Running verification {Idx} '{Desc}' via {Sibling}",
            Name, verifyAction, v.Description, sibling.Name);

        TestResult childResult;
        try
        {
            childResult = await sibling.ExecuteAsync(syntheticTask, ct);
        }
        catch (Exception ex)
        {
            steps.Add(TestStep.Err(verifyAction,
                $"Verification agent '{sibling.Name}' threw: {ex.Message}"));
            return;
        }

        // Prefix each child step so the verification steps are clearly grouped.
        foreach (var childStep in childResult.Steps)
        {
            steps.Add(new TestStep
            {
                Action = $"{verifyAction} {childStep.Action}",
                Summary = childStep.Summary,
                Status = childStep.Status,
                Detail = childStep.Detail,
                Duration = childStep.Duration,
            });
        }

        // Surface the aggregate status from the verification so the delivery
        // objective reflects a failed verification as Failed (not Passed).
        if (childResult.Status is TestStatus.Failed or TestStatus.Error
            && childResult.Steps.Count == 0)
        {
            // Sibling reported status without steps — make that visible.
            steps.Add(new TestStep
            {
                Action = verifyAction,
                Summary = childResult.Summary,
                Status = childResult.Status,
            });
        }
    }

    private static WebUiTestDefinition CloneAndSubstitute(
        WebUiTestDefinition src, IReadOnlyDictionary<string, string> ctx)
    {
        var clone = new WebUiTestDefinition
        {
            Description = src.Description,
            StartUrl = TokenSubstituter.Substitute(src.StartUrl, ctx) ?? src.StartUrl,
            TakeScreenshotOnFailure = src.TakeScreenshotOnFailure,
            Steps = src.Steps.Select(s => new WebUiStep
            {
                Action    = s.Action,
                Selector  = TokenSubstituter.Substitute(s.Selector, ctx),
                Value     = TokenSubstituter.Substitute(s.Value,    ctx),
                TimeoutMs = s.TimeoutMs,
            }).ToList(),
        };
        return clone;
    }

    private static DesktopUiTestDefinition CloneAndSubstitute(
        DesktopUiTestDefinition src, IReadOnlyDictionary<string, string> ctx)
    {
        var clone = new DesktopUiTestDefinition
        {
            Description = src.Description,
            TakeScreenshotOnFailure = src.TakeScreenshotOnFailure,
            Steps = src.Steps.Select(s => new DesktopUiStep
            {
                Action       = s.Action,
                AutomationId = TokenSubstituter.Substitute(s.AutomationId, ctx),
                Name         = TokenSubstituter.Substitute(s.Name,         ctx),
                ClassName    = TokenSubstituter.Substitute(s.ClassName,    ctx),
                ControlType  = TokenSubstituter.Substitute(s.ControlType,  ctx),
                TreePath     = TokenSubstituter.Substitute(s.TreePath,     ctx),
                Value        = TokenSubstituter.Substitute(s.Value,        ctx),
                MenuPath     = TokenSubstituter.Substitute(s.MenuPath,     ctx),
                WindowTitle  = TokenSubstituter.Substitute(s.WindowTitle,  ctx),
                TimeoutMs    = s.TimeoutMs,
            }).ToList(),
        };
        return clone;
    }

    // ─────────────────────────────────────────────────────
    // LLM prompt
    // ─────────────────────────────────────────────────────

    private async Task<List<AseXmlDeliveryTestCase>?> GenerateTestCasesAsync(
        TestTask task, CancellationToken ct)
    {
        // Pull the endpoint code catalogue from the resolver so the LLM
        // picks from the real list in the target Bravo DB.
        IReadOnlyList<string> endpointCodes;
        try
        {
            endpointCodes = await _endpoints.ListCodesAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Could not load endpoint codes from mil.V2_MIL_EndPoint — LLM will see an empty catalogue. " +
                "Pass --endpoint <code> to supply one explicitly.");
            endpointCodes = Array.Empty<string>();
        }

        var templateCatalogue = BuildTemplateCatalogue();
        var endpointCatalogue = endpointCodes.Count > 0
            ? string.Join(", ", endpointCodes)
            : "(none discoverable — caller must supply --endpoint)";

        var prompt = $$"""
            You are generating aseXML DELIVERY test cases for an AEMO B2B test run.
            Each case names ONE template to render and ONE endpoint code to ship to.

            Objective:
            "{{task.Description}}"

            Available templates (copy templateId + transactionType verbatim):
            {{templateCatalogue}}

            Available endpoint codes (copy verbatim into endpointCode):
            {{endpointCatalogue}}

            RULES:
            - Read the objective LITERALLY. Return exactly one delivery case unless the
              objective explicitly asks for multiple variants.
            - Populate only fields listed as "source: user" in the chosen template.
            - NEVER set auto fields (MessageID, TransactionID, MessageDate, TransactionDate).
            - NEVER set const fields.
            - Populate endpointCode with the code named in the objective (e.g. "GatewaySPARQ"),
              matching verbatim one of the Available endpoint codes above.
              If the objective does not name an endpoint and none of the codes obviously
              matches, leave endpointCode as an empty string — the CLI may supply a default.
            - Keep the description short and specific (e.g. "Deliver MFN One In All In for NMI 4103035611 to GatewaySPARQ").

            Respond ONLY with a JSON array of this shape (no prose, no markdown fences):
            [
              {
                "name": "short label",
                "description": "what this case verifies",
                "templateId": "MFN-OneInAllIn",
                "transactionType": "MeterFaultAndIssueNotification",
                "fieldValues": {
                  "NMI": "4103035611"
                },
                "endpointCode": "GatewaySPARQ",
                "validateAgainstSchema": false
              }
            ]
            """;

        return await AskLlmForJsonAsync<List<AseXmlDeliveryTestCase>>(prompt, ct);
    }

    private string BuildTemplateCatalogue()
    {
        var sb = new StringBuilder();
        foreach (var t in _templates.All())
        {
            sb.AppendLine($"- templateId: {t.Manifest.TemplateId}");
            sb.AppendLine($"  transactionType: {t.Manifest.TransactionType}");
            if (!string.IsNullOrWhiteSpace(t.Manifest.TransactionGroup))
                sb.AppendLine($"  transactionGroup: {t.Manifest.TransactionGroup}");
            if (!string.IsNullOrWhiteSpace(t.Manifest.Description))
                sb.AppendLine($"  description: {t.Manifest.Description}");

            var userFields = t.Manifest.Fields
                .Where(f => string.Equals(f.Value.Source, "user", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (userFields.Count > 0)
            {
                sb.AppendLine("  userFields:");
                foreach (var (name, spec) in userFields)
                {
                    var req = spec.Required ? "required" : "optional";
                    var ex  = string.IsNullOrWhiteSpace(spec.Example) ? "" : $" example=\"{spec.Example}\"";
                    sb.AppendLine($"    - {name} ({req}){ex}");
                }
            }
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────

    private string ResolveRunOutputDir(string runId)
    {
        var root = Path.IsPathRooted(_config.AseXml.OutputPath)
            ? _config.AseXml.OutputPath
            : Path.Combine(AppContext.BaseDirectory, _config.AseXml.OutputPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(root, $"{stamp}_{runId}_deliver");
    }

    private static string MakeFileSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Trim().Replace(' ', '-');
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private static string BuildRenderDetail(
        AseXmlDeliveryTestCase tc, string txnType, string xmlLocalPath,
        IReadOnlyDictionary<string, string> resolvedFields, string xml)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Template: {tc.TemplateId} ({txnType})");
        sb.AppendLine($"Local:    {xmlLocalPath}");
        sb.AppendLine();
        sb.AppendLine("Resolved fields:");
        foreach (var (k, v) in resolvedFields.OrderBy(kv => kv.Key))
            sb.AppendLine($"  {k} = {v}");
        sb.AppendLine();
        var preview = xml.Length > 1200 ? xml[..1200] + "\n…[truncated]" : xml;
        sb.AppendLine("XML preview:");
        sb.Append(preview);
        return sb.ToString();
    }

    private TestResult BuildResult(
        TestTask task, List<TestStep> steps, TestStatus status, string summary,
        Stopwatch sw, List<AseXmlDeliveryTestDefinition> definitions,
        List<Dictionary<string, object?>> deliveries, string? outputDir = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["generatedTestCases"] = definitions,
            ["deliveries"] = deliveries,
        };
        if (outputDir is not null) metadata["outputDir"] = outputDir;

        return new TestResult
        {
            ObjectiveId = task.Id,
            ObjectiveName = task.Description,
            AgentName = Name,
            Status = status,
            Summary = summary,
            Steps = steps,
            Duration = sw.Elapsed,
            Metadata = metadata
        };
    }
}
