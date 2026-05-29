using MN.Models;

namespace MN.Interfaces;

public interface IMessageQueue
{
    ValueTask EnqueueAsync(ProcessingMessage message, CancellationToken ct);
    ValueTask<ProcessingMessage?> DequeueAsync(CancellationToken ct);

    /// <summary>
    /// Peek-locks up to <paramref name="maxMessages"/> messages within the given
    /// <paramref name="maxWait"/> window. Locked messages are held in-flight and
    /// invisible to other consumers until completed or abandoned.
    /// Models Azure Service Bus ReceiveMessagesAsync with peek-lock mode.
    /// </summary>
    ValueTask<IReadOnlyList<PeekLock>> PeekLockBatchAsync(
        int maxMessages, TimeSpan maxWait, CancellationToken ct);

    /// <summary>Removes the message from in-flight state (processing succeeded).</summary>
    ValueTask CompleteAsync(Guid lockToken, CancellationToken ct);

    /// <summary>Returns the message to the queue for retry (processing failed transiently).</summary>
    ValueTask AbandonAsync(Guid lockToken, CancellationToken ct);
}
