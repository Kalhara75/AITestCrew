using AiTestCrew.Agents.Persistence;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// Calls the WebApi over HTTP to manage modules. Used when <c>ServerUrl</c> is configured.
/// </summary>
internal sealed class ApiClientModuleRepository : IModuleRepository
{
    private readonly RemoteHttpClient _http;

    public ApiClientModuleRepository(RemoteHttpClient http) => _http = http;

    public async Task<PersistedModule> CreateAsync(string name, string? description = null)
    {
        var result = await _http.PostAsync<object, PersistedModule>(
            "api/modules", new { name, description });
        return result!;
    }

    public async Task<PersistedModule?> GetAsync(string moduleId) =>
        await _http.GetAsync<PersistedModule>($"api/modules/{moduleId}");

    public async Task<List<PersistedModule>> ListAllAsync() =>
        await _http.GetAsync<List<PersistedModule>>("api/modules") ?? [];

    public async Task UpdateAsync(PersistedModule module) =>
        await _http.PutAsync($"api/modules/{module.Id}", new { module.Name, module.Description });

    public async Task DeleteAsync(string moduleId) =>
        await _http.DeleteAsync($"api/modules/{moduleId}");

    public bool Exists(string moduleId)
    {
        // Synchronous call — use GetAwaiter().GetResult() since the interface is sync
        var module = _http.GetAsync<PersistedModule>($"api/modules/{moduleId}")
            .GetAwaiter().GetResult();
        return module is not null;
    }
}
