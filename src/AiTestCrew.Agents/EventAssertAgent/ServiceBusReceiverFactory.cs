using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using AiTestCrew.Core.Configuration;
using SdkServiceBusReceiveMode = Azure.Messaging.ServiceBus.ServiceBusReceiveMode;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Default <see cref="IServiceBusReceiverFactory"/>. Wraps the Azure SDK's
/// <c>ServiceBusClient</c>. Caches one client per
/// <c>(namespace, authMode, ManagedIdentityClientId)</c> tuple so we don't
/// pay the connection-establishment cost every receive loop.
///
/// <para>
/// Lifetime: a single instance is registered as a singleton in DI. Cached
/// clients are disposed when this factory is disposed (driven by the host's
/// shutdown). Disposing a factory that's never been used is safe — the
/// concurrent dictionary is just empty.
/// </para>
/// </summary>
public class ServiceBusReceiverFactory : IServiceBusReceiverFactory, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServiceBusClient> _clients = new();

    public async Task<IServiceBusReceiverHandle> OpenAsync(
        ServiceBusConnectionConfig connection,
        ServiceBusEntity entity,
        ReceiveMode mode,
        string? sessionId,
        CancellationToken ct)
    {
        var client = _clients.GetOrAdd(BuildCacheKey(connection), _ => BuildClient(connection));

        var sdkMode = mode == ReceiveMode.ReceiveAndDelete
            ? SdkServiceBusReceiveMode.ReceiveAndDelete
            : SdkServiceBusReceiveMode.PeekLock;

        if (entity.Type == ServiceBusEntityType.Queue)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                var sessionReceiver = await client.AcceptSessionAsync(
                    entity.Name,
                    sessionId,
                    new ServiceBusSessionReceiverOptions { ReceiveMode = sdkMode },
                    ct);
                return new SessionHandle(sessionReceiver);
            }
            var receiver = client.CreateReceiver(
                entity.Name,
                new ServiceBusReceiverOptions { ReceiveMode = sdkMode });
            return new ReceiverHandle(receiver);
        }

        // Topic + subscription
        if (string.IsNullOrEmpty(entity.SubscriptionName))
            throw new InvalidOperationException(
                $"Topic entity '{entity.Name}' requires SubscriptionName to be set");

        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessionReceiver = await client.AcceptSessionAsync(
                entity.Name,
                entity.SubscriptionName,
                sessionId,
                new ServiceBusSessionReceiverOptions { ReceiveMode = sdkMode },
                ct);
            return new SessionHandle(sessionReceiver);
        }

        var topicReceiver = client.CreateReceiver(
            entity.Name,
            entity.SubscriptionName,
            new ServiceBusReceiverOptions { ReceiveMode = sdkMode });
        return new ReceiverHandle(topicReceiver);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _clients.Clear();
        GC.SuppressFinalize(this);
    }

    private static string BuildCacheKey(ServiceBusConnectionConfig c) =>
        c.AuthMode switch
        {
            ServiceBusAuthMode.AzureAd =>
                $"aad|{c.FullyQualifiedNamespace}|{c.ManagedIdentityClientId}",
            // Use the FQ namespace from the connection string for cache identity if available;
            // otherwise hash the connection string so two distinct strings get distinct clients.
            ServiceBusAuthMode.ConnectionString =>
                $"cs|{c.ConnectionString?.GetHashCode() ?? 0}",
            _ => $"unknown|{c.GetHashCode()}",
        };

    private static ServiceBusClient BuildClient(ServiceBusConnectionConfig c)
    {
        return c.AuthMode switch
        {
            ServiceBusAuthMode.AzureAd => BuildAzureAdClient(c),
            ServiceBusAuthMode.ConnectionString => new ServiceBusClient(c.ConnectionString),
            _ => throw new InvalidOperationException(
                $"Unsupported Service Bus auth mode '{c.AuthMode}'"),
        };
    }

    private static ServiceBusClient BuildAzureAdClient(ServiceBusConnectionConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.FullyQualifiedNamespace))
            throw new InvalidOperationException(
                "AzureAd auth requires FullyQualifiedNamespace to be set");

        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(c.ManagedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId = c.ManagedIdentityClientId;
        }
        var credential = new DefaultAzureCredential(credentialOptions);
        return new ServiceBusClient(c.FullyQualifiedNamespace, credential);
    }

    // ── Handle implementations ────────────────────────────────────────

    private sealed class ReceiverHandle : IServiceBusReceiverHandle
    {
        private readonly ServiceBusReceiver _receiver;

        public ReceiverHandle(ServiceBusReceiver receiver) { _receiver = receiver; }

        public async Task<IReadOnlyList<ReceivedMessageView>> ReceiveBatchAsync(
            int maxMessages, TimeSpan perCallTimeout, CancellationToken ct)
        {
            var batch = await _receiver.ReceiveMessagesAsync(maxMessages, perCallTimeout, ct);
            return batch is null
                ? Array.Empty<ReceivedMessageView>()
                : batch.Select(MessageProjector.Project).ToList();
        }

        public async Task<IReadOnlyList<ReceivedMessageView>> PeekBatchAsync(
            int maxMessages, CancellationToken ct)
        {
            var batch = await _receiver.PeekMessagesAsync(maxMessages, cancellationToken: ct);
            return batch is null
                ? Array.Empty<ReceivedMessageView>()
                : batch.Select(MessageProjector.Project).ToList();
        }

        public Task CompleteAsync(ReceivedMessageView message, CancellationToken ct) =>
            message.RawMessage is ServiceBusReceivedMessage raw
                ? _receiver.CompleteMessageAsync(raw, ct)
                : Task.CompletedTask;

        public Task AbandonAsync(ReceivedMessageView message, CancellationToken ct) =>
            message.RawMessage is ServiceBusReceivedMessage raw
                ? _receiver.AbandonMessageAsync(raw, cancellationToken: ct)
                : Task.CompletedTask;

        public ValueTask DisposeAsync() => _receiver.DisposeAsync();
    }

    private sealed class SessionHandle : IServiceBusReceiverHandle
    {
        private readonly ServiceBusSessionReceiver _receiver;

        public SessionHandle(ServiceBusSessionReceiver receiver) { _receiver = receiver; }

        public async Task<IReadOnlyList<ReceivedMessageView>> ReceiveBatchAsync(
            int maxMessages, TimeSpan perCallTimeout, CancellationToken ct)
        {
            var batch = await _receiver.ReceiveMessagesAsync(maxMessages, perCallTimeout, ct);
            return batch is null
                ? Array.Empty<ReceivedMessageView>()
                : batch.Select(MessageProjector.Project).ToList();
        }

        public async Task<IReadOnlyList<ReceivedMessageView>> PeekBatchAsync(
            int maxMessages, CancellationToken ct)
        {
            var batch = await _receiver.PeekMessagesAsync(maxMessages, cancellationToken: ct);
            return batch is null
                ? Array.Empty<ReceivedMessageView>()
                : batch.Select(MessageProjector.Project).ToList();
        }

        public Task CompleteAsync(ReceivedMessageView message, CancellationToken ct) =>
            message.RawMessage is ServiceBusReceivedMessage raw
                ? _receiver.CompleteMessageAsync(raw, ct)
                : Task.CompletedTask;

        public Task AbandonAsync(ReceivedMessageView message, CancellationToken ct) =>
            message.RawMessage is ServiceBusReceivedMessage raw
                ? _receiver.AbandonMessageAsync(raw, cancellationToken: ct)
                : Task.CompletedTask;

        public ValueTask DisposeAsync() => _receiver.DisposeAsync();
    }

    private static class MessageProjector
    {
        public static ReceivedMessageView Project(ServiceBusReceivedMessage m)
        {
            var props = new Dictionary<string, object?>(m.ApplicationProperties.Count, StringComparer.Ordinal);
            foreach (var (k, v) in m.ApplicationProperties)
                props[k] = v;

            return new ReceivedMessageView
            {
                MessageId = m.MessageId,
                CorrelationId = m.CorrelationId,
                Subject = m.Subject,
                ContentType = m.ContentType,
                ReplyTo = m.ReplyTo,
                To = m.To,
                SessionId = m.SessionId,
                EnqueuedTimeUtc = m.EnqueuedTime.UtcDateTime,
                DeliveryCount = m.DeliveryCount,
                PartitionKey = m.PartitionKey,
                ApplicationProperties = props,
                Body = m.Body?.ToArray() ?? Array.Empty<byte>(),
                RawMessage = m,
            };
        }
    }
}
