using MN.Entities;
using MN.Core;

namespace MN.Interfaces;

public interface IMessageRepository
{
    Task<NotificationMessage> CreateAsync(NotificationMessage message, CancellationToken ct);
    Task<NotificationMessage?> GetByIdAsync(Guid tenantId, Guid messageId, CancellationToken ct);
    Task UpdateStatusAsync(Guid tenantId, Guid messageId, MessageStatus status, string? failureReason, CancellationToken ct);
    Task<IReadOnlyList<NotificationMessage>> GetByTenantAsync(Guid tenantId, CancellationToken ct);
}
