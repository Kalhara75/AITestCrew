namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Captures a column (or a JSONPath-extracted scalar within a JSON column) from the
/// first row of a DB check's SELECT into the per-objective post-step run context as
/// <c>{{<see cref="As"/>}}</c>. Sibling post-steps that follow this DB check —
/// inline OR deferred — see the captured value via <c>StepParameterSubstituter.Apply</c>.
///
/// Captures only run when the DB check's <see cref="DbCheckStepDefinition.ColumnAssertions"/>
/// list passed (or was empty); a failing assertion means the row is wrong, so the
/// captured value would be suspect.
/// </summary>
public class ColumnCapture
{
    /// <summary>Column name from the SELECT result set. <c>{{Token}}</c>-substituted at runtime.</summary>
    public string Column { get; set; } = "";

    /// <summary>
    /// Optional JSONPath inside the column's value (e.g. <c>$.OrderId</c>).
    /// When set, the column value is parsed as JSON and the path resolved before
    /// the captured value is bound. <c>{{Token}}</c>-substituted.
    /// </summary>
    public string? JsonPath { get; set; }

    /// <summary>
    /// Token name to bind in the post-step run context, e.g. <c>"JobId"</c>
    /// (no braces). Sibling post-steps reference it as <c>{{JobId}}</c>.
    ///
    /// <strong>Not</strong> <c>{{Token}}</c>-substituted — substituting it would
    /// let parent context redirect captures unexpectedly.
    /// </summary>
    public string As { get; set; } = "";

    /// <summary>
    /// When true (default), the step fails if the column or JSON path resolves to
    /// null/missing. When false, the token is left undefined so subsequent
    /// substitutions emit a literal <c>{{<see cref="As"/>}}</c> and log a WARN
    /// via the existing <c>unknownTokens</c> collector.
    /// </summary>
    public bool Required { get; set; } = true;
}
