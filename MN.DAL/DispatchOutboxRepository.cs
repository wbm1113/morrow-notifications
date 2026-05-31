using Microsoft.EntityFrameworkCore;
using MN.Core;
using MN.Entities;
using MN.Interfaces;

namespace MN.DAL;

public class DispatchOutboxRepository(AppDbContext db) : IDispatchOutboxRepository
{
    public async Task<IReadOnlyList<DispatchOutboxEntry>> TryClaimUnpublishedAcrossTenantsAsync(
        int max, string claimerId, CancellationToken ct)
    {
        if (max <= 0)
            return [];

        var now = DateTime.UtcNow;
        var leaseUntil = now.Add(OutboxClaimConstants.ClaimLeaseDuration);

        // Batch claim via ExecuteUpdate — EF translates to a single UPDATE … LIMIT on SQLite.
        await db.DispatchOutbox
            .Where(o => o.PublishedAt == null
                        && (o.ClaimedUntil == null || o.ClaimedUntil < now))
            .OrderBy(o => o.CreatedAt)
            .Take(max)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(o => o.ClaimedUntil, leaseUntil)
                    .SetProperty(o => o.ClaimedBy, claimerId),
                ct);

        return await db.DispatchOutbox
            .Where(o => o.ClaimedBy == claimerId
                        && o.PublishedAt == null
                        && o.ClaimedUntil == leaseUntil)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);
    }
    
    public async Task MarkPublishedAsync(IReadOnlyList<Guid> ids, string claimerId, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var now = DateTime.UtcNow;
        var entries = await db.DispatchOutbox
            .Where(o => ids.Contains(o.Id)
                        && o.PublishedAt == null
                        && o.ClaimedBy == claimerId)
            .ToListAsync(ct);

        foreach (var entry in entries)
        {
            entry.PublishedAt = now;
            entry.ClaimedUntil = null;
            entry.ClaimedBy = null;
        }

        if (entries.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
