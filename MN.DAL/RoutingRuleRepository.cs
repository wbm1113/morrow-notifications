using Microsoft.EntityFrameworkCore;
using MN.Entities;
using MN.Interfaces;

namespace MN.DAL;

public class RoutingRuleRepository(AppDbContext db) : IRoutingRuleRepository
{
    public async Task<RoutingRule?> GetByIdAsync(Guid tenantId, Guid ruleId, CancellationToken ct) =>
        await db.RoutingRules.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == ruleId, ct);

    public async Task<IReadOnlyList<RoutingRule>> GetByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.RoutingRules
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoutingRule>> GetMatchingRulesAsync(Guid tenantId, string eventType, CancellationToken ct) =>
        await db.RoutingRules
            .Where(r => r.TenantId == tenantId && r.IsActive && r.EventType == eventType)
            .ToListAsync(ct);

    public async Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct)
    {
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<RoutingRule> UpdateAsync(RoutingRule rule, CancellationToken ct)
    {
        rule.UpdatedAt = DateTime.UtcNow;
        db.RoutingRules.Update(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task DeleteAsync(Guid tenantId, Guid ruleId, CancellationToken ct)
    {
        var rule = await db.RoutingRules
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == ruleId, ct);
        if (rule is not null)
        {
            db.RoutingRules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }
    }
}
