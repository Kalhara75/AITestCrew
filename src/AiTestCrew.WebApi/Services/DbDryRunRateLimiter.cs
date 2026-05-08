using System.Collections.Concurrent;

namespace AiTestCrew.WebApi.Services;

/// <summary>
/// Tiny per-user fixed-window token bucket guarding
/// <c>POST /api/db-check/dry-run</c> against accidental hammering from a buggy
/// "Try query" UI. 10 requests / minute / user; the 11th in a window returns 429.
///
/// Memory pressure is bounded by a 5-minute janitor sweep that drops users
/// whose window expired, mirroring the <c>AgentHeartbeatMonitor</c> pattern at
/// a longer cadence (rate-limit state doesn't need 30s precision).
/// </summary>
public sealed class DbDryRunRateLimiter
{
    public const int DefaultMaxRequestsPerWindow = 10;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;

    public DbDryRunRateLimiter(int maxPerWindow = DefaultMaxRequestsPerWindow, TimeSpan? window = null)
    {
        _maxPerWindow = maxPerWindow > 0 ? maxPerWindow : DefaultMaxRequestsPerWindow;
        _window = window ?? DefaultWindow;
    }

    /// <summary>
    /// Records one request from <paramref name="userKey"/> and returns true if
    /// it's allowed; false if the user has already used their quota in the
    /// current window. <paramref name="userKey"/> can be a user id, an api-key
    /// hash, or — when auth is disabled — the remote IP, whatever the caller
    /// has on hand for partitioning.
    /// </summary>
    public bool TryAcquire(string userKey)
    {
        var now = DateTime.UtcNow;
        while (true)
        {
            var bucket = _buckets.GetOrAdd(userKey, _ => new Bucket(now, 0));
            // Roll the window forward if we've crossed it.
            if (now - bucket.WindowStart >= _window)
            {
                var fresh = new Bucket(now, 1);
                if (_buckets.TryUpdate(userKey, fresh, bucket))
                    return true;
                // CAS lost — re-read.
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
    /// <c>AgentHeartbeatMonitor.SweepRateLimiterAsync</c> on a 5-minute cadence.
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
