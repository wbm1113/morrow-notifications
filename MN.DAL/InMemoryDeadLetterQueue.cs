using System.Collections.Concurrent;
using MN.Models;
using MN.Interfaces;

namespace MN.DAL;

public class InMemoryDeadLetterQueue : IDeadLetterQueue
{
    private readonly ConcurrentBag<DeadLetterMessage> _messages = new();

    public ValueTask SendAsync(DeadLetterMessage message, CancellationToken ct)
    {
        _messages.Add(message);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<DeadLetterMessage> GetAll() =>
        _messages.OrderByDescending(m => m.FailedAt).ToList();

    public IReadOnlyList<DeadLetterMessage> GetByTenant(Guid tenantId) =>
        _messages.Where(m => m.TenantId == tenantId)
                 .OrderByDescending(m => m.FailedAt)
                 .ToList();
}
