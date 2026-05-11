using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// A single structured assertion on an HTTP response — evaluated after the
/// API call completes. When <see cref="ApiTestDefinition.ApiAssertions"/> has
/// entries the LLM-validation path is bypassed; structured assertions are
/// authoritative.
///
/// Operator semantics match <see cref="ColumnAssertion"/> (REQ-002) and
/// <see cref="AiTestCrew.Agents.EventAssertAgent.EventCriterion"/> (REQ-004)
/// via the shared <see cref="AiTestCrew.Agents.Common.ScalarOperatorEvaluator"/>.
/// </summary>
public class ApiAssertion
{
    /// <summary>Where in the response to extract the actual value from.</summary>
    public ApiAssertionSource Source { get; set; } = ApiAssertionSource.Status;

    /// <summary>
    /// Response header name — required when <see cref="Source"/> is
    /// <see cref="ApiAssertionSource.Header"/>. Compared case-insensitively.
    /// <c>{{Token}}</c>-substituted at runtime.
    /// </summary>
    public string? HeaderName { get; set; }

    /// <summary>
    /// JSONPath expression applied to the parsed JSON response body — required
    /// when <see cref="Source"/> is <see cref="ApiAssertionSource.Body"/>.
    /// Example: <c>$.data.id</c>, <c>$.items[0].status</c>.
    /// <c>{{Token}}</c>-substituted at runtime (allows dynamic path segments).
    /// </summary>
    public string? JsonPath { get; set; }

    /// <summary>Operator to apply. Defaults to <see cref="AssertionOperator.Equals"/>.</summary>
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;

    /// <summary>
    /// Expected value (string projection). <c>{{Token}}</c>-substituted at runtime
    /// so prior captures can flow into assertion predicates.
    /// </summary>
    public string Expected { get; set; } = "";

    /// <summary>
    /// Upper bound for <see cref="AssertionOperator.Between"/>; ignored by all
    /// other operators. <c>{{Token}}</c>-substituted.
    /// </summary>
    public string? Expected2 { get; set; }

    /// <summary>
    /// When true (default), string operators use case-insensitive comparison.
    /// Numeric and date operators ignore this flag.
    /// </summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>
    /// Tolerance window (seconds) for <see cref="AssertionOperator.EqualsDate"/>.
    /// Null = exact match (zero tolerance).
    /// </summary>
    public double? ToleranceSeconds { get; set; }

    /// <summary>
    /// Tolerance delta for <see cref="AssertionOperator.EqualsNumeric"/>.
    /// Null = exact match (zero tolerance).
    /// </summary>
    public decimal? ToleranceDelta { get; set; }
}
