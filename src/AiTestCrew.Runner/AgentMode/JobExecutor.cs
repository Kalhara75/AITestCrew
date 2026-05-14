using System.Text.Json;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Agents.Recording;
using AiTestCrew.Core.Exceptions;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Runner.AgentMode;

/// <summary>
/// Bridges a dequeued job to the right handler. JobKind decides whether it's a
/// standard test run (<see cref="TestOrchestrator"/>) or an interactive recording
/// session (<see cref="IRecordingService"/>).
///
/// Deferred post-step queue entries (discriminator <c>"DeferredVerification"</c>)
/// are routed directly to <see cref="PostStepOrchestrator"/> — no agent dispatch,
/// no history lookup — because the replay is pure post-step work against a
/// snapshotted context.
/// </summary>
internal sealed class JobExecutor
{
    private readonly TestOrchestrator _orchestrator;
    private readonly IRecordingService _recording;
    private readonly PostStepOrchestrator _postStepOrchestrator;
    private readonly IAuthRefreshRepository? _authRefreshRepo;
    private readonly IRunQueueRepository? _queueRepo;
    private readonly IRecordingLockRepository? _lockRepo;
    private readonly ILogger<JobExecutor>? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JobExecutor(
        TestOrchestrator orchestrator,
        IRecordingService recording,
        PostStepOrchestrator postStepOrchestrator,
        IAuthRefreshRepository? authRefreshRepo = null,
        IRunQueueRepository? queueRepo = null,
        IRecordingLockRepository? lockRepo = null,
        ILogger<JobExecutor>? logger = null)
    {
        _orchestrator = orchestrator;
        _recording = recording;
        _postStepOrchestrator = postStepOrchestrator;
        _authRefreshRepo = authRefreshRepo;
        _queueRepo = queueRepo;
        _lockRepo = lockRepo;
        _logger = logger;
    }

    public async Task<JobOutcome> ExecuteAsync(NextJobResponse job, string? agentId, CancellationToken ct)
    {
        var kind = string.IsNullOrWhiteSpace(job.JobKind) ? "Run" : job.JobKind;
        return kind switch
        {
            "Record"                 => await ExecuteRecordAsync(job, ct),
            "RecordSetup"            => await ExecuteRecordSetupAsync(job, ct),
            "RecordVerification"     => await ExecuteRecordVerificationAsync(job, ct),
            "AuthSetup"              => await ExecuteAuthSetupAsync(job, ct),
            _                        => await ExecuteRunAsync(job, agentId, ct),
        };
    }

    private async Task<JobOutcome> ExecuteRunAsync(NextJobResponse job, string? agentId, CancellationToken ct)
    {
        // Detect a deferred post-step payload (self-contained snapshot enqueued
        // by any parent agent). Route directly to PostStepOrchestrator — the
        // replay is pure post-step work against the snapshot, so we skip the
        // orchestrator → agent roundtrip that pre-Slice-2 code used.
        var deferred = TryParseDeferredRequest(job.RequestJson);
        if (deferred is not null)
        {
            try
            {
                var outcome = await _postStepOrchestrator.RunDeferredAttemptAsync(deferred, ct);
                // AwaitingVerification (= retry re-enqueued) is not a terminal failure —
                // treat as success so the queue entry isn't flagged red.
                var passedOrRetrying = outcome.Status is TestStatus.Passed or TestStatus.AwaitingVerification;
                return new JobOutcome(passedOrRetrying, outcome.Summary,
                    passedOrRetrying ? null : outcome.Summary);
            }
            catch (AuthRequiredException ex)
            {
                // Deferred verification hit a login redirect / 401 — park it on an
                // auth-refresh row exactly like a regular run. The re-enqueued
                // RequestJson still carries the DeferredVerificationRequest snapshot,
                // so when the janitor releases it the verification re-runs against
                // the same delivery context.
                var parked = await TryParkOnAuthRefreshAsync(
                    job, agentId, deferred.ModuleId, deferred.DeliveryObjectiveId, ex, ct);
                if (parked is not null) return parked;
                return new JobOutcome(false, "Auth required — no recovery dispatcher available", ex.Message);
            }
        }

        var request = JsonSerializer.Deserialize<QueuedRunRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid run request JSON");

        if (!Enum.TryParse<RunMode>(request.Mode, true, out var mode))
            mode = RunMode.Reuse;

        var objective = mode is RunMode.Reuse or RunMode.VerifyOnly ? "" : (request.Objective ?? "");
        var reuseId = mode is RunMode.Reuse or RunMode.VerifyOnly ? request.TestSetId : null;

        try
        {
            var suite = await _orchestrator.RunAsync(
                objective, mode, reuseId,
                externalRunId: job.JobId,
                moduleId: request.ModuleId,
                targetTestSetId: request.TestSetId,
                objectiveName: request.ObjectiveName,
                objectiveId: request.ObjectiveId,
                apiStackKey: request.ApiStackKey,
                apiModule: request.ApiModule,
                verificationWaitOverride: request.VerificationWaitOverride,
                environmentKey: request.EnvironmentKey,
                verifyStepFilter: request.VerifyStepFilter);

            // AwaitingVerification is a SUCCESS for the top-level agent job: the
            // delivery itself worked; only the verification is deferred. Reporting
            // failure here would pollute the RunTracker with a stale "Failed" +
            // surface the LLM's provisional "Inconclusive" summary as an error,
            // even though the deferred verification will complete cleanly later.
            var anyFailed = suite.Results.Any(r =>
                r.Status == TestStatus.Failed || r.Status == TestStatus.Error);
            var ok = !anyFailed;  // Passed + AwaitingVerification + Skipped count as success
            var summary = $"{suite.Passed}/{suite.TotalObjectives} passed";
            return new JobOutcome(ok, summary, ok ? null : suite.Summary);
        }
        catch (AuthRequiredException ex)
        {
            // Park the run on an outstanding auth-refresh and re-enqueue the same
            // work with auth_refresh_id set. The queue janitor releases the new
            // entry (resets not_before_at) when the refresh terminates.
            var parked = await TryParkOnAuthRefreshAsync(
                job, agentId, request.ModuleId, request.ObjectiveId, ex, ct);
            if (parked is not null) return parked;
            // Couldn't park (no repos available — local mode or config issue) →
            // fall through to a normal failed outcome.
            return new JobOutcome(false, "Auth required — no recovery dispatcher available", ex.Message);
        }
    }

    private async Task<JobOutcome?> TryParkOnAuthRefreshAsync(
        NextJobResponse job, string? agentId,
        string? fallbackModuleId, string? fallbackObjectiveId,
        AuthRequiredException ex, CancellationToken ct)
    {
        if (_authRefreshRepo is null || _queueRepo is null) return null;

        var requestedScope = new AuthRefreshRequest
        {
            EnvironmentKey = ex.EnvironmentKey,
            Surface = ex.Surface,
            ApiStackKey = ex.ApiStackKey,
            // Storage state is local to the agent's machine for UI surfaces;
            // null for API surface so any agent can drive the re-acquisition.
            AgentId = ex.Surface == AuthSurface.Api ? null : agentId,
            RequestedByRunId = job.JobId,
        };

        var saved = await _authRefreshRepo.InsertOrJoinAsync(requestedScope);
        _logger?.LogWarning(
            "Auth required for run {RunId} — registered/joined refresh {Id} (env={Env} surface={Surface})",
            job.JobId, saved.Id, ex.EnvironmentKey, ex.Surface);

        // Re-enqueue the same work with a far-future not_before_at so no agent
        // picks it up until the janitor releases it. ParentRunId carries the
        // run id forward so the dashboard polling stays attached.
        var deferred = new RunQueueEntry
        {
            ModuleId = job.ModuleId ?? fallbackModuleId ?? "",
            TestSetId = job.TestSetId,
            ObjectiveId = job.ObjectiveId ?? fallbackObjectiveId,
            TargetType = job.TargetType,
            Mode = job.Mode,
            JobKind = "Run",
            RequestJson = job.RequestJson,
            NotBeforeAt = DateTime.UtcNow.AddDays(7),  // janitor will reset on refresh completion
            ParentRunId = job.JobId,
            AuthRefreshId = saved.Id,
            CreatedAt = DateTime.UtcNow,
        };
        await _queueRepo.EnqueueAsync(deferred);

        return new JobOutcome(
            Success: true,  // Current entry is "done" — work transferred to deferred entry
            Summary: $"Auth required — paused on refresh {saved.Id}",
            Error: null,
            AuthRefreshId: saved.Id);
    }

    /// <summary>
    /// Attempts to interpret the queue entry's request_json as a deferred-verification
    /// snapshot. Returns null when it's a regular run request.
    /// </summary>
    private static DeferredVerificationRequest? TryParseDeferredRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            // Cheap discriminator check before full deserialisation.
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("kind", out var kindEl)
                || kindEl.ValueKind != JsonValueKind.String
                || kindEl.GetString() != "DeferredVerification")
                return null;

            return JsonSerializer.Deserialize<DeferredVerificationRequest>(json, JsonOpts);
        }
        catch { return null; }
    }

    private async Task<JobOutcome> ExecuteRecordAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RecordCaseRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid RecordCaseRequest JSON");
        var result = await _recording.RecordCaseAsync(req, ct);
        await TryReleaseLockAsync(job.JobId, ct);
        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private async Task<JobOutcome> ExecuteRecordSetupAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RecordSetupRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid RecordSetupRequest JSON");
        var result = await _recording.RecordSetupAsync(req, ct);
        await TryReleaseLockAsync(job.JobId, ct);
        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private async Task<JobOutcome> ExecuteRecordVerificationAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RecordVerificationRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid RecordVerificationRequest JSON");
        var result = await _recording.RecordVerificationAsync(req, ct);
        await TryReleaseLockAsync(job.JobId, ct);
        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private async Task TryReleaseLockAsync(string jobId, CancellationToken ct)
    {
        if (_lockRepo is null) return;
        try { await _lockRepo.ReleaseAsync(jobId, ct); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to release recording lock for job {JobId}", jobId);
        }
    }

    private async Task<JobOutcome> ExecuteAuthSetupAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<AuthSetupRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid AuthSetupRequest JSON");
        var result = await _recording.AuthSetupAsync(req, ct);

        // Settle the auth-refresh row so the dashboard banner clears. The queue
        // entry's own success only marks the QUEUE row Completed; the
        // run_auth_refreshes row stays InProgress until /complete or /fail is called.
        if (!string.IsNullOrEmpty(req.AuthRefreshId) && _authRefreshRepo is not null)
        {
            try
            {
                if (result.Success)
                    await _authRefreshRepo.MarkCompletedAsync(req.AuthRefreshId);
                else
                    await _authRefreshRepo.MarkFailedAsync(
                        req.AuthRefreshId, result.Error ?? "Auth setup failed");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "AuthSetup completed but failed to settle auth-refresh {Id}; janitor will time it out",
                    req.AuthRefreshId);
            }
        }

        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private sealed class QueuedRunRequest
    {
        public string? Objective { get; set; }
        public string? ObjectiveName { get; set; }
        public string Mode { get; set; } = "";
        public string? TestSetId { get; set; }
        public string? ModuleId { get; set; }
        public string? ObjectiveId { get; set; }
        public string? ApiStackKey { get; set; }
        public string? ApiModule { get; set; }
        public int? VerificationWaitOverride { get; set; }
        public string? EnvironmentKey { get; set; }
        public VerifyStepFilter? VerifyStepFilter { get; set; }
    }
}

internal sealed record JobOutcome(
    bool Success,
    string Summary,
    string? Error = null,
    string? AuthRefreshId = null);
