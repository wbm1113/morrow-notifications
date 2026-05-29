namespace MN.Models;

public record IngestEventRequest(
    Guid TenantId,
    string EventType,
    string Payload);

public record IngestEventResponse(Guid MessageId);
