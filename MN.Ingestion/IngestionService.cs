using MN.Core;
using MN.Interfaces;
using MN.Models;
using static MN.Core.MessageSchemaVersion;

namespace MN.Ingestion;

public class IngestionService(
    ITenantRepository tenantRepository,
    IMessageQueue messageQueue) : IIngestionService
{
    public async Task<IngestEventResponse> IngestAsync(IngestEventRequest request, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(request.TenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant '{request.TenantId}' not found.");

        if (!tenant.IsActive)
            throw new InvalidOperationException($"Tenant '{request.TenantId}' is inactive.");

        var message = new ProcessingMessage(
            Id: Guid.NewGuid(),
            TenantId: request.TenantId,
            SchemaVersion: Current,
            EventType: request.EventType,
            Payload: request.Payload,
            CreatedAt: DateTime.UtcNow);

        await messageQueue.EnqueueAsync(message, ct);

        return new IngestEventResponse(message.Id);
    }
}
