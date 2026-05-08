using System.Globalization;
using System.Text.RegularExpressions;
using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.Agents.Common;

/// <summary>
/// Operator-dispatch helper shared between scalar-source evaluators (DB column
/// values, Service Bus message fields, …). Takes a string projection of the
/// actual value plus an <paramref name="isNull"/> flag and returns Pass/Fail
/// plus a human reason. Callers are responsible for value extraction
/// (JSONPath into a column, XPath into a message body, etc.) and for building
/// the <paramref name="fieldLabel"/> used in error messages.
///
/// NULL fidelity matches REQ-002's contract: when <paramref name="isNull"/> is
/// true, only <see cref="AssertionOperator.IsNull"/> passes; every other
/// operator (including Equals against an empty string) fails.
/// </summary>
public static class ScalarOperatorEvaluator
{
    public readonly record struct Result(bool Passed, string? Reason);

    public static Result Evaluate(
        AssertionOperator op,
        string fieldLabel,
        string? actual,
        bool isNull,
        string expected,
        string? expected2,
        bool ignoreCase,
        double? toleranceSeconds,
        decimal? toleranceDelta)
    {
        // NULL-aware operators short-circuit.
        switch (op)
        {
            case AssertionOperator.IsNull:
                return isNull
                    ? new Result(true, null)
                    : new Result(false,
                        $"{fieldLabel}: expected NULL, got '{Truncate(actual)}'");
            case AssertionOperator.IsNotNull:
                return isNull
                    ? new Result(false, $"{fieldLabel}: expected non-NULL, got NULL")
                    : new Result(true, null);
        }

        // For every non-null-aware operator, NULL on the actual side is a fail —
        // NULL never equals "", contains anything, or is greater/less than a value.
        if (isNull)
        {
            return new Result(false,
                $"{fieldLabel}: expected '{Truncate(expected)}' ({op}), got NULL");
        }

        var actualString = actual ?? "";

        switch (op)
        {
            case AssertionOperator.Equals:
                return CompareString(fieldLabel, actualString, expected, ignoreCase,
                    (a, e, c) => string.Equals(a, e, c),
                    "expected '{0}', got '{1}'");

            case AssertionOperator.NotEquals:
                return CompareString(fieldLabel, actualString, expected, ignoreCase,
                    (a, e, c) => !string.Equals(a, e, c),
                    "expected != '{0}', got '{1}'");

            case AssertionOperator.Contains:
                return CompareString(fieldLabel, actualString, expected, ignoreCase,
                    (a, e, c) => a.Contains(e, c),
                    "expected to contain '{0}', got '{1}'");

            case AssertionOperator.NotContains:
                return CompareString(fieldLabel, actualString, expected, ignoreCase,
                    (a, e, c) => !a.Contains(e, c),
                    "expected to NOT contain '{0}', got '{1}'");

            case AssertionOperator.StartsWith:
                return CompareString(fieldLabel, actualString, expected, ignoreCase,
                    (a, e, c) => a.StartsWith(e, c),
                    "expected to start with '{0}', got '{1}'");

            case AssertionOperator.EndsWith:
                return CompareString(fieldLabel, actualString, expected, ignoreCase,
                    (a, e, c) => a.EndsWith(e, c),
                    "expected to end with '{0}', got '{1}'");

            case AssertionOperator.Regex:
                return EvaluateRegex(fieldLabel, actualString, expected, ignoreCase);

            case AssertionOperator.GreaterThan:
                return EvaluateCompare(fieldLabel, actualString, expected, expectsGreater: true);

            case AssertionOperator.LessThan:
                return EvaluateCompare(fieldLabel, actualString, expected, expectsGreater: false);

            case AssertionOperator.Between:
                return EvaluateBetween(fieldLabel, actualString, expected, expected2);

            case AssertionOperator.EqualsNumeric:
                return EvaluateEqualsNumeric(fieldLabel, actualString, expected, toleranceDelta);

            case AssertionOperator.EqualsDate:
                return EvaluateEqualsDate(fieldLabel, actualString, expected, toleranceSeconds);

            default:
                return new Result(false,
                    $"{fieldLabel}: operator '{op}' is not supported");
        }
    }

    private static Result CompareString(
        string fieldLabel, string actual, string expected, bool ignoreCase,
        Func<string, string, StringComparison, bool> predicate,
        string failTemplate)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return predicate(actual, expected, cmp)
            ? new Result(true, null)
            : new Result(false,
                $"{fieldLabel}: " + string.Format(CultureInfo.InvariantCulture, failTemplate,
                    Truncate(expected), Truncate(actual)));
    }

    private static Result EvaluateRegex(string fieldLabel, string actual, string pattern, bool ignoreCase)
    {
        Regex rx;
        try
        {
            var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            rx = new Regex(pattern, opts);
        }
        catch (ArgumentException ex)
        {
            return new Result(false,
                $"{fieldLabel}: regex pattern '{Truncate(pattern)}' is invalid: {ex.Message}");
        }
        return rx.IsMatch(actual)
            ? new Result(true, null)
            : new Result(false,
                $"{fieldLabel}: expected to match /{Truncate(pattern)}/, got '{Truncate(actual)}'");
    }

    private static Result EvaluateCompare(string fieldLabel, string actual, string expected, bool expectsGreater)
    {
        if (TryParseDecimal(actual, out var actualNum) && TryParseDecimal(expected, out var expectedNum))
        {
            var passed = expectsGreater ? actualNum > expectedNum : actualNum < expectedNum;
            return passed
                ? new Result(true, null)
                : new Result(false,
                    $"{fieldLabel}: expected {(expectsGreater ? ">" : "<")} {expectedNum}, got {actualNum}");
        }

        if (TryParseDateTimeOffset(actual, out var actualDate) && TryParseDateTimeOffset(expected, out var expectedDate))
        {
            var passed = expectsGreater ? actualDate > expectedDate : actualDate < expectedDate;
            return passed
                ? new Result(true, null)
                : new Result(false,
                    $"{fieldLabel}: expected {(expectsGreater ? ">" : "<")} '{Truncate(expected)}', got '{Truncate(actual)}'");
        }

        return new Result(false,
            $"{fieldLabel}: cannot compare '{Truncate(actual)}' to '{Truncate(expected)}' as a number or date");
    }

    private static Result EvaluateBetween(string fieldLabel, string actual, string expected, string? expected2)
    {
        if (string.IsNullOrEmpty(expected2))
        {
            return new Result(false,
                $"{fieldLabel}: Between requires Expected2 (upper bound) but it is empty");
        }

        if (TryParseDecimal(actual, out var actualNum)
            && TryParseDecimal(expected, out var lowNum)
            && TryParseDecimal(expected2!, out var highNum))
        {
            var passed = actualNum >= lowNum && actualNum <= highNum;
            return passed
                ? new Result(true, null)
                : new Result(false,
                    $"{fieldLabel}: expected [{lowNum}, {highNum}], got {actualNum}");
        }

        if (TryParseDateTimeOffset(actual, out var actualDate)
            && TryParseDateTimeOffset(expected, out var lowDate)
            && TryParseDateTimeOffset(expected2!, out var highDate))
        {
            var passed = actualDate >= lowDate && actualDate <= highDate;
            return passed
                ? new Result(true, null)
                : new Result(false,
                    $"{fieldLabel}: expected ['{Truncate(expected)}', '{Truncate(expected2)}'], got '{Truncate(actual)}'");
        }

        return new Result(false,
            $"{fieldLabel}: cannot interpret '{Truncate(actual)}' / '{Truncate(expected)}' / '{Truncate(expected2)}' as numbers or dates");
    }

    private static Result EvaluateEqualsNumeric(string fieldLabel, string actual, string expected, decimal? toleranceDelta)
    {
        if (!TryParseDecimal(actual, out var actualNum))
            return new Result(false,
                $"{fieldLabel}: actual '{Truncate(actual)}' is not a number");
        if (!TryParseDecimal(expected, out var expectedNum))
            return new Result(false,
                $"{fieldLabel}: expected '{Truncate(expected)}' is not a number");

        var tolerance = toleranceDelta ?? 0m;
        var diff = Math.Abs(actualNum - expectedNum);
        return diff <= tolerance
            ? new Result(true, null)
            : new Result(false,
                $"{fieldLabel}: expected {expectedNum} (±{tolerance}), got {actualNum}");
    }

    private static Result EvaluateEqualsDate(string fieldLabel, string actual, string expected, double? toleranceSeconds)
    {
        if (!TryParseDateTimeOffset(actual, out var actualDate))
            return new Result(false,
                $"{fieldLabel}: actual '{Truncate(actual)}' is not a date");
        if (!TryParseDateTimeOffset(expected, out var expectedDate))
            return new Result(false,
                $"{fieldLabel}: expected '{Truncate(expected)}' is not a date");

        var tolerance = TimeSpan.FromSeconds(toleranceSeconds ?? 0);
        var diff = (actualDate - expectedDate).Duration();
        return diff <= tolerance
            ? new Result(true, null)
            : new Result(false,
                $"{fieldLabel}: expected '{Truncate(expected)}' (±{tolerance.TotalSeconds:0.##}s), got '{Truncate(actual)}'");
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

    private static string Truncate(string? s, int max = 200)
    {
        if (s is null) return "<null>";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
