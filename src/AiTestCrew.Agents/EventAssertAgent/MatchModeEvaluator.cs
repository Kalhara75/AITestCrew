namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Folds a per-message pass/fail vector into an overall verdict for an
/// event-assert post-step, given the configured <see cref="MatchMode"/> and
/// (where applicable) <c>ExpectedCount</c> / <c>MaxCount</c> bounds.
///
/// Pure — no side effects, no dependencies on Azure SDK or persistence types.
/// Trivially unit-testable across the boundary cases listed in REQ-004 §12
/// (AnyMessage with mixed pass/fail, AllMessages with empty / all-pass /
/// pass+fail, ExactlyOne, count-bounded modes including the negative-assertion
/// shape <c>MaxCount(0)</c>).
/// </summary>
public static class MatchModeEvaluator
{
    public readonly record struct Result(bool Passed, string Reason);

    public static Result Evaluate(
        MatchMode mode,
        int totalReceived,
        int passCount,
        int? expectedCount,
        int? maxCount)
    {
        switch (mode)
        {
            case MatchMode.AnyMessage:
                return passCount > 0
                    ? new Result(true,
                        $"{passCount} of {totalReceived} message(s) matched all criteria")
                    : new Result(false,
                        $"no message matched all criteria (received {totalReceived})");

            case MatchMode.AllMessages:
                if (totalReceived == 0)
                    return new Result(false,
                        "no messages received within the timeout window — AllMessages requires at least one matching message");
                return passCount == totalReceived
                    ? new Result(true,
                        $"all {totalReceived} message(s) matched every criterion")
                    : new Result(false,
                        $"{passCount} of {totalReceived} message(s) matched; AllMessages requires all to match");

            case MatchMode.ExactlyOne:
                return passCount == 1
                    ? new Result(true,
                        $"exactly one of {totalReceived} message(s) matched all criteria")
                    : new Result(false,
                        $"expected exactly one match, got {passCount} (received {totalReceived})");

            case MatchMode.ExactCount:
                {
                    var expected = expectedCount ?? -1;
                    if (expected < 0)
                        return new Result(false,
                            "ExactCount requires ExpectedCount to be set");
                    return passCount == expected
                        ? new Result(true,
                            $"got exactly {expected} matching message(s) of {totalReceived} received")
                        : new Result(false,
                            $"expected exactly {expected} matching message(s), got {passCount} (received {totalReceived})");
                }

            case MatchMode.MinCount:
                {
                    var min = expectedCount ?? -1;
                    if (min < 0)
                        return new Result(false,
                            "MinCount requires ExpectedCount to be set");
                    return passCount >= min
                        ? new Result(true,
                            $"got {passCount} matching message(s) (min {min}) of {totalReceived} received")
                        : new Result(false,
                            $"expected at least {min} matching message(s), got {passCount} (received {totalReceived})");
                }

            case MatchMode.MaxCount:
                {
                    var max = expectedCount ?? -1;
                    if (max < 0)
                        return new Result(false,
                            "MaxCount requires ExpectedCount to be set");
                    return passCount <= max
                        ? new Result(true,
                            max == 0
                                ? $"got 0 matching message(s) of {totalReceived} received (negative assertion satisfied)"
                                : $"got {passCount} matching message(s) (max {max}) of {totalReceived} received")
                        : new Result(false,
                            $"expected at most {max} matching message(s), got {passCount} (received {totalReceived})");
                }

            case MatchMode.CountRange:
                {
                    var lo = expectedCount ?? -1;
                    var hi = maxCount ?? -1;
                    if (lo < 0 || hi < 0 || hi < lo)
                        return new Result(false,
                            "CountRange requires ExpectedCount (lower) and MaxCount (upper); MaxCount must be >= ExpectedCount");
                    return passCount >= lo && passCount <= hi
                        ? new Result(true,
                            $"got {passCount} matching message(s) in [{lo}, {hi}] of {totalReceived} received")
                        : new Result(false,
                            $"expected between {lo} and {hi} matching message(s), got {passCount} (received {totalReceived})");
                }

            default:
                return new Result(false, $"unsupported match mode '{mode}'");
        }
    }

    /// <summary>
    /// Returns true when the receive loop can stop early — i.e. additional
    /// messages cannot change the verdict. <see cref="MatchMode.AllMessages"/>
    /// can short-circuit on the first failing message; modes with an upper
    /// bound (<see cref="MatchMode.MaxCount"/>, <see cref="MatchMode.ExactlyOne"/>,
    /// <see cref="MatchMode.ExactCount"/>, <see cref="MatchMode.CountRange"/>)
    /// short-circuit when the bound is exceeded.
    ///
    /// <see cref="MatchMode.AnyMessage"/> short-circuits on the first pass.
    /// <see cref="MatchMode.MinCount"/> short-circuits when the floor is reached.
    ///
    /// Negative-assertion shapes (<see cref="MatchMode.MaxCount"/> with
    /// <c>ExpectedCount=0</c>) NEVER short-circuit on the no-match-yet path —
    /// the loop must run the full timeout to actually verify zero arrived.
    /// </summary>
    public static bool CanShortCircuit(
        MatchMode mode,
        int totalReceived,
        int passCount,
        int? expectedCount,
        int? maxCount)
    {
        switch (mode)
        {
            case MatchMode.AnyMessage:
                return passCount >= 1;

            case MatchMode.AllMessages:
                // Stop only on first failing message.
                return totalReceived > 0 && passCount < totalReceived;

            case MatchMode.ExactlyOne:
                // Past 1 we can never recover; on hitting 1 we can't stop yet
                // because a 2nd would invalidate the pass.
                return passCount > 1;

            case MatchMode.ExactCount:
                // Same logic — we can stop once the upper bound is exceeded.
                return expectedCount is int e && passCount > e;

            case MatchMode.MinCount:
                return expectedCount is int min && passCount >= min;

            case MatchMode.MaxCount:
                // Stop on overshoot only; never short-circuit "still at zero" because
                // the loop must run the full timeout for the negative-assertion shape.
                return expectedCount is int max && passCount > max;

            case MatchMode.CountRange:
                return maxCount is int hi && passCount > hi;

            default:
                return false;
        }
    }
}
