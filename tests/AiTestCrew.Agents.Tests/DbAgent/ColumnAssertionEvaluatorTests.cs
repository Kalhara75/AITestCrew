using AiTestCrew.Agents.DbAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.DbAgent;

/// <summary>
/// Covers every operator across (string, numeric, date, NULL, JSON-extracted)
/// shapes plus the typical edge cases. Failure messages are spot-checked but
/// not pinned word-for-word — diagnostic strings are not part of the public
/// contract.
/// </summary>
public class ColumnAssertionEvaluatorTests
{
    // ── Equals / NotEquals / Contains / Starts / Ends ─────────────────

    [Fact]
    public void Equals_string_passes_with_case_insensitive_default()
    {
        var a = new ColumnAssertion { Column = "Status", Expected = "PROCESSED" };
        var r = ColumnAssertionEvaluator.Evaluate(a, "processed", isDbNull: false);
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Equals_string_respects_ignore_case_false()
    {
        var a = new ColumnAssertion { Column = "Status", Expected = "PROCESSED", IgnoreCase = false };
        ColumnAssertionEvaluator.Evaluate(a, "processed", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void NotEquals_passes_when_different()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.NotEquals, Expected = "y" };
        ColumnAssertionEvaluator.Evaluate(a, "z", isDbNull: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void Contains_substring_passes()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.Contains, Expected = "abc" };
        ColumnAssertionEvaluator.Evaluate(a, "ZZabcZZ", isDbNull: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void NotContains_passes_when_absent()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.NotContains, Expected = "abc" };
        ColumnAssertionEvaluator.Evaluate(a, "xyz", isDbNull: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void StartsWith_and_EndsWith_basic_path()
    {
        var s = new ColumnAssertion { Column = "X", Operator = AssertionOperator.StartsWith, Expected = "hello" };
        var e = new ColumnAssertion { Column = "X", Operator = AssertionOperator.EndsWith, Expected = "world" };
        ColumnAssertionEvaluator.Evaluate(s, "hello world", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(e, "hello world", isDbNull: false).Passed.Should().BeTrue();
    }

    // ── Regex ──────────────────────────────────────────────────────────

    [Fact]
    public void Regex_matches_pattern()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.Regex, Expected = @"^\d{3}-[A-Z]+$" };
        ColumnAssertionEvaluator.Evaluate(a, "123-ABC", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "12-ABC", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void Regex_invalid_pattern_fails_gracefully()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.Regex, Expected = "[unclosed" };
        var r = ColumnAssertionEvaluator.Evaluate(a, "anything", isDbNull: false);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("invalid");
    }

    // ── Comparisons (>, <, Between) ────────────────────────────────────

    [Fact]
    public void GreaterThan_numeric()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.GreaterThan, Expected = "10" };
        ColumnAssertionEvaluator.Evaluate(a, "20", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "5", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void LessThan_falls_back_to_date()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.LessThan, Expected = "2026-12-31" };
        ColumnAssertionEvaluator.Evaluate(a, "2026-01-01", isDbNull: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void Between_numeric_inclusive()
    {
        var a = new ColumnAssertion
        {
            Column = "X",
            Operator = AssertionOperator.Between,
            Expected = "10",
            Expected2 = "20",
        };
        ColumnAssertionEvaluator.Evaluate(a, "10", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "15", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "20", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "21", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void Between_requires_expected2()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.Between, Expected = "10" };
        var r = ColumnAssertionEvaluator.Evaluate(a, "12", isDbNull: false);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("Expected2");
    }

    // ── IsNull / IsNotNull ─────────────────────────────────────────────

    [Fact]
    public void IsNull_passes_only_on_null()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.IsNull };
        ColumnAssertionEvaluator.Evaluate(a, null, isDbNull: true).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "", isDbNull: false).Passed.Should().BeFalse();  // empty != NULL
        ColumnAssertionEvaluator.Evaluate(a, "x", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void IsNotNull_passes_on_non_null_including_empty_string()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.IsNotNull };
        ColumnAssertionEvaluator.Evaluate(a, "", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, null, isDbNull: true).Passed.Should().BeFalse();
    }

    [Fact]
    public void Equals_against_null_actual_fails_even_for_empty_expected()
    {
        var a = new ColumnAssertion { Column = "X", Expected = "" };
        ColumnAssertionEvaluator.Evaluate(a, null, isDbNull: true).Passed.Should().BeFalse();
    }

    // ── EqualsNumeric ──────────────────────────────────────────────────

    [Fact]
    public void EqualsNumeric_treats_zero_and_zero_dot_zero_as_equal()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.EqualsNumeric, Expected = "0" };
        ColumnAssertionEvaluator.Evaluate(a, "0.00", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, 0m, isDbNull: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void EqualsNumeric_with_tolerance()
    {
        var a = new ColumnAssertion
        {
            Column = "X",
            Operator = AssertionOperator.EqualsNumeric,
            Expected = "10",
            ToleranceDelta = 0.5m,
        };
        ColumnAssertionEvaluator.Evaluate(a, "10.4", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "10.6", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void EqualsNumeric_non_numeric_actual_fails_typed()
    {
        var a = new ColumnAssertion { Column = "X", Operator = AssertionOperator.EqualsNumeric, Expected = "10" };
        var r = ColumnAssertionEvaluator.Evaluate(a, "abc", isDbNull: false);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("not a number");
    }

    // ── EqualsDate ─────────────────────────────────────────────────────

    [Fact]
    public void EqualsDate_treats_simple_date_as_midnight_utc()
    {
        var a = new ColumnAssertion
        {
            Column = "X",
            Operator = AssertionOperator.EqualsDate,
            Expected = "2026-01-01",
        };
        ColumnAssertionEvaluator.Evaluate(a, "2026-01-01T00:00:00Z", isDbNull: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void EqualsDate_with_tolerance_seconds()
    {
        var a = new ColumnAssertion
        {
            Column = "X",
            Operator = AssertionOperator.EqualsDate,
            Expected = "2026-05-08T10:00:00Z",
            ToleranceSeconds = 60,
        };
        ColumnAssertionEvaluator.Evaluate(a, "2026-05-08T10:00:30Z", isDbNull: false).Passed.Should().BeTrue();
        ColumnAssertionEvaluator.Evaluate(a, "2026-05-08T10:02:00Z", isDbNull: false).Passed.Should().BeFalse();
    }

    [Fact]
    public void EqualsDate_handles_DateTime_actual()
    {
        var a = new ColumnAssertion
        {
            Column = "X",
            Operator = AssertionOperator.EqualsDate,
            Expected = "2026-05-08T10:00:00Z",
        };
        var actual = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc);
        ColumnAssertionEvaluator.Evaluate(a, actual, isDbNull: false).Passed.Should().BeTrue();
    }

    // ── JSON path ──────────────────────────────────────────────────────

    [Fact]
    public void JsonPath_extracts_scalar_and_compares()
    {
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.OrderId",
            Expected = "12345",
        };
        var r = ColumnAssertionEvaluator.Evaluate(a, """{"OrderId":"12345","Other":1}""", isDbNull: false);
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void JsonPath_path_not_found_fails_with_typed_reason()
    {
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.Missing",
            Expected = "x",
        };
        var r = ColumnAssertionEvaluator.Evaluate(a, """{"OrderId":"1"}""", isDbNull: false);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("$.Missing").And.Contain("not found");
    }

    [Fact]
    public void JsonPath_column_not_json_fails_with_typed_reason()
    {
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.X",
            Expected = "x",
        };
        var r = ColumnAssertionEvaluator.Evaluate(a, "not json", isDbNull: false);
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("not JSON");
    }

    [Fact]
    public void JsonPath_with_token_containing_apostrophe_in_expected_doesnt_break_path()
    {
        // Sanity check that an Expected containing characters that look like JSON
        // string delimiters doesn't trip the path machinery — Expected goes through
        // the operator, NOT through the path parser.
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.Note",
            Expected = "O'Brien's note",
        };
        var r = ColumnAssertionEvaluator.Evaluate(a, """{"Note":"O'Brien's note"}""", isDbNull: false);
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void JsonPath_resolves_to_json_null_passes_IsNull()
    {
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.OrderId",
            Operator = AssertionOperator.IsNull,
        };
        var r = ColumnAssertionEvaluator.Evaluate(a, """{"OrderId":null}""", isDbNull: false);
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void JsonPath_missing_path_fails_IsNotNull()
    {
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.Missing",
            Operator = AssertionOperator.IsNotNull,
        };
        var r = ColumnAssertionEvaluator.Evaluate(a, """{"OrderId":"1"}""", isDbNull: false);
        r.Passed.Should().BeFalse();
    }

    [Fact]
    public void JsonPath_into_indexed_array()
    {
        var a = new ColumnAssertion
        {
            Column = "Payload",
            JsonPath = "$.Items[0].Code",
            Expected = "ABC",
        };
        var r = ColumnAssertionEvaluator.Evaluate(a,
            """{"Items":[{"Code":"ABC"},{"Code":"DEF"}]}""", isDbNull: false);
        r.Passed.Should().BeTrue();
    }
}
