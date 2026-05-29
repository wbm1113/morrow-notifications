namespace MN.Models;

/// <summary>
/// In-flight view of a message as it moves through the queue, processor, and channel layers.
/// Kept separate from the EF entity so the EF change-tracker and nav properties
/// never leak outside the DAL boundary.
/// Class (not record) because DeliveryAttempts is mutated in place during processing;
/// value semantics and structural equality would be misleading for a long-lived mutable object.
/// </summary>
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
    /// Incremented per delivery attempt. Not persisted — mirrors ASB DeliveryCount semantics.
    /// </summary>
    public int DeliveryAttempts { get; set; } = 0;
}
