namespace MN.Models;

public record DeadLetterResponse(
    Guid Id,
    Guid TenantId,
    Guid OriginalMessageId,
    Guid? DispatchId,
    string? ChannelType,
    string EventType,
    string Payload,
    string FailureReason,
    DateTime FailedAt);
