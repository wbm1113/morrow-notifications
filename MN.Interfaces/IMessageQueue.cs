namespace MN.Interfaces;

public interface IMessageQueue<TMessage> where TMessage : class
{
    ValueTask EnqueueAsync(TMessage message, CancellationToken ct);

    /// <summary>
    /// Peek-locks up to <paramref name="maxMessages"/> messages within the given
    /// <paramref name="maxWait"/> window. Locked messages are held in-flight and
    /// invisible to other consumers until completed or abandoned.
    /// Models Azure Service Bus ReceiveMessagesAsync with peek-lock mode.
    /// </summary>
    ValueTask<IReadOnlyList<PeekLock<TMessage>>> PeekLockBatchAsync(
        int maxMessages, TimeSpan maxWait, CancellationToken ct);

    ValueTask CompleteAsync(Guid lockToken, CancellationToken ct);

    /// <summary>
    /// Returns the message to the queue for retry. When <paramref name="visibilityDelay"/>
    /// is non-zero, the message is held invisible until the delay elapses — models ASB
    /// scheduled redelivery and prevents immediate hot-loop reprocessing.
    /// </summary>
    ValueTask AbandonAsync(Guid lockToken, CancellationToken ct, TimeSpan visibilityDelay = default);
}

public interface IEventQueue : IMessageQueue<MN.Models.ProcessingMessage> { }

public interface IDispatchQueue : IMessageQueue<MN.Models.DispatchMessage> { }
