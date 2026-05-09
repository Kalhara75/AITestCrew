using System.Collections.Concurrent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Tiny per-user fixed-window token bucket guarding
/// <c>POST /api/event-assert/peek</c> against accidental hammering from a buggy
/// "Peek messages" UI button. 10 requests / minute / user; the 11th in a window
/// returns 429.
///
/// <para>
/// Mirrors <see cref="DbDryRunRateLimiter"/>'s shape so the consolidation work
/// noted in the REQ-004 plan is purely a refactor — both buckets can lift into
/// a shared <c>PerUserTokenBucket&lt;TKey&gt;</c> utility once the third user
/// arrives. Memory pressure is bounded by a 5-minute janitor sweep (driven
/// from <c>AgentHeartbeatMonitor.SweepRateLimiterIfDue</c>).
/// </para>
/// </summary>
public sealed class EventAssertPeekRateLimiter
{
    public const int DefaultMaxRequestsPerWindow = 10;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;

    public EventAssertPeekRateLimiter(int maxPerWindow = DefaultMaxRequestsPerWindow, TimeSpan? window = null)
    {
        _maxPerWindow = maxPerWindow > 0 ? maxPerWindow : DefaultMaxRequestsPerWindow;
        _window = window ?? DefaultWindow;
    }

    public bool TryAcquire(string userKey)
    {
        var now = DateTime.UtcNow;
        while (true)
        {
            var bucket = _buckets.GetOrAdd(userKey, _ => new Bucket(now, 0));
            if (now - bucket.WindowStart >= _window)
            {
                var fresh = new Bucket(now, 1);
                if (_buckets.TryUpdate(userKey, fresh, bucket))
                    return true;
                continue;
            }
            if (bucket.Count >= _maxPerWindow)
                return false;
            var incremented = new Bucket(bucket.WindowStart, bucket.Count + 1);
            if (_buckets.TryUpdate(userKey, incremented, bucket))
                return true;
        }
    }

    /// <summary>
    /// Drops users whose window expired. Called periodically by
    /// <c>AgentHeartbeatMonitor.SweepRateLimiterIfDue</c> on a 5-minute cadence.
    /// </summary>
    public int Sweep(DateTime nowUtc)
    {
        var dropped = 0;
        foreach (var (key, bucket) in _buckets)
        {
            if (nowUtc - bucket.WindowStart >= _window
                && _buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket)))
            {
                dropped++;
            }
        }
        return dropped;
    }

    private readonly record struct Bucket(DateTime WindowStart, int Count);
}
