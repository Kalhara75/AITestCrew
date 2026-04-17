namespace AiTestCrew.WebApi.Services;

public interface IModuleRunTracker
{
    ModuleRunStatus Create(string moduleRunId, string moduleId, string moduleName,
        List<TestSetRunProgress> testSets);
    ModuleRunStatus? Get(string moduleRunId);
    ModuleRunStatus? GetByModuleId(string moduleId);
    void AdvanceToTestSet(string moduleRunId, string testSetId, string childRunId);
    void CompleteTestSet(string moduleRunId, string testSetId, bool success, string? error);
    void Complete(string moduleRunId);
    void Fail(string moduleRunId, string error);
    bool HasActiveModuleRun();
    bool HasActiveModuleRunForModule(string moduleId);
    ModuleRunStatus? GetActiveRun();
}
