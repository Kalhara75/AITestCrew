using System.Text.Json;

namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Reads and writes execution run history as JSON files.
/// Storage layout: executions/{testSetId}/{runId}.json
/// </summary>
public class ExecutionHistoryRepository
{
    private readonly string _baseDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ExecutionHistoryRepository(string baseDir)
    {
        _baseDir = Path.Combine(baseDir, "executions");
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

    /// <summary>Deletes all execution runs for a given test set.</summary>
    public Task DeleteRunsForTestSetAsync(string testSetId)
    {
        var dir = Path.Combine(_baseDir, testSetId);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Directory where execution history is stored.</summary>
    public string Directory => _baseDir;
}
