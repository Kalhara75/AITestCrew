using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// How the agent receives messages from the queue / subscription.
///
/// <list type="bullet">
///   <item><description><see cref="PeekLock"/> — default. Each received message
///     is locked, the agent inspects it, then settles via Complete (on pass +
///     <c>CompleteOnPass=true</c>) or Abandon (on fail, or pass with
///     <c>CompleteOnPass=false</c>). Safe for shared subscriptions where other
///     consumers run alongside the test.</description></item>
///   <item><description><see cref="ReceiveAndDelete"/> — destructive. Messages
///     are removed from the entity as they're received. Useful for the
///     pre-parent <c>DrainBeforeParent</c> hook (where staleness is the
///     intent) but otherwise risky on production-traffic queues.</description></item>
/// </list>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReceiveMode
{
    PeekLock = 0,
    ReceiveAndDelete,
}
