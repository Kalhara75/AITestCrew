using AiTestCrew.Core.Models;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Abstraction for the distributed run queue. Dashboard triggers enqueue jobs here;
/// agents poll <see cref="ClaimNextAsync"/>, execute locally, then report back.
/// </summary>
public interface IRunQueueRepository
{
    /// <summary>Enqueues a new job. Returns the inserted entry (with <c>Status = "Queued"</c>).</summary>
    Task<RunQueueEntry> EnqueueAsync(RunQueueEntry entry);

    /// <summary>
    /// Atomically claims the oldest Queued job whose target type is in <paramref name="capabilities"/>,
    /// marking it Claimed by <paramref name="agentId"/>. Returns the claimed entry or null if none match.
    /// </summary>
    Task<RunQueueEntry?> ClaimNextAsync(string agentId, IEnumerable<string> capabilities);

    Task<RunQueueEntry?> GetByIdAsync(string id);

    /// <summary>Flips Claimed → Running when the agent starts executing.</summary>
    Task MarkRunningAsync(string id);

    /// <summary>Terminal state — Completed or Failed + optional error message.</summary>
    Task MarkCompletedAsync(string id, bool success, string? error);

    /// <summary>User cancelled a Queued job before an agent claimed it. No-op if already Claimed.</summary>
    Task<bool> CancelAsync(string id);

    /// <summary>Active jobs (Queued/Claimed/Running) + recent terminal jobs, newest first.</summary>
    Task<List<RunQueueEntry>> ListRecentAsync(int max = 50);

    /// <summary>The currently-claimed job (Claimed or Running) for an agent, if any.</summary>
    Task<RunQueueEntry?> GetActiveForAgentAsync(string agentId);
}
