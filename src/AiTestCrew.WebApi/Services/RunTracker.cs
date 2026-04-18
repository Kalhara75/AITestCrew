using System.Collections.Concurrent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Tracks in-progress and recently completed test runs.
/// Singleton service — the Web API uses this to coordinate between
/// POST /api/runs (fire) and GET /api/runs/{id}/status (poll).
/// </summary>
public class RunTracker : IRunTracker
{
    private readonly ConcurrentDictionary<string, RunStatus> _runs = new();

    public RunStatus Create(string runId, string objective, string mode, string? testSetId)
    {
        var status = new RunStatus
        {
            RunId = runId,
            Objective = objective,
            Mode = mode,
            TestSetId = testSetId,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        };
        _runs[runId] = status;
        return status;
    }

    public RunStatus? Get(string runId) => _runs.GetValueOrDefault(runId);

    public void Complete(string runId, string testSetId)
    {
        if (_runs.TryGetValue(runId, out var status))
        {
            status.Status = "Completed";
            status.CompletedAt = DateTime.UtcNow;
            status.TestSetId = testSetId;
        }
    }

    public void Fail(string runId, string error)
    {
        if (_runs.TryGetValue(runId, out var status))
        {
            status.Status = "Failed";
            status.CompletedAt = DateTime.UtcNow;
            status.Error = error;
        }
    }

    public bool HasActiveRun() => _runs.Values.Any(IsActive);

    public bool HasActiveRunForTestSet(string testSetId) =>
        _runs.Values.Any(r => IsActive(r) && r.TestSetId == testSetId);

    public RunStatus? GetActiveRun() => _runs.Values.FirstOrDefault(IsActive);

    private static bool IsActive(RunStatus r) =>
        r.Status == "Running" || r.Status == "Queued" || r.Status == "Claimed";
}

public class RunStatus
{
    public string RunId { get; set; } = "";
    public string Objective { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? TestSetId { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}
