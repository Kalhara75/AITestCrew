using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// How the agent should interpret a received message's body when evaluating
/// criteria / captures with <c>Body.&lt;jsonpath&gt;</c> or <c>BodyXml.&lt;xpath&gt;</c>
/// field paths.
///
/// <list type="bullet">
///   <item><description><see cref="Auto"/> — sniff from <c>ContentType</c> first
///     (case-insensitive contains check on <c>json</c> / <c>xml</c>); fall back to
///     the first non-whitespace byte (<c>{</c>/<c>[</c> → JSON, <c>&lt;</c> → XML,
///     else Text). The default and right answer for almost everything.</description></item>
///   <item><description><see cref="Json"/> / <see cref="Xml"/> / <see cref="Text"/>
///     — force a specific parser; useful when a producer mislabels its
///     content-type.</description></item>
///   <item><description><see cref="Binary"/> — disables <c>Body.*</c> and
///     <c>BodyXml.*</c> paths (they fail with a typed reason). Match via system
///     properties, application properties, and <c>BodyLength</c> only.</description></item>
/// </list>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BodyFormat
{
    Auto = 0,
    Json,
    Xml,
    Text,
    Binary,
}
