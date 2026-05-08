using AiTestCrew.WebApi.Services;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.WebApi.Tests;

public class DbDryRunRateLimiterTests
{
    [Fact]
    public void Allows_up_to_max_requests_in_window()
    {
        var limiter = new DbDryRunRateLimiter(maxPerWindow: 3, window: TimeSpan.FromMinutes(1));
        for (var i = 0; i < 3; i++)
            limiter.TryAcquire("user1").Should().BeTrue($"request {i + 1} of 3 should succeed");
        limiter.TryAcquire("user1").Should().BeFalse("4th request in same window must be denied");
    }

    [Fact]
    public void Other_users_unaffected_by_one_users_quota()
    {
        var limiter = new DbDryRunRateLimiter(maxPerWindow: 2, window: TimeSpan.FromMinutes(1));
        limiter.TryAcquire("user1").Should().BeTrue();
        limiter.TryAcquire("user1").Should().BeTrue();
        limiter.TryAcquire("user1").Should().BeFalse();
        limiter.TryAcquire("user2").Should().BeTrue();
        limiter.TryAcquire("user2").Should().BeTrue();
    }

    [Fact]
    public void Window_rolls_over_after_expiry()
    {
        // Use a tiny window so the test can sleep through it without flake risk.
        var limiter = new DbDryRunRateLimiter(maxPerWindow: 1, window: TimeSpan.FromMilliseconds(100));
        limiter.TryAcquire("user1").Should().BeTrue();
        limiter.TryAcquire("user1").Should().BeFalse();
        Thread.Sleep(150);
        limiter.TryAcquire("user1").Should().BeTrue();
    }

    [Fact]
    public void Sweep_drops_expired_buckets()
    {
        var limiter = new DbDryRunRateLimiter(maxPerWindow: 5, window: TimeSpan.FromMilliseconds(50));
        limiter.TryAcquire("user1").Should().BeTrue();
        limiter.TryAcquire("user2").Should().BeTrue();
        Thread.Sleep(100);
        var dropped = limiter.Sweep(DateTime.UtcNow);
        dropped.Should().Be(2);
    }
}
