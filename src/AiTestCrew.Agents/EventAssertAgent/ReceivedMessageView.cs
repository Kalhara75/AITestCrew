namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Plain-old projection of an Azure Service Bus message that the agent works
/// with. Decoupled from <c>Azure.Messaging.ServiceBus.ServiceBusReceivedMessage</c>
/// (sealed, hard to fake) so unit tests can supply synthetic message streams
/// via a fake <see cref="IServiceBusReceiverFactory"/>.
///
/// <para>
/// <see cref="RawMessage"/> is the SDK-side reference used by Complete /
/// Abandon settlement on PeekLock receivers — it's <c>null</c> for messages
/// produced by peek-mode receivers and for messages produced by test fakes
/// (the fake factory uses ReceiveAndDelete-equivalent semantics where no
/// settlement is needed).
/// </para>
/// </summary>
public class ReceivedMessageView
{
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? ContentType { get; init; }
    public string? ReplyTo { get; init; }
    public string? To { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset EnqueuedTimeUtc { get; init; }
    public int DeliveryCount { get; init; }
    public string? PartitionKey { get; init; }

    /// <summary>Custom application properties — keys preserved verbatim from the wire.</summary>
    public IReadOnlyDictionary<string, object?> ApplicationProperties { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Raw body bytes. May be empty for messages with no payload.</summary>
    public byte[] Body { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Opaque SDK-side reference for settlement. Cast back to the concrete
    /// SDK type inside <see cref="ServiceBusReceiverFactory"/>; <c>null</c>
    /// when settlement isn't applicable (peek mode, fake factory).
    /// </summary>
    public object? RawMessage { get; init; }
}
