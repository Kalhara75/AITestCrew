using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Definition of a read-only database check step. Runs a single SELECT and
/// compares the result against an expected row count OR a structured
/// <see cref="ColumnAssertions"/> list. Token substitution (<c>{{Token}}</c>) is
/// applied to the SQL text and to every assertion / capture field that takes a
/// string before execution (see <c>StepParameterSubstituter.Apply</c>).
///
/// Safety: a runtime guardrail rejects any statement other than SELECT,
/// mirroring <c>SqlGuardrails</c> for the teardown executor.
///
/// Used as a <see cref="AiTestCrew.Agents.AseXmlAgent.VerificationStep.DbCheck"/>
/// carrier on a post-step — never as a top-level test step.
/// </summary>
public class DbCheckStepDefinition
{
    /// <summary>Short human-readable name for the check (e.g. "Job row created for NMI").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Logical connection key resolved via
    /// <c>IEnvironmentResolver.ResolveDbConnectionString(connectionKey, envKey)</c>.
    /// <c>"BravoDb"</c> falls back to the legacy <c>BravoDbConnectionString</c>
    /// when no <c>DbConnections.BravoDb</c> entry exists. New keys
    /// (e.g. <c>"SdrReportingDb"</c>) must be configured under
    /// <c>TestEnvironment.Environments.&lt;env&gt;.DbConnections</c> or the top-level
    /// <c>TestEnvironment.DbConnections</c> fallback.
    /// </summary>
    public string ConnectionKey { get; set; } = "BravoDb";

    /// <summary>
    /// Single SELECT statement to run. Tokens (<c>{{NMI}}</c>, <c>{{MessageID}}</c>, etc.)
    /// are substituted from the parent post-step context before execution.
    /// </summary>
    public string Sql { get; set; } = "";

    /// <summary>
    /// Expected row count — if non-null, the check passes when the query returns
    /// exactly this many rows. Mutually exclusive with <see cref="ColumnAssertions"/>:
    /// when both are present, the assertion list takes precedence.
    /// </summary>
    public int? ExpectedRowCount { get; set; }

    /// <summary>
    /// Per-column assertions evaluated against the FIRST row of the result set.
    /// Each entry is an explicit <see cref="ColumnAssertion"/> with operator,
    /// optional JSONPath, expected value(s), and tolerances.
    /// </summary>
    public List<ColumnAssertion> ColumnAssertions { get; set; } = [];

    /// <summary>
    /// Captures applied to the FIRST row after every <see cref="ColumnAssertions"/>
    /// entry passes. Each capture binds a column or JSON-extracted scalar into the
    /// per-objective post-step run context as <c>{{<see cref="ColumnCapture.As"/>}}</c>
    /// for sibling post-steps to read.
    /// </summary>
    public List<ColumnCapture> Captures { get; set; } = [];

    /// <summary>Per-query timeout in seconds. Default 15.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    // ── Backward compatibility for legacy `expectedColumnValues` JSON ──

    /// <summary>
    /// Legacy shim — accepts the old <c>expectedColumnValues: {col: "value"}</c>
    /// shape on deserialise and promotes each entry into a
    /// <see cref="AssertionOperator.Equals"/> assertion appended to
    /// <see cref="ColumnAssertions"/>. Never serialised back out (returns null).
    /// Mirrors the <c>ApiDefinitionCompat</c>/<c>WebUiDefinitionCompat</c> pattern
    /// used by <c>TestObjective</c>.
    /// </summary>
    [JsonPropertyName("expectedColumnValues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LegacyExpectedColumnValues
    {
        get => null;  // never serialise
        set
        {
            if (value is null) return;
            foreach (var (column, expected) in value)
            {
                ColumnAssertions.Add(new ColumnAssertion
                {
                    Column = column,
                    Operator = AssertionOperator.Equals,
                    Expected = expected,
                });
            }
        }
    }
}
