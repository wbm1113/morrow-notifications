using Microsoft.EntityFrameworkCore;
using MN.Core;
using MN.Entities;
using MN.Interfaces;

namespace MN.DAL;

public class MessageRepository(AppDbContext db) : IMessageRepository
{
    public async Task<NotificationMessage> CreateAsync(NotificationMessage message, CancellationToken ct)
    {
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task EnsureProcessingAsync(NotificationMessage message, CancellationToken ct)
    {
        var existing = await db.Messages
            .FirstOrDefaultAsync(m => m.TenantId == message.TenantId && m.Id == message.Id, ct);

        if (existing is null)
        {
            db.Messages.Add(message);
        }
        else
        {
            existing.Status = MessageStatus.Processing;
            existing.FailureReason = null;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<NotificationMessage?> GetByIdAsync(Guid tenantId, Guid messageId, CancellationToken ct) =>
        await db.Messages.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == messageId, ct);

    public async Task UpdateStatusAsync(Guid tenantId, Guid messageId, MessageStatus status, string? failureReason, CancellationToken ct)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == messageId, ct);
        if (message is null) return;

        message.Status = status;
        message.FailureReason = failureReason;
        message.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationMessage>> GetByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.Messages
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
}
