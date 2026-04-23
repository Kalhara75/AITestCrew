using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// HTTP-backed <see cref="IPendingVerificationRepository"/> used by the Runner in
/// agent mode against a remote WebApi. All state lives in the server DB; the agent
/// round-trips through REST so distributed deployments stay consistent.
/// </summary>
internal sealed class ApiClientPendingVerificationRepository : IPendingVerificationRepository
{
    private readonly RemoteHttpClient _http;

    public ApiClientPendingVerificationRepository(RemoteHttpClient http) => _http = http;

    public async Task InsertAsync(PendingVerification pending) =>
        await _http.PostAsync("api/pending-verifications", pending);

    public async Task<PendingVerification?> GetByIdAsync(string pendingId) =>
        await _http.GetAsync<PendingVerification>(
            $"api/pending-verifications/{Uri.EscapeDataString(pendingId)}");

    public async Task UpdateAttemptAsync(
        string pendingId, string newQueueEntryId, int attemptCount, string attemptLogJson) =>
        await _http.PostAsync(
            $"api/pending-verifications/{Uri.EscapeDataString(pendingId)}/attempt",
            new { newQueueEntryId, attemptCount, attemptLogJson });

    public async Task MarkCompletedAsync(string pendingId, string resultJson, string attemptLogJson) =>
        await _http.PostAsync(
            $"api/pending-verifications/{Uri.EscapeDataString(pendingId)}/complete",
            new { resultJson, attemptLogJson });

    public async Task MarkFailedAsync(string pendingId, string resultJson, string attemptLogJson) =>
        await _http.PostAsync(
            $"api/pending-verifications/{Uri.EscapeDataString(pendingId)}/fail",
            new { resultJson, attemptLogJson });

    public async Task<int> CountPendingForRunAsync(string parentRunId)
    {
        var resp = await _http.GetAsync<CountResponse>(
            $"api/pending-verifications/count?runId={Uri.EscapeDataString(parentRunId)}");
        return resp?.Count ?? 0;
    }

    public async Task<List<PendingVerification>> ListForRunAsync(string parentRunId)
    {
        var list = await _http.GetAsync<List<PendingVerification>>(
            $"api/pending-verifications/by-run/{Uri.EscapeDataString(parentRunId)}");
        return list ?? new();
    }

    // Server-side concerns — not reachable from the agent's execution path.
    public Task<int> CancelForRunAsync(string parentRunId) =>
        throw new NotSupportedException("Cancellation is driven from the dashboard against the server.");

    public Task<List<PendingVerification>> ListExpiredAsync(DateTime cutoffUtc) =>
        throw new NotSupportedException("Janitor runs server-side; not reachable from agent.");

    public Task<List<PendingVerification>> ListPendingAsync() =>
        throw new NotSupportedException("Dashboard-only; use the existing REST query directly.");

    private sealed record CountResponse(string RunId, int Count);
}
