using System.ComponentModel.DataAnnotations.Schema;
using MN.Core;

namespace MN.Entities;

public class NotificationMessage : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public int SchemaVersion { get; set; } = MessageSchemaVersion.Current;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public MessageStatus Status { get; set; } = MessageStatus.Processing;
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
