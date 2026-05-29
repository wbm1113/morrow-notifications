namespace MN.Models;

public record CreateRoutingRuleRequest(
    string EventType,
    string ChannelType);

public record UpdateRoutingRuleRequest(
    string? EventType,
    string? ChannelType,
    bool? IsActive);

public record RoutingRuleResponse(
    Guid Id,
    Guid TenantId,
    string EventType,
    string ChannelType,
    bool IsActive,
    DateTime CreatedAt);
