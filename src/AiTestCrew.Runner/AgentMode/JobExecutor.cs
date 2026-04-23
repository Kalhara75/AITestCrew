using System.Text.Json;
using AiTestCrew.Agents.AseXmlAgent.Delivery;
using AiTestCrew.Agents.Recording;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;

namespace AiTestCrew.Runner.AgentMode;

/// <summary>
/// Bridges a dequeued job to the right handler. JobKind decides whether it's a
/// standard test run (<see cref="TestOrchestrator"/>) or an interactive recording
/// session (<see cref="IRecordingService"/>).
/// </summary>
internal sealed class JobExecutor
{
    private readonly TestOrchestrator _orchestrator;
    private readonly IRecordingService _recording;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JobExecutor(TestOrchestrator orchestrator, IRecordingService recording)
    {
        _orchestrator = orchestrator;
        _recording = recording;
    }

    public async Task<JobOutcome> ExecuteAsync(NextJobResponse job, CancellationToken ct)
    {
        var kind = string.IsNullOrWhiteSpace(job.JobKind) ? "Run" : job.JobKind;
        return kind switch
        {
            "Record"                 => await ExecuteRecordAsync(job, ct),
            "RecordSetup"            => await ExecuteRecordSetupAsync(job, ct),
            "RecordVerification"     => await ExecuteRecordVerificationAsync(job, ct),
            "AuthSetup"              => await ExecuteAuthSetupAsync(job, ct),
            _                        => await ExecuteRunAsync(job, ct),
        };
    }

    private async Task<JobOutcome> ExecuteRunAsync(NextJobResponse job, CancellationToken ct)
    {
        // Detect a deferred-verification payload (self-contained snapshot the delivery
        // agent enqueued earlier). If present we route to VerifyOnly mode using the
        // snapshot — do NOT call GetLatestDeliveryContextAsync.
        var deferred = TryParseDeferredRequest(job.RequestJson);
        if (deferred is not null)
        {
            var suiteDeferred = await _orchestrator.RunAsync(
                objective: "",
                mode: RunMode.VerifyOnly,
                reuseId: deferred.TestSetId,
                ct: ct,
                externalRunId: deferred.ParentRunId,
                moduleId: string.IsNullOrEmpty(deferred.ModuleId) ? null : deferred.ModuleId,
                targetTestSetId: deferred.TestSetId,
                objectiveId: deferred.DeliveryObjectiveId,
                environmentKey: deferred.EnvironmentKey,
                deferredVerification: deferred);

            // Outcome of a deferred job reports per-attempt success. AwaitingVerification
            // (= retry re-enqueued) is not a terminal failure — treat as success so the
            // queue entry isn't flagged red.
            var passedOrRetrying = suiteDeferred.Results.All(r =>
                r.Status is TestStatus.Passed or TestStatus.AwaitingVerification);
            return new JobOutcome(passedOrRetrying, suiteDeferred.Summary,
                passedOrRetrying ? null : suiteDeferred.Summary);
        }

        var request = JsonSerializer.Deserialize<QueuedRunRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid run request JSON");

        if (!Enum.TryParse<RunMode>(request.Mode, true, out var mode))
            mode = RunMode.Reuse;

        var objective = mode is RunMode.Reuse or RunMode.VerifyOnly ? "" : (request.Objective ?? "");
        var reuseId = mode is RunMode.Reuse or RunMode.VerifyOnly ? request.TestSetId : null;

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
            environmentKey: request.EnvironmentKey);

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
        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private async Task<JobOutcome> ExecuteRecordSetupAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RecordSetupRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid RecordSetupRequest JSON");
        var result = await _recording.RecordSetupAsync(req, ct);
        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private async Task<JobOutcome> ExecuteRecordVerificationAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RecordVerificationRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid RecordVerificationRequest JSON");
        var result = await _recording.RecordVerificationAsync(req, ct);
        return new JobOutcome(result.Success, result.Summary, result.Success ? null : result.Error);
    }

    private async Task<JobOutcome> ExecuteAuthSetupAsync(NextJobResponse job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<AuthSetupRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid AuthSetupRequest JSON");
        var result = await _recording.AuthSetupAsync(req, ct);
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
    }
}

internal sealed record JobOutcome(bool Success, string Summary, string? Error = null);
