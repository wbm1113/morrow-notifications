using System.Collections.Concurrent;
using System.Threading.Channels;
using MN.Interfaces;
using MN.Models;

namespace MN.DAL;

public class InMemoryMessageQueue : IMessageQueue
{
    private readonly Channel<ProcessingMessage> _channel =
        Channel.CreateUnbounded<ProcessingMessage>(new UnboundedChannelOptions { SingleReader = true });

    // In-flight messages: locked after peek, pending Complete or Abandon.
    // Stand-in for Azure Service Bus peek-lock state.
    private readonly ConcurrentDictionary<Guid, ProcessingMessage> _inFlight = new();

    public async ValueTask EnqueueAsync(ProcessingMessage message, CancellationToken ct) =>
        await _channel.Writer.WriteAsync(message, ct);

    public async ValueTask<ProcessingMessage?> DequeueAsync(CancellationToken ct)
    {
        try
        {
            return await _channel.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<PeekLock>> PeekLockBatchAsync(
        int maxMessages, TimeSpan maxWait, CancellationToken ct)
    {
        var batch = new List<PeekLock>(maxMessages);

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
            batch.Add(new PeekLock(lockToken, message));
        }

        return batch;
    }

    public ValueTask CompleteAsync(Guid lockToken, CancellationToken ct)
    {
        _inFlight.TryRemove(lockToken, out _);
        return ValueTask.CompletedTask;
    }

    public async ValueTask AbandonAsync(Guid lockToken, CancellationToken ct)
    {
        if (_inFlight.TryRemove(lockToken, out var message))
            await _channel.Writer.WriteAsync(message, ct);
    }
}
