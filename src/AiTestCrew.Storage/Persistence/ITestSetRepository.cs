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
    /// <summary>
    /// Versioned save: when <paramref name="expectedVersion"/> is provided and the stored version
    /// differs, throws <see cref="AiTestCrew.Core.Exceptions.ConcurrencyException"/>.
    /// When absent, behaves like the unconditional overload.
    /// </summary>
    Task SaveAsync(PersistedTestSet testSet, string moduleId, int? expectedVersion, string? userId = null);
    Task<PersistedTestSet?> LoadAsync(string moduleId, string testSetId);
    IReadOnlyList<PersistedTestSet> ListByModule(string moduleId);
    Task<PersistedTestSet> CreateEmptyAsync(string moduleId, string name);
    Task MergeObjectivesAsync(
        string moduleId, string testSetId,
        List<TestObjective> newObjectives, string objective,
        string? objectiveName = null,
        string? apiStackKey = null, string? apiModule = null,
        string? endpointCode = null,
        string? environmentKey = null);
    Task UpdateRunStatsAsync(string moduleId, string testSetId);
    Task DeleteAsync(string moduleId, string testSetId);
    Task MoveObjectiveAsync(
        string sourceModuleId, string sourceTestSetId,
        string destModuleId, string destTestSetId,
        string objective);
}
