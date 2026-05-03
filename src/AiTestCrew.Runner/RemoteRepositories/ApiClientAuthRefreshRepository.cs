using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// HTTP-backed <see cref="IAuthRefreshRepository"/> used by the Runner in agent
/// mode against a remote WebApi. The agent only ever needs to register an auth
/// refresh and (rarely) report its outcome — janitor sweeps, listing, and
/// dashboard-driven start are server-side concerns.
/// </summary>
internal sealed class ApiClientAuthRefreshRepository : IAuthRefreshRepository
{
    private readonly RemoteHttpClient _http;

    public ApiClientAuthRefreshRepository(RemoteHttpClient http) => _http = http;

    public async Task<AuthRefreshRequest> InsertOrJoinAsync(AuthRefreshRequest request)
    {
        var resp = await _http.PostAsync<AuthRefreshRequest, AuthRefreshRequest>("api/auth-refreshes", request);
        return resp ?? request;
    }

    public async Task<AuthRefreshRequest?> GetByIdAsync(string id) =>
        await _http.GetAsync<AuthRefreshRequest>(
            $"api/auth-refreshes/{Uri.EscapeDataString(id)}");

    public async Task MarkCompletedAsync(string id) =>
        await _http.PostAsync(
            $"api/auth-refreshes/{Uri.EscapeDataString(id)}/complete", new { });

    public async Task MarkFailedAsync(string id, string errorMessage) =>
        await _http.PostAsync(
            $"api/auth-refreshes/{Uri.EscapeDataString(id)}/fail", new { error = errorMessage });

    // Server-side concerns — not reachable from the agent's execution path.
    public Task<List<AuthRefreshRequest>> ListActiveAsync() =>
        throw new NotSupportedException("Dashboard-only — call /api/auth-refreshes/active directly.");

    public Task MarkInProgressAsync(string id) =>
        throw new NotSupportedException("Driven by /start endpoint server-side.");

    public Task<bool> CancelAsync(string id) =>
        throw new NotSupportedException("Cancellation is driven from the dashboard.");

    public Task<List<AuthRefreshRequest>> ListStaleInProgressAsync(DateTime cutoffUtc) =>
        throw new NotSupportedException("Janitor runs server-side; not reachable from agent.");

    public Task<List<AuthRefreshRequest>> ListRecentlyCompletedAsync(DateTime sinceUtc) =>
        throw new NotSupportedException("Janitor runs server-side; not reachable from agent.");
}
