using AiTestCrew.Agents.Common;
using AiTestCrew.Agents.DbAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.Common;

/// <summary>
/// Direct tests of the lifted operator-dispatch helper (REQ-004 §1.1).
/// REQ-002's ColumnAssertionEvaluatorTests already exercise this transitively
/// through the column path; these tests lock the helper's contract so a future
/// refactor that breaks event-assert callers (different scalar source) doesn't
/// hide behind ColumnAssertion-shaped inputs.
///
/// Coverage focuses on inputs the column path can't produce (e.g. JSON-extracted
/// scalars projected from a Body.* / BodyXml.* path on a Service Bus message —
/// the source has no concept of a column ordinal, JSONPath is resolved by the
/// caller, and the helper just sees a string + isNull flag).
/// </summary>
public class ScalarOperatorEvaluatorTests
{
    [Fact]
    public void Equals_string_passes_with_case_insensitive_default()
    {
        var r = ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Equals, "field", "PROCESSED", isNull: false,
            "processed", expected2: null, ignoreCase: true,
            toleranceSeconds: null, toleranceDelta: null);
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Equals_respects_ignoreCase_false()
    {
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Equals, "field", "PROCESSED", isNull: false,
            "processed", null, ignoreCase: false, null, null)
            .Passed.Should().BeFalse();
    }

    [Fact]
    public void NULL_actual_only_passes_IsNull()
    {
        // Null-aware short-circuit. Mirrors REQ-002's contract — null never
        // equals "", contains anything, or is greater/less than a value.
        var ops = new[]
        {
            AssertionOperator.Equals, AssertionOperator.NotEquals,
            AssertionOperator.Contains, AssertionOperator.NotContains,
            AssertionOperator.StartsWith, AssertionOperator.EndsWith,
            AssertionOperator.Regex, AssertionOperator.GreaterThan,
            AssertionOperator.LessThan, AssertionOperator.Between,
            AssertionOperator.EqualsNumeric, AssertionOperator.EqualsDate,
        };
        foreach (var op in ops)
        {
            ScalarOperatorEvaluator.Evaluate(
                op, "field", actual: null, isNull: true,
                "anything", expected2: "x", ignoreCase: true,
                toleranceSeconds: 1, toleranceDelta: 0.1m)
                .Passed.Should().BeFalse($"op {op} must fail on a null actual");
        }

        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.IsNull, "field", null, isNull: true,
            "", null, true, null, null)
            .Passed.Should().BeTrue();
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.IsNotNull, "field", null, isNull: true,
            "", null, true, null, null)
            .Passed.Should().BeFalse();
    }

    [Fact]
    public void IsNull_fails_on_empty_string()
    {
        // Empty != NULL. Same fidelity REQ-002 locked in.
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.IsNull, "field", "", isNull: false,
            "", null, true, null, null)
            .Passed.Should().BeFalse();
    }

    [Fact]
    public void GreaterThan_falls_back_from_decimal_to_date()
    {
        // ISO-8601 timestamps compare as DateTimeOffset when neither side parses as a number.
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.GreaterThan, "field",
            "2026-05-09T10:00:00Z", isNull: false,
            "2026-05-09T09:00:00Z", null, true, null, null)
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void Between_handles_dates_at_boundaries()
    {
        var r1 = ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Between, "field",
            "2026-05-09T10:00:00Z", isNull: false,
            expected: "2026-05-09T09:00:00Z", expected2: "2026-05-09T11:00:00Z",
            ignoreCase: true, null, null);
        r1.Passed.Should().BeTrue();

        var r2 = ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Between, "field",
            "2026-05-09T12:00:00Z", isNull: false,
            "2026-05-09T09:00:00Z", "2026-05-09T11:00:00Z", true, null, null);
        r2.Passed.Should().BeFalse();
    }

    [Fact]
    public void EqualsNumeric_honours_toleranceDelta()
    {
        // Tolerance ±0.05 should accept 100.04 vs 100.00.
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.EqualsNumeric, "field",
            "100.04", isNull: false,
            "100.00", null, true, null, toleranceDelta: 0.05m)
            .Passed.Should().BeTrue();
        // Outside tolerance.
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.EqualsNumeric, "field",
            "100.10", isNull: false,
            "100.00", null, true, null, 0.05m)
            .Passed.Should().BeFalse();
    }

    [Fact]
    public void EqualsDate_honours_toleranceSeconds()
    {
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.EqualsDate, "field",
            "2026-05-09T10:00:03Z", isNull: false,
            "2026-05-09T10:00:00Z", null, true, toleranceSeconds: 5, null)
            .Passed.Should().BeTrue();
        ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.EqualsDate, "field",
            "2026-05-09T10:00:10Z", isNull: false,
            "2026-05-09T10:00:00Z", null, true, 5, null)
            .Passed.Should().BeFalse();
    }

    [Fact]
    public void Regex_invalid_pattern_fails_gracefully()
    {
        var r = ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Regex, "field", "anything", false,
            "[unclosed", null, true, null, null);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("invalid");
    }

    [Fact]
    public void Reason_includes_field_label()
    {
        // Diagnostics include the caller-supplied label so failures from
        // event-assert criteria surface "Body.OrderId" rather than a generic
        // column name. This is the difference vs. ColumnAssertionEvaluator's
        // hardcoded "Column.JsonPath" labelling.
        var r = ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Equals, "Body.OrderId", "actual-value", false,
            "expected-value", null, true, null, null);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("Body.OrderId");
    }

    [Fact]
    public void Between_requires_expected2()
    {
        var r = ScalarOperatorEvaluator.Evaluate(
            AssertionOperator.Between, "field", "5", false,
            "1", expected2: null, ignoreCase: true, null, null);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("Expected2");
    }
}
