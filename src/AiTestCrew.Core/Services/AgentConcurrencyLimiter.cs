namespace AiTestCrew.Core.Services;

/// <summary>
/// Global concurrency limiter for agent execution.
/// Wraps a <see cref="SemaphoreSlim"/> bounded by <c>MaxParallelAgents</c>.
/// Registered as a singleton so the same semaphore is shared across the
/// orchestrator and all concurrent test-set runs.
/// </summary>
public sealed class AgentConcurrencyLimiter
{
    private readonly SemaphoreSlim _semaphore;

    public int MaxConcurrency { get; }

    public AgentConcurrencyLimiter(int maxConcurrency)
    {
        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public Task WaitAsync(CancellationToken ct = default) => _semaphore.WaitAsync(ct);
    public void Release() => _semaphore.Release();
    public int CurrentCount => _semaphore.CurrentCount;
}
