namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Abstraction for module manifest storage.
/// </summary>
public interface IModuleRepository
{
    Task<PersistedModule> CreateAsync(string name, string? description = null);
    Task<PersistedModule?> GetAsync(string moduleId);
    Task<List<PersistedModule>> ListAllAsync();
    Task UpdateAsync(PersistedModule module);
    Task DeleteAsync(string moduleId);
    bool Exists(string moduleId);
}
