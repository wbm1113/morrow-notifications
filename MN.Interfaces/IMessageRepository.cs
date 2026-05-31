using MN.Entities;
using MN.Core;

namespace MN.Interfaces;

public interface IMessageRepository
{
    Task<NotificationMessage> CreateAsync(NotificationMessage message, CancellationToken ct);

    /// <summary>
    /// Idempotent write keyed by (TenantId, Id). Inserts on first processing attempt;
    /// on redelivery resets status to Processing without duplicating the row.
    /// </summary>
    Task EnsureProcessingAsync(NotificationMessage message, CancellationToken ct);

    Task<NotificationMessage?> GetByIdAsync(Guid tenantId, Guid messageId, CancellationToken ct);
    Task UpdateStatusAsync(Guid tenantId, Guid messageId, MessageStatus status, string? failureReason, CancellationToken ct);
    Task<IReadOnlyList<NotificationMessage>> GetByTenantAsync(Guid tenantId, CancellationToken ct);
}
