using MN.Models;

namespace MN.Interfaces;

public interface IDeadLetterQueue
{
    ValueTask SendAsync(DeadLetterMessage message, CancellationToken ct);
    IReadOnlyList<DeadLetterMessage> GetAll();
    IReadOnlyList<DeadLetterMessage> GetByTenant(Guid tenantId);
}
