namespace MN.Models;

public class DeadLetterMessage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OriginalMessageId { get; set; }
    public Guid? DispatchId { get; set; }
    public string? ChannelType { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime FailedAt { get; set; } = DateTime.UtcNow;
}
