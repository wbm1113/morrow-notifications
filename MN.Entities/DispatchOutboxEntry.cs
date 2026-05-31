namespace MN.Entities;

/// <summary>
/// Transactional outbox for dispatch queue publish. Written in the same commit as
/// <see cref="NotificationDispatch"/>; a background worker enqueues then marks published.
/// </summary>
public class DispatchOutboxEntry : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OriginalMessageId { get; set; }
    public Guid RuleId { get; set; }
    public string ChannelType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime MessageCreatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    /// <summary>Exclusive publish lease — set by TryClaim; cleared on MarkPublished or expiry.</summary>
    public DateTime? ClaimedUntil { get; set; }
    public string? ClaimedBy { get; set; }
}
