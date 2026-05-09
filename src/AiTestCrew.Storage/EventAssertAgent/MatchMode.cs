using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// How the per-message pass/fail vector resolves to an overall step result on
/// an Azure Service Bus event-assert post-step. The receive loop runs until
/// the timeout / max-messages / early-stop boundary is reached, then this
/// mode dictates the final verdict.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchMode
{
    /// <summary>Pass if at least one received message passes every criterion.</summary>
    AnyMessage = 0,

    /// <summary>Pass if every received message passes every criterion AND at least one message arrived.</summary>
    AllMessages,

    /// <summary>Pass if exactly one received message passes every criterion.</summary>
    ExactlyOne,

    /// <summary>Pass if exactly <see cref="EventAssertStepDefinition.ExpectedCount"/> messages passed.</summary>
    ExactCount,

    /// <summary>Pass if at least <see cref="EventAssertStepDefinition.ExpectedCount"/> messages passed.</summary>
    MinCount,

    /// <summary>
    /// Pass if at most <see cref="EventAssertStepDefinition.ExpectedCount"/> messages passed.
    /// <c>ExpectedCount=0</c> is the negative-assertion shape ("verify NO matching event was raised");
    /// the loop runs the full timeout to actually verify zero arrived.
    /// </summary>
    MaxCount,

    /// <summary>Pass if pass-count ∈ [<see cref="EventAssertStepDefinition.ExpectedCount"/>, <see cref="EventAssertStepDefinition.MaxCount"/>].</summary>
    CountRange,
}
