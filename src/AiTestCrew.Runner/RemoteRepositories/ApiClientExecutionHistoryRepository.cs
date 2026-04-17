using System.Text.Json;
using AiTestCrew.Agents.Persistence;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// Calls the WebApi over HTTP to manage execution history. Used when <c>ServerUrl</c> is configured.
/// </summary>
internal sealed class ApiClientExecutionHistoryRepository : IExecutionHistoryRepository
{
    private readonly RemoteHttpClient _http;

    public ApiClientExecutionHistoryRepository(RemoteHttpClient http) => _http = http;

    public async Task SaveAsync(PersistedExecutionRun run)
    {
        await _http.PostAsync("api/executions", run);
    }

    public async Task<PersistedExecutionRun?> GetRunAsync(string testSetId, string runId)
    {
        // Try module-scoped path first (most common), fall back to legacy
        var json = await _http.GetStringOrNullAsync($"api/testsets/{testSetId}/runs/{runId}");
        return json is null ? null : JsonSerializer.Deserialize<PersistedExecutionRun>(json, RemoteHttpClient.JsonOpts);
    }

    public IReadOnlyList<PersistedExecutionRun> ListRuns(string testSetId)
    {
        var result = _http.GetAsync<List<PersistedExecutionRun>>($"api/testsets/{testSetId}/runs")
            .GetAwaiter().GetResult();
        return result ?? [];
    }

    public PersistedExecutionRun? GetLatestRun(string testSetId)
    {
        var runs = ListRuns(testSetId);
        return runs.Count > 0 ? runs[0] : null;
    }

    public Dictionary<string, (PersistedObjectiveResult Result, string RunId)> GetLatestObjectiveStatuses(string testSetId)
    {
        // Scan runs client-side (same approach as file-based repo)
        var runs = ListRuns(testSetId);
        var result = new Dictionary<string, (PersistedObjectiveResult, string)>();
        foreach (var run in runs)
        {
            foreach (var obj in run.ObjectiveResults)
                result.TryAdd(obj.ObjectiveId, (obj, run.RunId));
        }
        return result;
    }

    public async Task DeleteRunsForTestSetAsync(string testSetId)
    {
        // Delete each run individually — no bulk endpoint yet
        var runs = ListRuns(testSetId);
        foreach (var run in runs)
            await _http.DeleteAsync($"api/testsets/{testSetId}/runs/{run.RunId}");
    }

    public async Task DeleteRunAsync(string testSetId, string runId)
    {
        await _http.DeleteAsync($"api/testsets/{testSetId}/runs/{runId}");
    }

    public async Task RemoveObjectiveFromHistoryAsync(string testSetId, string objectiveId)
    {
        // Not typically called from the Runner — handled server-side via delete objective endpoint
        await Task.CompletedTask;
    }

    public async Task<Dictionary<string, string>?> GetLatestDeliveryContextAsync(
        string testSetId, string? moduleId, string objectiveId)
    {
        var modId = moduleId ?? "";
        return await _http.GetAsync<Dictionary<string, string>>(
            $"api/modules/{modId}/testsets/{testSetId}/delivery-context/{objectiveId}");
    }

    public int CountRuns(string testSetId)
    {
        var runs = ListRuns(testSetId);
        return runs.Count;
    }
}
