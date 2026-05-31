using MN.Core;
using MN.Entities;
using MN.Models;

namespace MN.Interfaces;

public interface IDispatchRepository
{
    /// <summary>
    /// Idempotently creates dispatch + outbox rows for matched rules in one commit.
    /// Returns the count of outbox entries awaiting publish (new or reset).
    /// </summary>
    Task<int> PrepareDispatchesForRoutingAsync(
        ProcessingMessage message,
        IReadOnlyList<RoutingRule> rules,
        CancellationToken ct);

    Task<NotificationDispatch?> GetByIdAsync(Guid tenantId, Guid dispatchId, CancellationToken ct);

    Task UpdateStatusAsync(Guid tenantId, Guid dispatchId, DispatchStatus status, CancellationToken ct);

    Task<IReadOnlyList<NotificationDispatch>> GetByOriginalMessageAsync(
        Guid tenantId, Guid originalMessageId, CancellationToken ct);
}
