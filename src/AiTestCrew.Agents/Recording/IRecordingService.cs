namespace AiTestCrew.Agents.Recording;

/// <summary>
/// Executes an interactive recording session (browser or desktop) and persists the
/// captured steps back into the test set. Shared by CLI flows (--record, --record-setup,
/// --record-verification, --auth-setup) and the agent queue's recording JobKinds.
/// </summary>
public interface IRecordingService
{
    Task<RecordingResult> RecordCaseAsync(RecordCaseRequest request, CancellationToken ct = default);
    Task<RecordingResult> RecordSetupAsync(RecordSetupRequest request, CancellationToken ct = default);
    Task<RecordingResult> RecordVerificationAsync(RecordVerificationRequest request, CancellationToken ct = default);
    Task<RecordingResult> AuthSetupAsync(AuthSetupRequest request, CancellationToken ct = default);
}
