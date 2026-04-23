namespace AiTestCrew.WebApi.Services;

public interface IRunTracker
{
    RunStatus Create(string runId, string objective, string mode, string? testSetId);
    RunStatus? Get(string runId);
    void Complete(string runId, string testSetId);
    void MarkAwaitingVerification(string runId, string testSetId);
    void Fail(string runId, string error);
    bool HasActiveRun();
    bool HasActiveRunForTestSet(string testSetId);
    RunStatus? GetActiveRun();
}
