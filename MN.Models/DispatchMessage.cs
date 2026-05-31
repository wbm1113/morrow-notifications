namespace MN.Models;

public record DispatchMessage(
    Guid Id,
    Guid OriginalMessageId,
    Guid TenantId,
    Guid RuleId,
    string ChannelType,
    int SchemaVersion,
    string EventType,
    string Payload,
    DateTime CreatedAt)
{
    public int DeliveryAttempts { get; set; }

    public static Guid CreateId(Guid originalMessageId, Guid ruleId)
    {
        Span<byte> bytes = stackalloc byte[32];
        originalMessageId.TryWriteBytes(bytes[..16]);
        ruleId.TryWriteBytes(bytes[16..]);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return new Guid(hash[..16]);
    }
}
