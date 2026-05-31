using System.ComponentModel.DataAnnotations.Schema;

namespace MN.Entities;

/// <summary>
/// Maps an event type to a channel for a tenant. Unique (TenantId, EventType, ChannelType)
/// allows multi-channel dispatch per ingest while preventing duplicate rules per channel.
/// </summary>
public class RoutingRule : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string EventType { get; set; } = string.Empty;

    public string ChannelType { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;

    // below - possible routing rule implementations, finalization subject to
    // further requirements gathering.  who are we building this for and why?
    // public ICollection<RoutingRuleChannelConfig> ChannelConfigs { get; set; } = new List<RoutingRuleChannelConfig>();
    // public ICollection<RoutingRulePayloadCriteria> PayloadCriteria { get; set; } = new List<RoutingRulePayloadCriteria>();
}

public class RoutingRuleChannelConfig : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid RoutingRuleId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Config key, e.g. "webhook_url", "channel", "connector_url".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Config value. Stored as plaintext; secrets should be replaced with a vault reference in production.</summary>
    public string Value { get; set; } = string.Empty;

    public RoutingRule RoutingRule { get; set; } = null!;
}

public enum PayloadCriteriaOperator 
{
    Equals,
    StartsWith,
    EndsWith
}

public class RoutingRulePayloadCriteria : ITenantScoped 
{
    public Guid Id { get; set; }
    public Guid RoutingRuleId { get; set; }
    public Guid TenantId { get; set; }

    // e.g. data.order.id
    public string PayloadPath { get; set; }
    public PayloadCriteriaOperator Operator { get; set; }
    public string Value { get; set; }
}


