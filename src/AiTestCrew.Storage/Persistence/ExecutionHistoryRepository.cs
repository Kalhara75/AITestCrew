using System.Text.Json;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Reads and writes execution run history as JSON files.
/// Storage layout: executions/{testSetId}/{runId}.json
/// </summary>
public class ExecutionHistoryRepository : IExecutionHistoryRepository
{
    private readonly string _baseDir;
    private readonly int _maxRunsPerTestSet;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ExecutionHistoryRepository(string baseDir, int maxRunsPerTestSet = 0)
    {
        _baseDir = Path.Combine(baseDir, "executions");
        _maxRunsPerTestSet = maxRunsPerTestSet;
        System.IO.Directory.CreateDirectory(_baseDir);
    }

    /// <summary>Saves a completed execution run to disk.</summary>
    public async Task SaveAsync(PersistedExecutionRun run)
    {
        var dir = Path.Combine(_baseDir, run.TestSetId);
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{run.RunId}.json");
        var json = JsonSerializer.Serialize(run, JsonOpts);
        await File.WriteAllTextAsync(path, json);
        await PruneOldRunsAsync(run.TestSetId);
    }

    /// <summary>Loads a single execution run by test set ID and run ID.</summary>
    public async Task<PersistedExecutionRun?> GetRunAsync(string testSetId, string runId)
    {
        var path = Path.Combine(_baseDir, testSetId, $"{runId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        var run = JsonSerializer.Deserialize<PersistedExecutionRun>(json, JsonOpts);
        run?.MigrateToV2();
        return run;
    }

    /// <summary>Lists all execution runs for a test set, ordered by StartedAt descending.</summary>
    public IReadOnlyList<PersistedExecutionRun> ListRuns(string testSetId)
    {
        var dir = Path.Combine(_baseDir, testSetId);
        if (!System.IO.Directory.Exists(dir)) return [];

        var result = new List<PersistedExecutionRun>();
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var run = JsonSerializer.Deserialize<PersistedExecutionRun>(json, JsonOpts);
                if (run is not null)
                {
                    run.MigrateToV2();
                    result.Add(run);
                }
            }
            catch { /* skip malformed files */ }
        }
        return result.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>Returns the most recent execution run for a test set, or null.</summary>
    public PersistedExecutionRun? GetLatestRun(string testSetId)
    {
        var runs = ListRuns(testSetId);
        return runs.Count > 0 ? runs[0] : null;
    }

    /// <summary>
    /// Returns the most recent execution result for each objective across all runs.
    /// Scans runs in descending date order and picks the first result seen per objective ID.
    /// </summary>
    public Dictionary<string, (PersistedObjectiveResult Result, string RunId)> GetLatestObjectiveStatuses(string testSetId)
    {
        var runs = ListRuns(testSetId); // ordered descending by StartedAt
        var result = new Dictionary<string, (PersistedObjectiveResult, string)>();
        foreach (var run in runs)
        {
            foreach (var obj in run.ObjectiveResults)
                result.TryAdd(obj.ObjectiveId, (obj, run.RunId));
        }
        return result;
    }

    /// <summary>Deletes all execution runs for a given test set.</summary>
    public Task DeleteRunsForTestSetAsync(string testSetId)
    {
        var dir = Path.Combine(_baseDir, testSetId);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Deletes a single execution run file.</summary>
    public Task DeleteRunAsync(string testSetId, string runId)
    {
        var path = Path.Combine(_baseDir, testSetId, $"{runId}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Removes all results for a specific objective from every run in the test set.
    /// Deletes runs that become empty; recomputes aggregate counts on remaining runs.</summary>
    public async Task RemoveObjectiveFromHistoryAsync(string testSetId, string objectiveId)
    {
        var dir = Path.Combine(_baseDir, testSetId);
        if (!System.IO.Directory.Exists(dir)) return;

        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var run = JsonSerializer.Deserialize<PersistedExecutionRun>(json, JsonOpts);
                if (run is null) continue;
                run.MigrateToV2();

                var removed = run.ObjectiveResults.RemoveAll(r => r.ObjectiveId == objectiveId);
                if (removed == 0) continue;

                if (run.ObjectiveResults.Count == 0)
                {
                    File.Delete(file);
                }
                else
                {
                    run.TotalObjectives = run.ObjectiveResults.Count;
                    run.PassedObjectives = run.ObjectiveResults.Count(r => r.Status == "Passed");
                    run.FailedObjectives = run.ObjectiveResults.Count(r => r.Status == "Failed");
                    run.ErrorObjectives = run.ObjectiveResults.Count(r => r.Status == "Error");
                    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(run, JsonOpts));
                }
            }
            catch { /* skip malformed files */ }
        }
    }

    /// <summary>
    /// Returns a context dictionary seeded from the latest successful delivery for
    /// a specific objective — <c>MessageID</c>, <c>TransactionID</c>, <c>Filename</c>,
    /// <c>EndpointCode</c>, <c>RemotePath</c>. Used by <c>--record-verification</c> to
    /// auto-parameterise captured UI steps against real data the system has already
    /// processed.
    ///
    /// Returns null when no passed run exists for the objective OR the passed run
    /// has no persisted deliveries (e.g. non-delivery objective). <paramref name="moduleId"/>
    /// is accepted for future use but the lookup is scoped by <paramref name="testSetId"/>
    /// today (matching how history is stored on disk).
    /// </summary>
    public Task<Dictionary<string, string>?> GetLatestDeliveryContextAsync(
        string testSetId, string? moduleId, string objectiveId)
    {
        foreach (var run in ListRuns(testSetId))  // already ordered StartedAt desc
        {
            var match = run.ObjectiveResults
                .FirstOrDefault(o => string.Equals(o.ObjectiveId, objectiveId, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(o.Status, "Passed", StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            if (match.Deliveries is null || match.Deliveries.Count == 0) continue;

            var d = match.Deliveries[0];
            var filename = Path.GetFileName(d.RemotePath);  // derive from remotePath
            var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MessageID"]     = d.MessageId,
                ["TransactionID"] = d.TransactionId,
                ["Filename"]      = filename,
                ["EndpointCode"]  = d.EndpointCode,
                ["RemotePath"]    = d.RemotePath,
                ["UploadedAs"]    = d.UploadedAs,
            };
            return Task.FromResult<Dictionary<string, string>?>(ctx);
        }

        return Task.FromResult<Dictionary<string, string>?>(null);
    }

    /// <summary>Returns the number of execution run files for a test set (lightweight, no deserialization).</summary>
    public int CountRuns(string testSetId)
    {
        var dir = Path.Combine(_baseDir, testSetId);
        if (!System.IO.Directory.Exists(dir)) return 0;
        return System.IO.Directory.EnumerateFiles(dir, "*.json").Count();
    }

    /// <summary>Directory where execution history is stored.</summary>
    public string Directory => _baseDir;

    private async Task PruneOldRunsAsync(string testSetId)
    {
        if (_maxRunsPerTestSet <= 0) return;

        var runs = ListRuns(testSetId); // ordered by StartedAt descending
        if (runs.Count <= _maxRunsPerTestSet) return;

        foreach (var run in runs.Skip(_maxRunsPerTestSet))
            await DeleteRunAsync(testSetId, run.RunId);
    }
}
