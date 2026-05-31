using MN.Core;

namespace MN.Entities;

public class NotificationDispatch : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OriginalMessageId { get; set; }
    public Guid RuleId { get; set; }
    public string ChannelType { get; set; } = string.Empty;
    public DispatchStatus Status { get; set; } = DispatchStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
