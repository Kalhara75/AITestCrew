using AiTestCrew.Core.Configuration;

namespace AiTestCrew.Agents.EventAssertAgent;

/// <summary>
/// Abstracts the Azure SDK's sealed receiver / sender types so the agent can
/// be unit-tested with a programmable fake. The Azure-backed implementation
/// caches a <c>ServiceBusClient</c> per <c>(namespace, authMode, MI client id)</c>
/// tuple; the test fake keeps a queue of pre-arranged
/// <see cref="ReceivedMessageView"/> objects.
/// </summary>
public interface IServiceBusReceiverFactory
{
    /// <summary>
    /// Opens a receiver against the given entity. The handle owns a
    /// <c>ServiceBusReceiver</c> on the Azure-backed implementation and
    /// must be disposed asynchronously by the caller.
    /// </summary>
    Task<IServiceBusReceiverHandle> OpenAsync(
        ServiceBusConnectionConfig connection,
        ServiceBusEntity entity,
        ReceiveMode mode,
        string? sessionId,
        CancellationToken ct);
}

/// <summary>
/// Per-call handle exposed by the factory. Wraps a single SDK receiver and
/// projects its messages into <see cref="ReceivedMessageView"/>. Disposed
/// asynchronously by the agent at the end of the receive loop; on
/// <see cref="ReceiveMode.PeekLock"/>, any locked-but-unsettled messages
/// are abandoned automatically by the Azure SDK on dispose.
/// </summary>
public interface IServiceBusReceiverHandle : IAsyncDisposable
{
    /// <summary>
    /// Receives up to <paramref name="maxMessages"/> messages, waiting up to
    /// <paramref name="perCallTimeout"/> for the first one to arrive. Returns
    /// an empty array when the timeout elapses with no message — the agent's
    /// receive loop uses this to step its overall budget.
    /// </summary>
    Task<IReadOnlyList<ReceivedMessageView>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan perCallTimeout,
        CancellationToken ct);

    /// <summary>
    /// Peeks (read-only — no consumption, no lock) up to
    /// <paramref name="maxMessages"/> messages from the head of the entity.
    /// Used by the WebApi peek endpoint and the editor's "Peek messages"
    /// preview.
    /// </summary>
    Task<IReadOnlyList<ReceivedMessageView>> PeekBatchAsync(
        int maxMessages,
        CancellationToken ct);

    /// <summary>
    /// Settles a peek-locked message as Complete (removes it from the entity).
    /// No-op when the message wasn't locked (e.g. ReceiveAndDelete mode, peek mode).
    /// </summary>
    Task CompleteAsync(ReceivedMessageView message, CancellationToken ct);

    /// <summary>
    /// Settles a peek-locked message as Abandon (returns it to the entity for
    /// the next consumer). No-op when the message wasn't locked.
    /// </summary>
    Task AbandonAsync(ReceivedMessageView message, CancellationToken ct);
}
