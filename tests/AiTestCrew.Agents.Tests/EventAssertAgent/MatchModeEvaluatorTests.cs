using AiTestCrew.Agents.EventAssertAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

public class MatchModeEvaluatorTests
{
    [Theory]
    [InlineData(0, 0, false)]   // empty
    [InlineData(3, 0, false)]   // none matched
    [InlineData(3, 1, true)]    // one matched
    [InlineData(3, 3, true)]    // all matched
    public void AnyMessage(int total, int passed, bool expected)
    {
        MatchModeEvaluator.Evaluate(MatchMode.AnyMessage, total, passed, null, null)
            .Passed.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0, false)]   // empty fails
    [InlineData(2, 1, false)]   // partial fails
    [InlineData(3, 3, true)]    // all pass
    public void AllMessages(int total, int passed, bool expected)
    {
        MatchModeEvaluator.Evaluate(MatchMode.AllMessages, total, passed, null, null)
            .Passed.Should().Be(expected);
    }

    [Theory]
    [InlineData(2, 1, true)]    // exactly one matched
    [InlineData(2, 2, false)]   // two matched fails
    [InlineData(2, 0, false)]   // none matched fails
    public void ExactlyOne(int total, int passed, bool expected)
    {
        MatchModeEvaluator.Evaluate(MatchMode.ExactlyOne, total, passed, null, null)
            .Passed.Should().Be(expected);
    }

    [Theory]
    [InlineData(5, 2, 2, true)]
    [InlineData(5, 3, 2, false)]
    [InlineData(5, 1, 2, false)]
    public void ExactCount(int total, int passed, int expected, bool shouldPass)
    {
        MatchModeEvaluator.Evaluate(MatchMode.ExactCount, total, passed, expected, null)
            .Passed.Should().Be(shouldPass);
    }

    [Fact]
    public void ExactCount_requires_expectedCount()
    {
        MatchModeEvaluator.Evaluate(MatchMode.ExactCount, 5, 2, null, null)
            .Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 2, 2, true)]
    [InlineData(5, 3, 2, true)]
    [InlineData(5, 1, 2, false)]
    public void MinCount(int total, int passed, int min, bool shouldPass)
    {
        MatchModeEvaluator.Evaluate(MatchMode.MinCount, total, passed, min, null)
            .Passed.Should().Be(shouldPass);
    }

    [Theory]
    [InlineData(5, 2, 2, true)]
    [InlineData(5, 1, 2, true)]
    [InlineData(5, 3, 2, false)]
    public void MaxCount(int total, int passed, int max, bool shouldPass)
    {
        MatchModeEvaluator.Evaluate(MatchMode.MaxCount, total, passed, max, null)
            .Passed.Should().Be(shouldPass);
    }

    [Fact]
    public void MaxCount_zero_is_negative_assertion_shape()
    {
        // "verify NO matching event was raised" — pass when zero matched.
        MatchModeEvaluator.Evaluate(MatchMode.MaxCount, 5, 0, 0, null).Passed.Should().BeTrue();
        MatchModeEvaluator.Evaluate(MatchMode.MaxCount, 5, 1, 0, null).Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 2, 1, 3, true)]
    [InlineData(5, 0, 1, 3, false)]
    [InlineData(5, 4, 1, 3, false)]
    [InlineData(5, 1, 1, 3, true)]   // boundary
    [InlineData(5, 3, 1, 3, true)]   // boundary
    public void CountRange(int total, int passed, int lo, int hi, bool shouldPass)
    {
        MatchModeEvaluator.Evaluate(MatchMode.CountRange, total, passed, lo, hi)
            .Passed.Should().Be(shouldPass);
    }

    [Fact]
    public void CountRange_invalid_bounds_fails()
    {
        // hi < lo
        MatchModeEvaluator.Evaluate(MatchMode.CountRange, 5, 2, 5, 1)
            .Passed.Should().BeFalse();
        // missing bounds
        MatchModeEvaluator.Evaluate(MatchMode.CountRange, 5, 2, null, 3)
            .Passed.Should().BeFalse();
        MatchModeEvaluator.Evaluate(MatchMode.CountRange, 5, 2, 1, null)
            .Passed.Should().BeFalse();
    }

    // ── Short-circuit semantics ─────────────────────────────────────────

    [Fact]
    public void AnyMessage_short_circuits_on_first_pass()
    {
        MatchModeEvaluator.CanShortCircuit(MatchMode.AnyMessage, 1, 1, null, null)
            .Should().BeTrue();
        MatchModeEvaluator.CanShortCircuit(MatchMode.AnyMessage, 5, 0, null, null)
            .Should().BeFalse();
    }

    [Fact]
    public void AllMessages_short_circuits_on_first_fail()
    {
        // total=2, passed=1 → one already failed
        MatchModeEvaluator.CanShortCircuit(MatchMode.AllMessages, 2, 1, null, null)
            .Should().BeTrue();
        // total=0 (nothing yet) → no short-circuit
        MatchModeEvaluator.CanShortCircuit(MatchMode.AllMessages, 0, 0, null, null)
            .Should().BeFalse();
    }

    [Fact]
    public void MaxCount_zero_does_not_short_circuit_when_still_at_zero()
    {
        // Negative-assertion shape — must run the full timeout to verify zero arrived.
        MatchModeEvaluator.CanShortCircuit(MatchMode.MaxCount, 5, 0, 0, null)
            .Should().BeFalse();
        // But the moment one passes, we can short-circuit (verdict already failed).
        MatchModeEvaluator.CanShortCircuit(MatchMode.MaxCount, 5, 1, 0, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MinCount_short_circuits_when_floor_reached()
    {
        MatchModeEvaluator.CanShortCircuit(MatchMode.MinCount, 5, 3, 3, null)
            .Should().BeTrue();
        MatchModeEvaluator.CanShortCircuit(MatchMode.MinCount, 5, 2, 3, null)
            .Should().BeFalse();
    }

    [Fact]
    public void ExactlyOne_short_circuits_only_on_overshoot()
    {
        // 1 → can't short-circuit yet (a 2nd match would invalidate)
        MatchModeEvaluator.CanShortCircuit(MatchMode.ExactlyOne, 5, 1, null, null)
            .Should().BeFalse();
        // 2 → already invalid, stop
        MatchModeEvaluator.CanShortCircuit(MatchMode.ExactlyOne, 5, 2, null, null)
            .Should().BeTrue();
    }
}
