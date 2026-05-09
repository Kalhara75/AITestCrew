namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Captures a value from the FIRST passing message into the per-objective
/// post-step run context as <c>{{<see cref="As"/>}}</c>. Sibling post-steps
/// — inline OR deferred — see the captured value via
/// <c>StepParameterSubstituter.Apply</c>. Round-trip through
/// <c>DeferredVerificationRequest.CapturedTokens</c> is handled by
/// <c>PostStepOrchestrator</c> unchanged from REQ-002.
///
/// Captures only run when every <see cref="EventAssertStepDefinition.Criteria"/>
/// entry passed and the overall <see cref="EventAssertStepDefinition.MatchMode"/>
/// resolved to pass; a failing assertion means the message is wrong, so the
/// captured value would be suspect.
/// </summary>
public class EventCapture
{
    /// <summary>
    /// Field path (same syntax as <see cref="EventCriterion.Field"/> — system
    /// properties, <c>ApplicationProperties.*</c>, <c>Body.&lt;jsonpath&gt;</c>,
    /// <c>BodyXml.&lt;xpath&gt;</c>, <c>BodyText</c>, <c>BodyLength</c>).
    /// <c>{{Token}}</c>-substituted at runtime.
    /// </summary>
    public string Field { get; set; } = "";

    /// <summary>
    /// Token name to bind in the post-step run context, e.g. <c>"MessageId"</c>
    /// (no braces). Sibling post-steps reference it as <c>{{MessageId}}</c>.
    ///
    /// <strong>Not</strong> <c>{{Token}}</c>-substituted — substituting it would
    /// let parent context redirect captures unexpectedly. Matches REQ-002's
    /// <see cref="DbAgent.ColumnCapture.As"/> rule.
    /// </summary>
    public string As { get; set; } = "";

    /// <summary>
    /// When true (default), the step fails if the field path resolves to
    /// null/missing (or the JSON/XML extraction yields nothing). When false,
    /// the token is left undefined so subsequent substitutions emit a literal
    /// <c>{{<see cref="As"/>}}</c> and log a WARN via the existing
    /// <c>unknownTokens</c> collector.
    /// </summary>
    public bool Required { get; set; } = true;
}
