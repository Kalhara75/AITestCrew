using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Runner.RemoteRepositories;

/// <summary>
/// HTTP-backed <see cref="IRunQueueRepository"/> used by the Runner in agent mode
/// against a remote WebApi. Only the methods the delivery agent invokes are wired;
/// dashboard/admin operations (list / cancel / claim) stay server-side.
///
/// The claim loop (GET /api/queue/next) is NOT on this interface — the agent polls
/// that endpoint through <c>AgentClient</c>. What lives here is the set of
/// operations the <c>AseXmlDeliveryAgent</c> performs WHILE running a job: enqueue
/// a deferred follow-up, plus whatever the orchestrator might query.
/// </summary>
internal sealed class ApiClientRunQueueRepository : IRunQueueRepository
{
    private readonly RemoteHttpClient _http;

    public ApiClientRunQueueRepository(RemoteHttpClient http) => _http = http;

    public async Task<RunQueueEntry> EnqueueAsync(RunQueueEntry entry)
    {
        // The server-side POST /api/queue responds with { id } for the newly created
        // row. We don't need the body back — the entry we enqueue is already complete.
        await _http.PostAsync("api/queue", entry);
        return entry;
    }

    public async Task<RunQueueEntry?> GetByIdAsync(string id) =>
        await _http.GetAsync<RunQueueEntry>($"api/queue/{Uri.EscapeDataString(id)}");

    // ── Not reachable from the agent's hot path; keep these unimplemented so the
    //    first accidental call surfaces immediately rather than silently no-opping. ──

    public Task<RunQueueEntry?> ClaimNextAsync(string agentId, IEnumerable<string> capabilities) =>
        throw new NotSupportedException(
            "ClaimNextAsync is server-authoritative; the agent polls GET /api/queue/next via AgentClient.");

    public Task MarkRunningAsync(string id) =>
        throw new NotSupportedException("Use AgentClient.ReportProgressAsync — agent-side.");

    public Task MarkCompletedAsync(string id, bool success, string? error) =>
        throw new NotSupportedException("Use AgentClient.ReportResultAsync — agent-side.");

    public Task<bool> CancelAsync(string id) =>
        throw new NotSupportedException("Cancellation is driven from the dashboard against the server.");

    public Task<int> CancelPendingForRunAsync(string parentRunId) =>
        throw new NotSupportedException("Cancellation is driven from the dashboard against the server.");

    public Task<List<RunQueueEntry>> ListRecentAsync(int max = 50) =>
        throw new NotSupportedException("Dashboard-only; use the existing queue API directly.");

    public Task<RunQueueEntry?> GetActiveForAgentAsync(string agentId) =>
        throw new NotSupportedException("Server-side janitor path; not reachable from agent.");

    public Task<List<RunQueueEntry>> ListStaleClaimsAsync(TimeSpan staleAfter) =>
        throw new NotSupportedException("Server-side janitor path; not reachable from agent.");

    public Task<bool> ReleaseClaimAsync(string id) =>
        throw new NotSupportedException("Server-side janitor path; not reachable from agent.");
}
