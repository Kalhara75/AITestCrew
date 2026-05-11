using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.DbAgent;
using FluentAssertions;
using Xunit;

namespace AiTestCrew.Agents.Tests.ApiAgent;

/// <summary>
/// Covers <see cref="ApiAssertionEvaluator.Evaluate"/> across all four source
/// types (Status, Header, Body, BodyText) and representative operators.
/// </summary>
public class ApiAssertionEvaluatorTests
{
    private static readonly Dictionary<string, IEnumerable<string>> EmptyHeaders = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IEnumerable<string>> Headers(string key, string value) =>
        new(StringComparer.OrdinalIgnoreCase) { [key] = new[] { value } };

    // ── Status source ─────────────────────────────────────────────────────────────

    [Fact]
    public void Status_Equals_passes_when_code_matches()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Status, Operator = AssertionOperator.Equals, Expected = "200" };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "").Passed.Should().BeTrue();
    }

    [Fact]
    public void Status_Equals_fails_when_code_does_not_match()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Status, Operator = AssertionOperator.Equals, Expected = "200" };
        var r = ApiAssertionEvaluator.Evaluate(a, 404, EmptyHeaders, "");
        r.Passed.Should().BeFalse();
        r.Actual.Should().Be("404");
    }

    [Fact]
    public void Status_Between_passes_for_2xx_range()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Status, Operator = AssertionOperator.Between, Expected = "200", Expected2 = "299" };
        ApiAssertionEvaluator.Evaluate(a, 201, EmptyHeaders, "").Passed.Should().BeTrue();
    }

    // ── Header source ─────────────────────────────────────────────────────────────

    [Fact]
    public void Header_Equals_passes_case_insensitive_header_name()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Header, HeaderName = "content-type", Operator = AssertionOperator.Contains, Expected = "json" };
        var r = ApiAssertionEvaluator.Evaluate(a, 200, Headers("Content-Type", "application/json"), "");
        r.Passed.Should().BeTrue();
    }

    [Fact]
    public void Header_IsNull_passes_when_header_missing()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Header, HeaderName = "x-request-id", Operator = AssertionOperator.IsNull };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "").Passed.Should().BeTrue();
    }

    [Fact]
    public void Header_IsNotNull_fails_when_header_missing()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Header, HeaderName = "x-request-id", Operator = AssertionOperator.IsNotNull };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "").Passed.Should().BeFalse();
    }

    // ── Body (JSONPath) source ────────────────────────────────────────────────────

    [Fact]
    public void Body_JsonPath_Equals_passes_for_nested_value()
    {
        var body = """{"data":{"id":42,"name":"test"}}""";
        var a = new ApiAssertion { Source = ApiAssertionSource.Body, JsonPath = "$.data.id", Operator = AssertionOperator.EqualsNumeric, Expected = "42" };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, body).Passed.Should().BeTrue();
    }

    [Fact]
    public void Body_JsonPath_fails_for_non_json_body()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.Body, JsonPath = "$.id", Operator = AssertionOperator.Equals, Expected = "1" };
        var r = ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "plain text");
        r.Passed.Should().BeFalse();
        r.Reason.Should().Contain("not JSON");
    }

    [Fact]
    public void Body_JsonPath_fails_when_path_not_found()
    {
        var body = """{"id":1}""";
        var a = new ApiAssertion { Source = ApiAssertionSource.Body, JsonPath = "$.missing", Operator = AssertionOperator.Equals, Expected = "1" };
        var r = ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, body);
        r.Passed.Should().BeFalse();
    }

    [Fact]
    public void Body_JsonPath_IsNull_passes_for_null_json_value()
    {
        var body = """{"data":null}""";
        var a = new ApiAssertion { Source = ApiAssertionSource.Body, JsonPath = "$.data", Operator = AssertionOperator.IsNull };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, body).Passed.Should().BeTrue();
    }

    // ── BodyText source ───────────────────────────────────────────────────────────

    [Fact]
    public void BodyText_Contains_passes_case_insensitive_when_flagged()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.BodyText, Operator = AssertionOperator.Contains, Expected = "SUCCESS", IgnoreCase = true };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "result: success").Passed.Should().BeTrue();
    }

    [Fact]
    public void BodyText_NotContains_passes_when_text_absent()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.BodyText, Operator = AssertionOperator.NotContains, Expected = "error" };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "everything is fine").Passed.Should().BeTrue();
    }

    [Fact]
    public void BodyText_Regex_passes_for_matching_pattern()
    {
        var a = new ApiAssertion { Source = ApiAssertionSource.BodyText, Operator = AssertionOperator.Regex, Expected = @"\d{4}-\d{2}-\d{2}" };
        ApiAssertionEvaluator.Evaluate(a, 200, EmptyHeaders, "date: 2024-01-15").Passed.Should().BeTrue();
    }
}
