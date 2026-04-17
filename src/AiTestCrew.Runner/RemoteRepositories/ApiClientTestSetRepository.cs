using System.Text.Json;
using AiTestCrew.Agents.Persistence;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// Calls the WebApi over HTTP to manage test sets. Used when <c>ServerUrl</c> is configured.
/// </summary>
internal sealed class ApiClientTestSetRepository : ITestSetRepository
{
    private readonly RemoteHttpClient _http;

    public ApiClientTestSetRepository(RemoteHttpClient http) => _http = http;

    // ── Legacy (flat) operations ──

    public async Task SaveAsync(PersistedTestSet testSet)
    {
        // Legacy saves go to module_id="" — use the data endpoint with empty module
        await _http.PutAsync($"api/modules//testsets/{testSet.Id}/data", testSet);
    }

    public async Task<PersistedTestSet?> LoadAsync(string id)
    {
        var json = await _http.GetStringOrNullAsync($"api/testsets/{id}");
        return json is null ? null : JsonSerializer.Deserialize<PersistedTestSet>(json, RemoteHttpClient.JsonOpts);
    }

    public IReadOnlyList<PersistedTestSet> ListAll()
    {
        var result = _http.GetAsync<List<PersistedTestSet>>("api/testsets")
            .GetAwaiter().GetResult();
        return result ?? [];
    }

    public async Task UpdateRunStatsAsync(string id)
    {
        await _http.PostAsync<object>($"api/modules//testsets/{id}/run-stats", new { });
    }

    // ── Module-scoped operations ──

    public async Task SaveAsync(PersistedTestSet testSet, string moduleId)
    {
        await _http.PutAsync($"api/modules/{moduleId}/testsets/{testSet.Id}/data", testSet);
    }

    public async Task<PersistedTestSet?> LoadAsync(string moduleId, string testSetId)
    {
        var json = await _http.GetStringOrNullAsync($"api/modules/{moduleId}/testsets/{testSetId}");
        return json is null ? null : JsonSerializer.Deserialize<PersistedTestSet>(json, RemoteHttpClient.JsonOpts);
    }

    public IReadOnlyList<PersistedTestSet> ListByModule(string moduleId)
    {
        var result = _http.GetAsync<List<PersistedTestSet>>($"api/modules/{moduleId}/testsets")
            .GetAwaiter().GetResult();
        return result ?? [];
    }

    public async Task<PersistedTestSet> CreateEmptyAsync(string moduleId, string name)
    {
        var result = await _http.PostAsync<object, PersistedTestSet>(
            $"api/modules/{moduleId}/testsets", new { name });
        return result!;
    }

    public async Task MergeObjectivesAsync(
        string moduleId, string testSetId,
        List<TestObjective> newObjectives, string objective,
        string? objectiveName = null,
        string? apiStackKey = null, string? apiModule = null,
        string? endpointCode = null)
    {
        await _http.PostAsync($"api/modules/{moduleId}/testsets/{testSetId}/merge", new
        {
            objectives = newObjectives,
            objective,
            objectiveName,
            apiStackKey,
            apiModule,
            endpointCode
        });
    }

    public async Task UpdateRunStatsAsync(string moduleId, string testSetId)
    {
        await _http.PostAsync<object>($"api/modules/{moduleId}/testsets/{testSetId}/run-stats", new { });
    }

    public async Task DeleteAsync(string moduleId, string testSetId)
    {
        await _http.DeleteAsync($"api/modules/{moduleId}/testsets/{testSetId}");
    }

    public async Task MoveObjectiveAsync(
        string sourceModuleId, string sourceTestSetId,
        string destModuleId, string destTestSetId,
        string objective)
    {
        await _http.PostAsync($"api/modules/{sourceModuleId}/testsets/{sourceTestSetId}/move-objective",
            new
            {
                objective,
                destinationModuleId = destModuleId,
                destinationTestSetId = destTestSetId
            });
    }
}
