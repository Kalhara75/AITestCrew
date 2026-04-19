namespace AiTestCrew.Agents.Persistence;

/// <summary>
/// A single SQL teardown statement attached to a test set. Runs once per
/// objective, before agent execution, to clear server-side state written by
/// prior runs (e.g. meter reads inserted by an MDN delivery).
///
/// The SQL body supports <c>{{Token}}</c> placeholders — tokens resolve from
/// the objective's environment parameters, the first delivery step's
/// <c>FieldValues</c>, and the template's <c>const</c> fields. Unknown tokens
/// fail the run loudly.
///
/// Safety is enforced by <c>SqlGuardrails</c> (WHERE required, keyword
/// denylist) and by per-env opt-in via <c>DataTeardownEnabled</c>.
/// </summary>
public class SqlTeardownStep
{
    /// <summary>Display name shown in the UI / logs (e.g. "Clear MDM reads").</summary>
    public string Name { get; set; } = "";

    /// <summary>The SQL body. May contain <c>{{Token}}</c> placeholders.</summary>
    public string Sql { get; set; } = "";
}
