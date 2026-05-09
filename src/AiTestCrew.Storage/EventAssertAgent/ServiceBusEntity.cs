using System.Text.Json.Serialization;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Either a queue (single consumer cohort) or a topic+subscription (pub/sub
/// fanout). The agent opens its receiver against this entity to evaluate
/// criteria. Both <see cref="Name"/> and <see cref="SubscriptionName"/> are
/// <c>{{Token}}</c>-substituted at runtime.
///
/// To assert against a dead-letter queue, set <see cref="Name"/> to
/// <c>"my-queue/$DeadLetterQueue"</c> (the Azure SDK accepts the suffix).
/// </summary>
public class ServiceBusEntity
{
    /// <summary>Queue or Topic. Defaults to Queue.</summary>
    public ServiceBusEntityType Type { get; set; } = ServiceBusEntityType.Queue;

    /// <summary>
    /// Queue name OR topic name (depending on <see cref="Type"/>).
    /// <c>{{Token}}</c>-substituted at runtime.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Subscription name. Required when <see cref="Type"/> is
    /// <see cref="ServiceBusEntityType.Topic"/>; ignored otherwise.
    /// <c>{{Token}}</c>-substituted at runtime.
    /// </summary>
    public string? SubscriptionName { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceBusEntityType
{
    Queue = 0,
    Topic,
}
