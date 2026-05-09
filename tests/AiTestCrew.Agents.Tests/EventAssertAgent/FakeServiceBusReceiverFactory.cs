using AiTestCrew.Agents.EventAssertAgent;
using AiTestCrew.Core.Configuration;

namespace AiTestCrew.Agents.Tests.EventAssertAgent;

/// <summary>
/// Programmable test fake for <see cref="IServiceBusReceiverFactory"/>.
/// Uses an opaque <c>RawMessage</c> token so settlement calls can be
/// recorded by the test for assertion. Drains messages in FIFO order from
/// the configured queue; <c>ReceiveBatchAsync</c> returns up to N messages
/// and an empty array when the queue is exhausted (mimicking the SDK's
/// "timeout elapsed, no message arrived" behaviour).
/// </summary>
public sealed class FakeServiceBusReceiverFactory : IServiceBusReceiverFactory
{
    public Queue<ReceivedMessageView> PendingMessages { get; } = new();
    public List<string> CompletedMessageIds { get; } = new();
    public List<string> AbandonedMessageIds { get; } = new();
    public List<(string ConnectionKey, string Entity)> Opened { get; } = new();

    public Task<IServiceBusReceiverHandle> OpenAsync(
        ServiceBusConnectionConfig connection,
        ServiceBusEntity entity,
        ReceiveMode mode,
        string? sessionId,
        CancellationToken ct)
    {
        Opened.Add((connection.ConnectionString ?? connection.FullyQualifiedNamespace ?? "?",
            entity.Name));
        return Task.FromResult<IServiceBusReceiverHandle>(new Handle(this));
    }

    public ReceivedMessageView Enqueue(ReceivedMessageView v)
    {
        // Stamp a synthetic raw-message token so settlement can be recorded.
        var stamped = new ReceivedMessageView
        {
            MessageId = v.MessageId,
            CorrelationId = v.CorrelationId,
            Subject = v.Subject,
            ContentType = v.ContentType,
            ReplyTo = v.ReplyTo,
            To = v.To,
            SessionId = v.SessionId,
            EnqueuedTimeUtc = v.EnqueuedTimeUtc,
            DeliveryCount = v.DeliveryCount,
            PartitionKey = v.PartitionKey,
            ApplicationProperties = v.ApplicationProperties,
            Body = v.Body,
            RawMessage = new SettlementToken(v.MessageId ?? Guid.NewGuid().ToString()),
        };
        PendingMessages.Enqueue(stamped);
        return stamped;
    }

    private sealed record SettlementToken(string Id);

    private sealed class Handle : IServiceBusReceiverHandle
    {
        private readonly FakeServiceBusReceiverFactory _parent;

        public Handle(FakeServiceBusReceiverFactory parent) { _parent = parent; }

        public Task<IReadOnlyList<ReceivedMessageView>> ReceiveBatchAsync(
            int maxMessages, TimeSpan perCallTimeout, CancellationToken ct)
        {
            var batch = new List<ReceivedMessageView>();
            for (var i = 0; i < maxMessages; i++)
            {
                if (!_parent.PendingMessages.TryDequeue(out var msg)) break;
                batch.Add(msg);
            }
            return Task.FromResult<IReadOnlyList<ReceivedMessageView>>(batch);
        }

        public Task<IReadOnlyList<ReceivedMessageView>> PeekBatchAsync(int maxMessages, CancellationToken ct)
        {
            var snapshot = _parent.PendingMessages.Take(maxMessages).ToList();
            return Task.FromResult<IReadOnlyList<ReceivedMessageView>>(snapshot);
        }

        public Task CompleteAsync(ReceivedMessageView message, CancellationToken ct)
        {
            if (message.RawMessage is SettlementToken t)
                _parent.CompletedMessageIds.Add(t.Id);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(ReceivedMessageView message, CancellationToken ct)
        {
            if (message.RawMessage is SettlementToken t)
                _parent.AbandonedMessageIds.Add(t.Id);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
