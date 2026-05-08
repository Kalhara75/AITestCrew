namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Single per-column assertion on the first row returned by a DB check's SELECT.
/// Lifted out of the legacy <c>ExpectedColumnValues: Dictionary&lt;string,string&gt;</c>
/// shape so we can do JSONPath, comparators richer than equality, and NULL-aware
/// matching without sacrificing back-compat (the legacy dict deserialises into a
/// list of <see cref="AssertionOperator.Equals"/> entries via the JSON shim on
/// <see cref="DbCheckStepDefinition"/>).
/// </summary>
public class ColumnAssertion
{
    /// <summary>Column name from the SELECT result set. <c>{{Token}}</c>-substituted at runtime.</summary>
    public string Column { get; set; } = "";

    /// <summary>
    /// Optional JSONPath inside the column's value (e.g. <c>$.OrderId</c>,
    /// <c>$.Items[0].Code</c>). When set, the column value is parsed as JSON
    /// and the path resolved before the operator runs. <c>{{Token}}</c>-substituted.
    /// </summary>
    public string? JsonPath { get; set; }

    /// <summary>Comparator to apply. Defaults to <see cref="AssertionOperator.Equals"/>.</summary>
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;

    /// <summary>Expected value (string projection). <c>{{Token}}</c>-substituted.</summary>
    public string Expected { get; set; } = "";

    /// <summary>
    /// Second bound, used by <see cref="AssertionOperator.Between"/> as the upper
    /// inclusive bound. Ignored by other operators. <c>{{Token}}</c>-substituted.
    /// </summary>
    public string? Expected2 { get; set; }

    /// <summary>
    /// When true (default), string operators use case-insensitive comparison.
    /// Numeric / date operators ignore this flag.
    /// </summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>
    /// Tolerance window (seconds) applied by <see cref="AssertionOperator.EqualsDate"/>.
    /// Null = exact match (zero tolerance).
    /// </summary>
    public double? ToleranceSeconds { get; set; }

    /// <summary>
    /// Tolerance delta applied by <see cref="AssertionOperator.EqualsNumeric"/>.
    /// Null = exact match (zero tolerance).
    /// </summary>
    public decimal? ToleranceDelta { get; set; }
}
