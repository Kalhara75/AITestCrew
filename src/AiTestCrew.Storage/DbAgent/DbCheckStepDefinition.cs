namespace AiTestCrew.Agents.DbAgent;

/// <summary>
/// Definition of a read-only database check step. Runs a single SELECT and
/// compares the result against an expected row count OR a column-values
/// dictionary. Token substitution (<c>{{Token}}</c>) is applied to both the
/// SQL text and the expected column values before execution.
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
    /// Logical connection key. For Slice 1 only "BravoDb" is recognised — it
    /// reuses the connection string resolved via <c>IEnvironmentResolver</c>
    /// (per-customer override). Additional keys can be added when more DBs
    /// are brought under test.
    /// </summary>
    public string ConnectionKey { get; set; } = "BravoDb";

    /// <summary>
    /// Single SELECT statement to run. Tokens (<c>{{NMI}}</c>, <c>{{MessageID}}</c>, etc.)
    /// are substituted from the parent post-step context before execution.
    /// </summary>
    public string Sql { get; set; } = "";

    /// <summary>
    /// Expected row count — if non-null, the check passes when the query returns
    /// exactly this many rows. Mutually exclusive with <see cref="ExpectedColumnValues"/>.
    /// </summary>
    public int? ExpectedRowCount { get; set; }

    /// <summary>
    /// Expected column-value assertions — each entry asserts that the FIRST row
    /// contains a column whose string value matches (case-insensitive). Tokens
    /// in values are substituted from context. Mutually exclusive with
    /// <see cref="ExpectedRowCount"/>.
    /// </summary>
    public Dictionary<string, string> ExpectedColumnValues { get; set; } = [];

    /// <summary>Per-query timeout in seconds. Default 15.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
