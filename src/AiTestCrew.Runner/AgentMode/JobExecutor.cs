using System.Text.Json;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;

namespace AiTestCrew.Runner.AgentMode;

/// <summary>
/// Bridges a dequeued job to the existing <see cref="TestOrchestrator"/>:
/// reconstructs the run parameters from the request JSON and invokes RunAsync.
/// </summary>
internal sealed class JobExecutor
{
    private readonly TestOrchestrator _orchestrator;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JobExecutor(TestOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<TestSuiteResult> ExecuteAsync(NextJobResponse job, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<QueuedRunRequest>(job.RequestJson, JsonOpts)
            ?? throw new InvalidOperationException("Invalid run request JSON");

        if (!Enum.TryParse<RunMode>(request.Mode, true, out var mode))
            mode = RunMode.Reuse;

        var objective = mode is RunMode.Reuse or RunMode.VerifyOnly ? "" : (request.Objective ?? "");
        var reuseId = mode is RunMode.Reuse or RunMode.VerifyOnly ? request.TestSetId : null;

        return await _orchestrator.RunAsync(
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
