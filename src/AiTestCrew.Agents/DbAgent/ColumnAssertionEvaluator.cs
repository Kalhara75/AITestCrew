using System.Globalization;
using System.Text.RegularExpressions;

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
        // 1. JSON-path extraction up front, when configured.
        var effectiveIsNull = isDbNull;
        string? actualString;

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
        }

        // 2. NULL-aware operators short-circuit.
        switch (assertion.Operator)
        {
            case AssertionOperator.IsNull:
                return effectiveIsNull
                    ? new EvaluateResult(true, null)
                    : new EvaluateResult(false,
                        $"{Label(assertion)}: expected NULL, got '{Truncate(actualString)}'");
            case AssertionOperator.IsNotNull:
                return effectiveIsNull
                    ? new EvaluateResult(false,
                        $"{Label(assertion)}: expected non-NULL, got NULL")
                    : new EvaluateResult(true, null);
        }

        // For every non-null-aware operator, NULL on the actual side is a fail —
        // NULL never equals "", contains anything, or is greater/less than a value.
        if (effectiveIsNull)
        {
            return new EvaluateResult(false,
                $"{Label(assertion)}: expected '{Truncate(assertion.Expected)}' ({assertion.Operator}), got NULL");
        }

        var actual = actualString ?? "";

        switch (assertion.Operator)
        {
            case AssertionOperator.Equals:
                return CompareString(assertion, actual, (a, e, c) => string.Equals(a, e, c),
                    "expected '{0}', got '{1}'");

            case AssertionOperator.NotEquals:
                return CompareString(assertion, actual, (a, e, c) => !string.Equals(a, e, c),
                    "expected != '{0}', got '{1}'");

            case AssertionOperator.Contains:
                return CompareString(assertion, actual, (a, e, c) => a.Contains(e, c),
                    "expected to contain '{0}', got '{1}'");

            case AssertionOperator.NotContains:
                return CompareString(assertion, actual, (a, e, c) => !a.Contains(e, c),
                    "expected to NOT contain '{0}', got '{1}'");

            case AssertionOperator.StartsWith:
                return CompareString(assertion, actual, (a, e, c) => a.StartsWith(e, c),
                    "expected to start with '{0}', got '{1}'");

            case AssertionOperator.EndsWith:
                return CompareString(assertion, actual, (a, e, c) => a.EndsWith(e, c),
                    "expected to end with '{0}', got '{1}'");

            case AssertionOperator.Regex:
                return EvaluateRegex(assertion, actual);

            case AssertionOperator.GreaterThan:
                return EvaluateCompare(assertion, actual, expectsGreater: true);

            case AssertionOperator.LessThan:
                return EvaluateCompare(assertion, actual, expectsGreater: false);

            case AssertionOperator.Between:
                return EvaluateBetween(assertion, actual);

            case AssertionOperator.EqualsNumeric:
                return EvaluateEqualsNumeric(assertion, actual);

            case AssertionOperator.EqualsDate:
                return EvaluateEqualsDate(assertion, actual);

            default:
                return new EvaluateResult(false,
                    $"{Label(assertion)}: operator '{assertion.Operator}' is not supported");
        }
    }

    private static EvaluateResult CompareString(
        ColumnAssertion a, string actual,
        Func<string, string, StringComparison, bool> predicate,
        string failTemplate)
    {
        var cmp = a.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return predicate(actual, a.Expected, cmp)
            ? new EvaluateResult(true, null)
            : new EvaluateResult(false,
                $"{Label(a)}: " + string.Format(CultureInfo.InvariantCulture, failTemplate,
                    Truncate(a.Expected), Truncate(actual)));
    }

    private static EvaluateResult EvaluateRegex(ColumnAssertion a, string actual)
    {
        Regex rx;
        try
        {
            var opts = a.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            rx = new Regex(a.Expected, opts);
        }
        catch (ArgumentException ex)
        {
            return new EvaluateResult(false,
                $"{Label(a)}: regex pattern '{Truncate(a.Expected)}' is invalid: {ex.Message}");
        }
        return rx.IsMatch(actual)
            ? new EvaluateResult(true, null)
            : new EvaluateResult(false,
                $"{Label(a)}: expected to match /{Truncate(a.Expected)}/, got '{Truncate(actual)}'");
    }

    private static EvaluateResult EvaluateCompare(ColumnAssertion a, string actual, bool expectsGreater)
    {
        // Try decimal first, fall back to date.
        if (TryParseDecimal(actual, out var actualNum) && TryParseDecimal(a.Expected, out var expectedNum))
        {
            var passed = expectsGreater ? actualNum > expectedNum : actualNum < expectedNum;
            return passed
                ? new EvaluateResult(true, null)
                : new EvaluateResult(false,
                    $"{Label(a)}: expected {(expectsGreater ? ">" : "<")} {expectedNum}, got {actualNum}");
        }

        if (TryParseDateTimeOffset(actual, out var actualDate) && TryParseDateTimeOffset(a.Expected, out var expectedDate))
        {
            var passed = expectsGreater ? actualDate > expectedDate : actualDate < expectedDate;
            return passed
                ? new EvaluateResult(true, null)
                : new EvaluateResult(false,
                    $"{Label(a)}: expected {(expectsGreater ? ">" : "<")} '{Truncate(a.Expected)}', got '{Truncate(actual)}'");
        }

        return new EvaluateResult(false,
            $"{Label(a)}: cannot compare '{Truncate(actual)}' to '{Truncate(a.Expected)}' as a number or date");
    }

    private static EvaluateResult EvaluateBetween(ColumnAssertion a, string actual)
    {
        if (string.IsNullOrEmpty(a.Expected2))
        {
            return new EvaluateResult(false,
                $"{Label(a)}: Between requires Expected2 (upper bound) but it is empty");
        }

        if (TryParseDecimal(actual, out var actualNum)
            && TryParseDecimal(a.Expected, out var lowNum)
            && TryParseDecimal(a.Expected2!, out var highNum))
        {
            var passed = actualNum >= lowNum && actualNum <= highNum;
            return passed
                ? new EvaluateResult(true, null)
                : new EvaluateResult(false,
                    $"{Label(a)}: expected [{lowNum}, {highNum}], got {actualNum}");
        }

        if (TryParseDateTimeOffset(actual, out var actualDate)
            && TryParseDateTimeOffset(a.Expected, out var lowDate)
            && TryParseDateTimeOffset(a.Expected2!, out var highDate))
        {
            var passed = actualDate >= lowDate && actualDate <= highDate;
            return passed
                ? new EvaluateResult(true, null)
                : new EvaluateResult(false,
                    $"{Label(a)}: expected ['{Truncate(a.Expected)}', '{Truncate(a.Expected2)}'], got '{Truncate(actual)}'");
        }

        return new EvaluateResult(false,
            $"{Label(a)}: cannot interpret '{Truncate(actual)}' / '{Truncate(a.Expected)}' / '{Truncate(a.Expected2)}' as numbers or dates");
    }

    private static EvaluateResult EvaluateEqualsNumeric(ColumnAssertion a, string actual)
    {
        if (!TryParseDecimal(actual, out var actualNum))
            return new EvaluateResult(false,
                $"{Label(a)}: actual '{Truncate(actual)}' is not a number");
        if (!TryParseDecimal(a.Expected, out var expectedNum))
            return new EvaluateResult(false,
                $"{Label(a)}: expected '{Truncate(a.Expected)}' is not a number");

        var tolerance = a.ToleranceDelta ?? 0m;
        var diff = Math.Abs(actualNum - expectedNum);
        return diff <= tolerance
            ? new EvaluateResult(true, null)
            : new EvaluateResult(false,
                $"{Label(a)}: expected {expectedNum} (±{tolerance}), got {actualNum}");
    }

    private static EvaluateResult EvaluateEqualsDate(ColumnAssertion a, string actual)
    {
        if (!TryParseDateTimeOffset(actual, out var actualDate))
            return new EvaluateResult(false,
                $"{Label(a)}: actual '{Truncate(actual)}' is not a date");
        if (!TryParseDateTimeOffset(a.Expected, out var expectedDate))
            return new EvaluateResult(false,
                $"{Label(a)}: expected '{Truncate(a.Expected)}' is not a date");

        var tolerance = TimeSpan.FromSeconds(a.ToleranceSeconds ?? 0);
        var diff = (actualDate - expectedDate).Duration();
        return diff <= tolerance
            ? new EvaluateResult(true, null)
            : new EvaluateResult(false,
                $"{Label(a)}: expected '{Truncate(a.Expected)}' (±{tolerance.TotalSeconds:0.##}s), got '{Truncate(actual)}'");
    }

    private static bool TryParseDecimal(string text, out decimal value)
    {
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDateTimeOffset(string text, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            return true;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            return true;
        }
        value = default;
        return false;
    }

    private static string Label(ColumnAssertion a) =>
        string.IsNullOrEmpty(a.JsonPath) ? a.Column : $"{a.Column}.{a.JsonPath}";

    private static string Truncate(string? s, int max = 200)
    {
        if (s is null) return "<null>";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
