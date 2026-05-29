namespace MN.Models;

public record DeadLetterResponse(
    Guid Id,
    Guid TenantId,
    Guid OriginalMessageId,
    string EventType,
    string Payload,
    string FailureReason,
    DateTime FailedAt);
