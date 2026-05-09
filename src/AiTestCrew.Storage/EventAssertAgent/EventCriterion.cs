using AiTestCrew.Agents.DbAgent;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Single criterion evaluated against a received Service Bus message. Reuses
/// REQ-002's <see cref="AssertionOperator"/> surface so the operator semantics
/// (Equals / Contains / Regex / Between / EqualsNumeric / EqualsDate /
/// IsNull / IsNotNull, …) are identical between DB asserts and event asserts.
///
/// <para>
/// <b>Field path syntax</b> (resolved by <c>MessageFieldResolver</c>):
/// </para>
/// <list type="bullet">
///   <item><description>System property — <c>MessageId</c>, <c>CorrelationId</c>,
///     <c>Subject</c>, <c>ContentType</c>, <c>ReplyTo</c>, <c>To</c>,
///     <c>SessionId</c>, <c>EnqueuedTimeUtc</c>, <c>DeliveryCount</c>,
///     <c>PartitionKey</c></description></item>
///   <item><description>Application property — <c>ApplicationProperties.&lt;name&gt;</c></description></item>
///   <item><description>JSON body — <c>Body.&lt;jsonpath&gt;</c> (e.g. <c>Body.Order.Id</c>,
///     <c>Body.Items[0].Sku</c>); valid when <see cref="EventAssertStepDefinition.BodyFormat"/>
///     resolves to <see cref="BodyFormat.Json"/></description></item>
///   <item><description>XML body — <c>BodyXml.&lt;xpath&gt;</c> (e.g.
///     <c>BodyXml.//Order/@Id</c>, or <c>BodyXml.//*[local-name()='Order']/@Id</c>
///     when documents declare default namespaces); valid when
///     <see cref="EventAssertStepDefinition.BodyFormat"/> resolves to
///     <see cref="BodyFormat.Xml"/></description></item>
///   <item><description>Raw body string — <c>BodyText</c></description></item>
///   <item><description>Body byte length — <c>BodyLength</c></description></item>
/// </list>
///
/// All string fields (<see cref="Field"/>, <see cref="Expected"/>,
/// <see cref="Expected2"/>) are <c>{{Token}}</c>-substituted at runtime.
/// </summary>
public class EventCriterion
{
    /// <summary>Field path. See class summary for syntax. <c>{{Token}}</c>-substituted.</summary>
    public string Field { get; set; } = "";

    /// <summary>Comparator. Defaults to <see cref="AssertionOperator.Equals"/>.</summary>
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;

    /// <summary>Expected value (string projection). <c>{{Token}}</c>-substituted.</summary>
    public string Expected { get; set; } = "";

    /// <summary>Upper bound for <see cref="AssertionOperator.Between"/>; ignored otherwise. <c>{{Token}}</c>-substituted.</summary>
    public string? Expected2 { get; set; }

    /// <summary>When true (default), string operators use case-insensitive comparison.</summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>Tolerance window (seconds) applied by <see cref="AssertionOperator.EqualsDate"/>.</summary>
    public double? ToleranceSeconds { get; set; }

    /// <summary>Tolerance delta applied by <see cref="AssertionOperator.EqualsNumeric"/>.</summary>
    public decimal? ToleranceDelta { get; set; }
}
