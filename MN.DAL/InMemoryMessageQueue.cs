using System.Collections.Concurrent;
using System.Threading.Channels;
using MN.Interfaces;

namespace MN.DAL;

public class InMemoryMessageQueue<TMessage> : IMessageQueue<TMessage> where TMessage : class
{
    private readonly Channel<TMessage> _channel =
        Channel.CreateUnbounded<TMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Guid, TMessage> _inFlight = new();
    private readonly Lock _delayedLock = new();
    private readonly List<(DateTime AvailableAt, TMessage Message)> _delayed = [];

    public async ValueTask EnqueueAsync(TMessage message, CancellationToken ct) =>
        await _channel.Writer.WriteAsync(message, ct);

    public async ValueTask<IReadOnlyList<PeekLock<TMessage>>> PeekLockBatchAsync(
        int maxMessages, TimeSpan maxWait, CancellationToken ct)
    {
        ReleaseDueDelayedMessages();

        var batch = new List<PeekLock<TMessage>>(maxMessages);

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(maxWait);

        try
        {
            if (!await _channel.Reader.WaitToReadAsync(windowCts.Token))
                return batch;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return batch;
        }

        while (batch.Count < maxMessages && _channel.Reader.TryRead(out var message))
        {
            var lockToken = Guid.NewGuid();
            _inFlight[lockToken] = message;
            batch.Add(new PeekLock<TMessage>(lockToken, message));
        }

        return batch;
    }

    public ValueTask CompleteAsync(Guid lockToken, CancellationToken ct)
    {
        _inFlight.TryRemove(lockToken, out _);
        return ValueTask.CompletedTask;
    }

    public async ValueTask AbandonAsync(Guid lockToken, CancellationToken ct, TimeSpan visibilityDelay = default)
    {
        if (!_inFlight.TryRemove(lockToken, out var message))
            return;

        if (visibilityDelay <= TimeSpan.Zero)
        {
            // CancellationToken.None is intentional: the caller's token may already be cancelled
            // (e.g. host shutdown), but we must still return the message to the queue to avoid loss.
            await _channel.Writer.WriteAsync(message, CancellationToken.None);
            return;
        }

        lock (_delayedLock)
            _delayed.Add((DateTime.UtcNow + visibilityDelay, message));
    }

    private void ReleaseDueDelayedMessages()
    {
        var now = DateTime.UtcNow;

        lock (_delayedLock)
        {
            for (var i = _delayed.Count - 1; i >= 0; i--)
            {
                if (_delayed[i].AvailableAt > now)
                    continue;

                _channel.Writer.TryWrite(_delayed[i].Message);
                _delayed.RemoveAt(i);
            }
        }
    }
}

public sealed class InMemoryEventQueue : InMemoryMessageQueue<MN.Models.ProcessingMessage>, IEventQueue;

public sealed class InMemoryDispatchQueue : InMemoryMessageQueue<MN.Models.DispatchMessage>, IDispatchQueue;
