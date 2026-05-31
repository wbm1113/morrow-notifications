namespace MN.Models;


public class ProcessingMessage(
    Guid Id,
    Guid TenantId,
    int SchemaVersion,
    string EventType,
    string Payload,
    DateTime CreatedAt)
{
    public Guid Id { get; } = Id;
    public Guid TenantId { get; } = TenantId;
    public int SchemaVersion { get; } = SchemaVersion;
    public string EventType { get; } = EventType;
    public string Payload { get; } = Payload;
    public DateTime CreatedAt { get; } = CreatedAt;

    /// <summary>
    /// Incremented per delivery attempt. On ASB, initialize from
    /// <c>ServiceBusReceivedMessage.DeliveryCount</c> when building this model.
    /// <see cref="Id"/> is the idempotency key for DB upserts (set ASB MessageId to the same
    /// value on send for correlation; broker envelope id is not the business message identity).
    /// </summary>
    public int DeliveryAttempts { get; set; } = 0;
}
