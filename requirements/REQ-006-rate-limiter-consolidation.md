---
id: REQ-006
title: Consolidate per-user rate-limiters into a shared PerUserTokenBucket utility
status: Proposed
created: 2026-05-09
author: Kalhara Samarasinghe
area: webapi
---

# REQ-006 — Per-user rate-limiter consolidation

## Goal

Lift `DbDryRunRateLimiter` (REQ-002) and `EventAssertPeekRateLimiter` (REQ-004) into a single shared `PerUserTokenBucket<TKey>` utility, registered once in DI and reused by every endpoint that needs per-user request throttling. Keep the public API + behaviour identical so the existing 219-test suite stays green; this is a refactor, not a behavioural change.

## Why now

- Two near-verbatim copies of the same fixed-window token-bucket exist today (`src/AiTestCrew.WebApi/Services/DbDryRunRateLimiter.cs` and `src/AiTestCrew.WebApi/Services/EventAssertPeekRateLimiter.cs`). Phase 6 of REQ-004 explicitly flagged this as a TODO: "duplicate REQ-002's shape now; mark consolidation as follow-up."
- Every future endpoint that needs the same gate (run-trigger spam guard, recording-dispatch throttle, etc.) will either:
  - Add a third near-identical `*RateLimiter` — keeps doubling the surface for "fix one bug across N copies" maintenance, OR
  - Reuse one of the existing two ad-hoc — couples unrelated endpoints to the same bucket, leaks "DB dry-run" naming into unrelated paths.

  The third caller IS the right time to consolidate (the rule of three), and adding it later would be a strict drift — better to lift now while both call sites are fresh in head.
- `AgentHeartbeatMonitor.SweepRateLimiterIfDue` already sweeps both limiters independently every 5 minutes. After consolidation it iterates a single registry instead — cleaner.

## Scope — what's in

### 1. New `PerUserTokenBucket<TKey>` class

`src/AiTestCrew.WebApi/Services/PerUserTokenBucket.cs`. Generic over the key type so callers can use `string` (user id), `Guid`, IP address, or whatever else makes sense. Same API as the two existing classes:

```csharp
public sealed class PerUserTokenBucket<TKey> where TKey : notnull
{
    public const int DefaultMaxRequestsPerWindow = 10;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    public PerUserTokenBucket(
        string name,                              // for sweep telemetry
        int maxPerWindow = DefaultMaxRequestsPerWindow,
        TimeSpan? window = null);

    public bool TryAcquire(TKey userKey);
    public int Sweep(DateTime nowUtc);

    public string Name { get; }                   // "db-dry-run", "event-assert-peek"
}
```

Implementation: lifted verbatim from `DbDryRunRateLimiter` (the older of the two — `EventAssertPeekRateLimiter` was already a copy of it). The `name` argument is new — used by `AgentHeartbeatMonitor`'s sweep log line so the operator can tell which bucket dropped what.

### 2. Registry pattern for DI + janitor

Each consumer registers a uniquely-named instance:

```csharp
// In WebApi/Program.cs
builder.Services.AddSingleton<IPerUserTokenBucketRegistry, PerUserTokenBucketRegistry>();
builder.Services.AddSingleton(_ => new PerUserTokenBucket<string>("db-dry-run"));
builder.Services.AddSingleton(_ => new PerUserTokenBucket<string>("event-assert-peek"));
```

`IPerUserTokenBucketRegistry` is a thin `IEnumerable<PerUserTokenBucketBase>` wrapper that `AgentHeartbeatMonitor.SweepRateLimiterIfDue` iterates. Replaces the current "look up `DbDryRunRateLimiter` and `EventAssertPeekRateLimiter` separately" pattern.

### 3. Endpoint migrations

`DbCheckEndpoints.cs` and `EventAssertEndpoints.cs` change their constructor parameter from `DbDryRunRateLimiter` / `EventAssertPeekRateLimiter` to the named `PerUserTokenBucket<string>` resolved by `[FromKeyedServices("db-dry-run")]` (or whatever pattern the DI container supports).

Alternative: keep the two `*RateLimiter` types as thin wrappers that delegate to a shared bucket. Less mechanical change — endpoint code doesn't move at all — but the duplication stays visible in the type system. **Recommendation**: do the full migration so the two specialised types are deleted entirely; the consolidation is the point.

### 4. Janitor sweep extension

`AgentHeartbeatMonitor.SweepRateLimiterIfDue` becomes:

```csharp
private void SweepRateLimiterIfDue()
{
    if (DateTime.UtcNow - _lastRateLimiterSweepAt < TimeSpan.FromMinutes(5)) return;
    _lastRateLimiterSweepAt = DateTime.UtcNow;

    var registry = _sp.GetService<IPerUserTokenBucketRegistry>();
    if (registry is null) return;

    var now = DateTime.UtcNow;
    foreach (var bucket in registry)
    {
        try
        {
            var dropped = bucket.Sweep(now);
            if (dropped > 0)
                _logger.LogDebug("{Name} rate limiter swept — {Count} expired bucket(s).",
                    bucket.Name, dropped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Rate-limiter sweep failed for '{Name}'", bucket.Name);
        }
    }
}
```

One iteration replaces two hardcoded probes; failure of one bucket doesn't take down sweeps for the others.

## Scope — explicitly out

- **Distributed rate-limiting (Redis-backed, etc.)** — out. The current in-memory bucket is per-process; if AITestCrew ever runs behind a load balancer with sticky sessions disabled, this becomes inadequate. That's a separate REQ; the consolidation here is structurally compatible with a future `IRateLimiter` abstraction layer above the bucket.
- **Algorithmic change** — still fixed-window token bucket. Sliding window / leaky bucket / smoother backpressure is a separate concern. Keep behaviour identical.
- **Exposing rate-limit headers** (`X-RateLimit-Remaining`, `Retry-After`) — nice to have; out for v1. The endpoints currently respond `429 + JSON message`; that pattern is preserved.
- **Different rate-limit policies per env** — no consumer needs this today.

## Acceptance criteria

1. **`DbDryRunRateLimiter` and `EventAssertPeekRateLimiter` are deleted.** No code outside `PerUserTokenBucket.cs` carries the algorithm.
2. **All 219 existing tests pass.** `DbCheckEndpointsTests.cs` and `EventAssertEndpointsTests.cs` continue to validate the same 429 / 200 / sweep behaviour without modification (or only trivial modification — if they instantiated the old type directly, the test's `BuildHostAsync` updates to use the new bucket type).
3. **Janitor sweep telemetry now names the bucket.** `AgentHeartbeatMonitor` emits `"event-assert-peek rate limiter swept — 3 expired bucket(s)."` instead of the generic message.
4. **Dependency injection registration is single-point.** The two named `PerUserTokenBucket<string>` registrations in `Program.cs` use the same factory; future endpoints add a third line, not a third class.
5. **No new public API surface.** The lifted utility's `TryAcquire` / `Sweep` / `Name` / constructor signature are the only public members. Internal `Bucket` record-struct is fine to keep as it is.

## Files most likely touched

**New**
- `src/AiTestCrew.WebApi/Services/PerUserTokenBucket.cs`
- `src/AiTestCrew.WebApi/Services/IPerUserTokenBucketRegistry.cs` + impl

**Modified**
- `src/AiTestCrew.WebApi/Program.cs` — DI registrations.
- `src/AiTestCrew.WebApi/Endpoints/DbCheckEndpoints.cs` — constructor param type change.
- `src/AiTestCrew.WebApi/Endpoints/EventAssertEndpoints.cs` — same.
- `src/AiTestCrew.WebApi/Services/AgentHeartbeatMonitor.cs` — `SweepRateLimiterIfDue` rewrite.

**Deleted**
- `src/AiTestCrew.WebApi/Services/DbDryRunRateLimiter.cs`
- `src/AiTestCrew.WebApi/Services/EventAssertPeekRateLimiter.cs`

**Tests**
- `tests/AiTestCrew.WebApi.Tests/DbCheckEndpointsTests.cs` — `BuildHostAsync` parameter type change.
- `tests/AiTestCrew.WebApi.Tests/EventAssertEndpointsTests.cs` — same.
- (Optional) `tests/AiTestCrew.WebApi.Tests/PerUserTokenBucketTests.cs` — direct unit tests of the lifted utility (sweep math, `TryAcquire` window roll-forward, concurrent claim CAS loop).

## Open questions

1. **DI keying** — does the codebase already use keyed services (`AddKeyedSingleton`, `[FromKeyedServices]`)? If not, adding the dependency for two callers might be overkill; a small registry interface that consumers retrieve from `IServiceProvider.GetServices<PerUserTokenBucket<string>>()` and then pick by `.Name` is a fallback. Implementer's call.
2. **Rename**: `PerUserTokenBucket` is the most accurate name (the algorithm IS a fixed-window token bucket; the "per user" describes the partitioning). Consider whether `RateLimiter<TKey>` reads better even though it's less precise.
3. **Phase 1 reduction**: if the team prefers minimum churn, an even smaller refactor is "lift the algorithm into a private base class, leave the two derived `*RateLimiter` types as type-safe wrappers." Cost: zero call-site changes; benefit: half the consolidation. The full deletion of both types is recommended but a two-phase approach is fine.
