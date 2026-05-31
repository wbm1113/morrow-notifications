using Microsoft.EntityFrameworkCore;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.DAL;

public class DispatchRepository(AppDbContext db) : IDispatchRepository
{
    public async Task<int> PrepareDispatchesForRoutingAsync(
        ProcessingMessage message,
        IReadOnlyList<RoutingRule> rules,
        CancellationToken ct)
    {
        var existingDispatches = await db.Dispatches
            .Where(d => d.TenantId == message.TenantId && d.OriginalMessageId == message.Id)
            .ToDictionaryAsync(d => d.Id, ct);

        var existingOutbox = await db.DispatchOutbox
            .Where(o => o.TenantId == message.TenantId && o.OriginalMessageId == message.Id)
            .ToDictionaryAsync(o => o.Id, ct);

        var scheduled = 0;
        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            var dispatchId = DispatchMessage.CreateId(message.Id, rule.Id);
            existingOutbox.TryGetValue(dispatchId, out var outbox);

            if (existingDispatches.TryGetValue(dispatchId, out var dispatch))
            {
                switch (dispatch.Status)
                {
                    // Channel already delivered — no outbox touch; idempotent on event redelivery.
                    case DispatchStatus.Succeeded:
                        continue;

                    case DispatchStatus.Pending:
                        if (outbox is null)
                        {
                            // Orphan recovery: dispatch row exists but outbox insert never committed.
                            AddOutboxEntry(message, rule, dispatchId, now);
                            scheduled++;
                        }
                        else if (outbox.PublishedAt is null)
                        {
                            // Outbox exists, not yet published — publisher will pick it up; do not duplicate.
                        }
                        else
                        {
                            // Outbox published — item is on (or was on) the dispatch queue; delivery owns completion.
                        }

                        continue;

                    case DispatchStatus.Failed:
                        // Manual replay only: re-routing the same event (e.g. operational re-enqueue).
                        // Normal delivery retries stay on the dispatch queue (DeliveryAttempts, max 3)
                        // and end in Failed without touching this path. Reset to Pending and republish outbox.
                        dispatch.Status = DispatchStatus.Pending;
                        dispatch.ProcessedAt = null;
                        scheduled += EnsureUnpublishedOutbox(message, rule, dispatchId, outbox, now);
                        continue;
                }

                continue;
            }

            db.Dispatches.Add(new NotificationDispatch
            {
                Id = dispatchId,
                TenantId = message.TenantId,
                OriginalMessageId = message.Id,
                RuleId = rule.Id,
                ChannelType = rule.ChannelType,
                Status = DispatchStatus.Pending,
                CreatedAt = now
            });

            AddOutboxEntry(message, rule, dispatchId, now);
            scheduled++;
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        return scheduled;
    }

    private int EnsureUnpublishedOutbox(
        ProcessingMessage message,
        RoutingRule rule,
        Guid dispatchId,
        DispatchOutboxEntry? outbox,
        DateTime now)
    {
        if (outbox is null)
        {
            AddOutboxEntry(message, rule, dispatchId, now);
            return 1;
        }

        if (outbox.PublishedAt is not null)
        {
            outbox.PublishedAt = null;
            SyncOutboxPayload(outbox, message, rule);
            return 1;
        }

        return 0;
    }

    private void AddOutboxEntry(ProcessingMessage message, RoutingRule rule, Guid dispatchId, DateTime now)
    {
        db.DispatchOutbox.Add(new DispatchOutboxEntry
        {
            Id = dispatchId,
            TenantId = message.TenantId,
            OriginalMessageId = message.Id,
            RuleId = rule.Id,
            ChannelType = rule.ChannelType,
            SchemaVersion = message.SchemaVersion,
            EventType = message.EventType,
            Payload = message.Payload,
            MessageCreatedAt = message.CreatedAt,
            CreatedAt = now
        });
    }

    private static void SyncOutboxPayload(DispatchOutboxEntry outbox, ProcessingMessage message, RoutingRule rule)
    {
        outbox.RuleId = rule.Id;
        outbox.ChannelType = rule.ChannelType;
        outbox.SchemaVersion = message.SchemaVersion;
        outbox.EventType = message.EventType;
        outbox.Payload = message.Payload;
        outbox.MessageCreatedAt = message.CreatedAt;
    }

    public async Task<NotificationDispatch?> GetByIdAsync(Guid tenantId, Guid dispatchId, CancellationToken ct) =>
        await db.Dispatches.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == dispatchId, ct);

    public async Task UpdateStatusAsync(Guid tenantId, Guid dispatchId, DispatchStatus status, CancellationToken ct)
    {
        var dispatch = await db.Dispatches.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == dispatchId, ct);
        if (dispatch is null) return;

        dispatch.Status = status;
        dispatch.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationDispatch>> GetByOriginalMessageAsync(
        Guid tenantId, Guid originalMessageId, CancellationToken ct) =>
        await db.Dispatches
            .Where(d => d.TenantId == tenantId && d.OriginalMessageId == originalMessageId)
            .ToListAsync(ct);
}
