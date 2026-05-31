using MN.Entities;

namespace MN.Interfaces;

public interface IRoutingRuleRepository
{
    Task<RoutingRule?> GetByIdAsync(Guid tenantId, Guid ruleId, CancellationToken ct);
    Task<IReadOnlyList<RoutingRule>> GetByTenantAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<RoutingRule>> GetMatchingRulesAsync(Guid tenantId, string eventType, CancellationToken ct);
    Task<RoutingRule?> GetByEventTypeAndChannelAsync(Guid tenantId, string eventType, string channelType, CancellationToken ct);
    Task<RoutingRule> CreateAsync(RoutingRule rule, CancellationToken ct);
    Task<RoutingRule> UpdateAsync(RoutingRule rule, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, Guid ruleId, CancellationToken ct);
}
