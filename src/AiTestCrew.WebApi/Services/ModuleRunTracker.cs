using System.Collections.Concurrent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Tracks in-progress and recently completed module-level test runs.
/// A module run sequentially executes all test sets within a module.
/// </summary>
public class ModuleRunTracker
{
    private readonly ConcurrentDictionary<string, ModuleRunStatus> _runs = new();

    public ModuleRunStatus Create(string moduleRunId, string moduleId, string moduleName,
        List<TestSetRunProgress> testSets)
    {
        var status = new ModuleRunStatus
        {
            ModuleRunId = moduleRunId,
            ModuleId = moduleId,
            ModuleName = moduleName,
            Status = "Running",
            StartedAt = DateTime.UtcNow,
            TestSets = testSets
        };
        _runs[moduleRunId] = status;
        return status;
    }

    public ModuleRunStatus? Get(string moduleRunId) => _runs.GetValueOrDefault(moduleRunId);

    public ModuleRunStatus? GetByModuleId(string moduleId) =>
        _runs.Values.FirstOrDefault(r => r.ModuleId == moduleId &&
            (r.Status == "Running" || r.CompletedAt > DateTime.UtcNow.AddMinutes(-2)));

    public void AdvanceToTestSet(string moduleRunId, string testSetId, string childRunId)
    {
        if (_runs.TryGetValue(moduleRunId, out var status))
        {
            var ts = status.TestSets.FirstOrDefault(t => t.TestSetId == testSetId);
            if (ts != null)
            {
                ts.Status = "Running";
                ts.ChildRunId = childRunId;
            }
        }
    }

    public void CompleteTestSet(string moduleRunId, string testSetId, bool success, string? error)
    {
        if (_runs.TryGetValue(moduleRunId, out var status))
        {
            var ts = status.TestSets.FirstOrDefault(t => t.TestSetId == testSetId);
            if (ts != null)
            {
                ts.Status = success ? "Completed" : "Failed";
                ts.Error = error;
            }
        }
    }

    public void Complete(string moduleRunId)
    {
        if (_runs.TryGetValue(moduleRunId, out var status))
        {
            var hasFailures = status.TestSets.Any(t => t.Status == "Failed");
            status.Status = hasFailures ? "CompletedWithFailures" : "Completed";
            status.CompletedAt = DateTime.UtcNow;
        }
    }

    public void Fail(string moduleRunId, string error)
    {
        if (_runs.TryGetValue(moduleRunId, out var status))
        {
            status.Status = "Failed";
            status.CompletedAt = DateTime.UtcNow;
            status.Error = error;
        }
    }

    public bool HasActiveModuleRun() => _runs.Values.Any(r => r.Status == "Running");

    public ModuleRunStatus? GetActiveRun() => _runs.Values.FirstOrDefault(r => r.Status == "Running");
}

public class ModuleRunStatus
{
    public string ModuleRunId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string Status { get; set; } = "Running";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public List<TestSetRunProgress> TestSets { get; set; } = new();
    public int CompletedCount => TestSets.Count(t => t.Status is "Completed" or "Failed");
    public int TotalCount => TestSets.Count;
    public string? CurrentTestSetId => TestSets.FirstOrDefault(t => t.Status == "Running")?.TestSetId;
}

public class TestSetRunProgress
{
    public string TestSetId { get; set; } = "";
    public string TestSetName { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? ChildRunId { get; set; }
    public string? Error { get; set; }
}
