namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// Abstraction for execution run history storage.
/// </summary>
public interface IExecutionHistoryRepository
{
    Task SaveAsync(PersistedExecutionRun run);
    Task<PersistedExecutionRun?> GetRunAsync(string testSetId, string runId);
    IReadOnlyList<PersistedExecutionRun> ListRuns(string testSetId);
    PersistedExecutionRun? GetLatestRun(string testSetId);
    Dictionary<string, (PersistedObjectiveResult Result, string RunId)> GetLatestObjectiveStatuses(string testSetId);
    Task DeleteRunsForTestSetAsync(string testSetId);
    Task DeleteRunAsync(string testSetId, string runId);
    Task RemoveObjectiveFromHistoryAsync(string testSetId, string objectiveId);
    Task<Dictionary<string, string>?> GetLatestDeliveryContextAsync(
        string testSetId, string? moduleId, string objectiveId);
    int CountRuns(string testSetId);
}
