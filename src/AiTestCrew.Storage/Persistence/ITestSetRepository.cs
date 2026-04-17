namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Abstraction for test set storage (both legacy flat and module-scoped).
/// </summary>
public interface ITestSetRepository
{
    // ── Legacy (flat) operations ──
    Task SaveAsync(PersistedTestSet testSet);
    Task<PersistedTestSet?> LoadAsync(string id);
    IReadOnlyList<PersistedTestSet> ListAll();
    Task UpdateRunStatsAsync(string id);

    // ── Module-scoped operations ──
    Task SaveAsync(PersistedTestSet testSet, string moduleId);
    Task<PersistedTestSet?> LoadAsync(string moduleId, string testSetId);
    IReadOnlyList<PersistedTestSet> ListByModule(string moduleId);
    Task<PersistedTestSet> CreateEmptyAsync(string moduleId, string name);
    Task MergeObjectivesAsync(
        string moduleId, string testSetId,
        List<TestObjective> newObjectives, string objective,
        string? objectiveName = null,
        string? apiStackKey = null, string? apiModule = null,
        string? endpointCode = null);
    Task UpdateRunStatsAsync(string moduleId, string testSetId);
    Task DeleteAsync(string moduleId, string testSetId);
    Task MoveObjectiveAsync(
        string sourceModuleId, string sourceTestSetId,
        string destModuleId, string destTestSetId,
        string objective);
}
