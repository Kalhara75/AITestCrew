using System.Globalization;
using AiTestCrew.Agents.Common;
using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.Agents.ApiAgent;

/// <summary>
/// Pure evaluator for a single <see cref="ApiAssertion"/> against an HTTP
/// response. Returns <see cref="EvaluateResult.Passed"/> plus an optional human
/// reason; never throws on malformed input — failures are surfaced as Failed
/// (data issue), not exceptions (config/agent issue).
///
/// Source resolution order:
/// <list type="bullet">
///   <item><see cref="ApiAssertionSource.Status"/> — the integer status code string.</item>
///   <item><see cref="ApiAssertionSource.Header"/> — the value of the named response header (case-insensitive); missing header = null.</item>
///   <item><see cref="ApiAssertionSource.Body"/> — JSONPath extraction via <see cref="JsonValueExtractor"/> from the parsed response body; non-JSON body = typed error.</item>
///   <item><see cref="ApiAssertionSource.BodyText"/> — the raw response body string.</item>
/// </list>
///
/// After source resolution, operator dispatch delegates to the shared
/// <see cref="ScalarOperatorEvaluator"/> — the same evaluator used by
/// <see cref="ColumnAssertionEvaluator"/> (REQ-002) and
/// <see cref="AiTestCrew.Agents.EventAssertAgent.EventCriterionEvaluator"/> (REQ-004).
/// </summary>
public static class ApiAssertionEvaluator
{
    public readonly record struct EvaluateResult(bool Passed, string? Reason, string? Actual);

    /// <summary>
    /// Evaluates one <paramref name="assertion"/> against the HTTP response data.
    /// </summary>
    /// <param name="assertion">The assertion to check (already token-substituted by the caller).</param>
    /// <param name="statusCode">The HTTP response status code.</param>
    /// <param name="headers">Response headers dictionary (case-insensitive keys).</param>
    /// <param name="responseBody">The raw response body string (may be empty).</param>
    public static EvaluateResult Evaluate(
        ApiAssertion assertion,
        int statusCode,
        IDictionary<string, IEnumerable<string>> headers,
        string responseBody)
    {
        string? actualString;
        bool isNull;

        switch (assertion.Source)
        {
            case ApiAssertionSource.Status:
            {
                actualString = statusCode.ToString(CultureInfo.InvariantCulture);
                isNull = false;
                break;
            }

            case ApiAssertionSource.Header:
            {
                var headerName = assertion.HeaderName ?? "";
                // HTTP headers are case-insensitive — find the first key match.
                string? headerValue = null;
                foreach (var (k, vs) in headers)
                {
                    if (string.Equals(k, headerName, StringComparison.OrdinalIgnoreCase))
                    {
                        headerValue = string.Join(", ", vs);
                        break;
                    }
                }
                actualString = headerValue;
                isNull = headerValue is null;
                break;
            }

            case ApiAssertionSource.Body:
            {
                var path = assertion.JsonPath ?? "$";
                var status = JsonValueExtractor.TryExtract(
                    string.IsNullOrEmpty(responseBody) ? null : responseBody,
                    path, out var node, out var err);

                return status switch
                {
                    JsonValueExtractor.ExtractionStatus.Found =>
                        EvaluateOperator(assertion,
                            $"Body({path})",
                            JsonValueExtractor.ToScalarString(node!),
                            isNull: false),
                    JsonValueExtractor.ExtractionStatus.FoundNull =>
                        EvaluateOperator(assertion, $"Body({path})", null, isNull: true),
                    JsonValueExtractor.ExtractionStatus.NotJson =>
                        new EvaluateResult(false,
                            $"Body({path}): response body is not JSON ({err})", null),
                    JsonValueExtractor.ExtractionStatus.InvalidPath =>
                        new EvaluateResult(false,
                            $"Body({path}): JSON path is invalid ({err})", null),
                    _ => // NotFound
                        new EvaluateResult(false,
                            $"JSON path '{path}' not found in response body", null),
                };
            }

            case ApiAssertionSource.BodyText:
            {
                actualString = responseBody ?? "";
                isNull = false;
                break;
            }

            default:
                return new EvaluateResult(false,
                    $"Unknown assertion source '{assertion.Source}'", null);
        }

        return EvaluateOperator(assertion, assertion.Source.ToString(), actualString, isNull);
    }

    private static EvaluateResult EvaluateOperator(
        ApiAssertion assertion,
        string fieldLabel,
        string? actual,
        bool isNull)
    {
        var result = ScalarOperatorEvaluator.Evaluate(
            assertion.Operator,
            fieldLabel,
            actual,
            isNull,
            assertion.Expected,
            assertion.Expected2,
            assertion.IgnoreCase,
            assertion.ToleranceSeconds,
            assertion.ToleranceDelta);

        return new EvaluateResult(result.Passed, result.Reason, actual);
    }
}
