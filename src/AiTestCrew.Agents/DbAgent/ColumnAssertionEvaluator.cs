using System.Globalization;
using AiTestCrew.Agents.Common;

namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Pure evaluator for a single <see cref="ColumnAssertion"/> against a row's raw
/// column value. Returns <see cref="EvaluateResult.Passed"/> + an optional human
/// reason; never throws on malformed input — failures are surfaced as Failed (data
/// issue), not errors (config / agent issue).
///
/// NULL fidelity: when <paramref name="isDbNull"/> is true (or the JSON path
/// resolved to JSON null), only <see cref="AssertionOperator.IsNull"/> passes;
/// <see cref="AssertionOperator.IsNotNull"/> fails, and string ops including
/// <see cref="AssertionOperator.Equals"/> against an empty string fail (NULL ≠ "").
///
/// Implementation: front-loads JSONPath extraction (column-specific concern),
/// then delegates operator dispatch to <see cref="ScalarOperatorEvaluator"/>
/// (shared with the Service Bus event-assert agent in REQ-004).
/// </summary>
public static class ColumnAssertionEvaluator
{
    public readonly record struct EvaluateResult(bool Passed, string? Reason);

    /// <summary>
    /// Evaluates an assertion against a raw cell value. <paramref name="isDbNull"/>
    /// signals SQL NULL; the evaluator also treats a JSON path that resolves to
    /// JSON <c>null</c> as null for IsNull / IsNotNull semantics. JSON path
    /// extraction failure (path missing, column not JSON) is reported as a Failed
    /// assertion with a typed reason.
    /// </summary>
    public static EvaluateResult Evaluate(
        ColumnAssertion assertion,
        object? rawCellValue,
        bool isDbNull)
    {
        var label = Label(assertion);

        // 1. Front-load JSON-path extraction when configured. This is the
        //    column-specific concern; the operator dispatch beneath is generic.
        string? actualString;
        bool effectiveIsNull;

        if (!string.IsNullOrEmpty(assertion.JsonPath))
        {
            if (isDbNull)
            {
                actualString = null;
                effectiveIsNull = true;
            }
            else
            {
                var columnText = rawCellValue switch
                {
                    string s => s,
                    null => null,
                    _ => Convert.ToString(rawCellValue, CultureInfo.InvariantCulture),
                };

                var status = JsonValueExtractor.TryExtract(columnText, assertion.JsonPath!, out var node, out var err);
                switch (status)
                {
                    case JsonValueExtractor.ExtractionStatus.Found:
                        actualString = JsonValueExtractor.ToScalarString(node!);
                        effectiveIsNull = false;
                        break;
                    case JsonValueExtractor.ExtractionStatus.FoundNull:
                        actualString = null;
                        effectiveIsNull = true;
                        break;
                    case JsonValueExtractor.ExtractionStatus.NotJson:
                        return new EvaluateResult(false,
                            $"column '{assertion.Column}' is not JSON ({err})");
                    case JsonValueExtractor.ExtractionStatus.InvalidPath:
                        return new EvaluateResult(false,
                            $"column '{assertion.Column}': {err}");
                    default:  // NotFound
                        return new EvaluateResult(false,
                            $"JSON path '{assertion.JsonPath}' not found in column '{assertion.Column}'");
                }
            }
        }
        else
        {
            actualString = isDbNull
                ? null
                : Convert.ToString(rawCellValue, CultureInfo.InvariantCulture);
            effectiveIsNull = isDbNull;
        }

        // 2. Delegate operator dispatch to the shared scalar evaluator.
        var result = ScalarOperatorEvaluator.Evaluate(
            assertion.Operator,
            label,
            actualString,
            effectiveIsNull,
            assertion.Expected,
            assertion.Expected2,
            assertion.IgnoreCase,
            assertion.ToleranceSeconds,
            assertion.ToleranceDelta);

        return new EvaluateResult(result.Passed, result.Reason);
    }

    private static string Label(ColumnAssertion a) =>
        string.IsNullOrEmpty(a.JsonPath) ? a.Column : $"{a.Column}.{a.JsonPath}";
}
