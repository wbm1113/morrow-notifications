using MN.Core;
using MN.Interfaces;
using MN.Models;
using static MN.Core.MessageSchemaVersion;

namespace MN.BusinessLogic;

public class IngestionService(
    ITenantRepository tenantRepository,
    IEventQueue eventQueue,
    IRateLimiterService rateLimiterService) : IIngestionService
{
    public async Task<IngestEventResponse> IngestAsync(IngestEventRequest request, CancellationToken ct)
    {
        var acquireResult = rateLimiterService.TryAcquire(request.TenantId);
        if (acquireResult == AcquireResult.TenantNotFound)
            throw new KeyNotFoundException($"Tenant '{request.TenantId}' not found.");
        if (acquireResult == AcquireResult.RateLimitExceeded)
            throw new RateLimitExceededException($"Rate limit exceeded for tenant '{request.TenantId}'.");

        var tenant = await tenantRepository.GetByIdAsync(request.TenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant '{request.TenantId}' not found.");

        // TODO: this check is currently unreachable — when a tenant is deactivated, TenantsController
        // calls RemoveTenant(), so TryAcquire() returns TenantNotFound (404) before we get here.
        // Tenant deactivation needs to be refactored to preserve the limiter entry with an "inactive"
        // state so that TryAcquire() can return a distinct result and this path can return a 422.
        // if (!tenant.IsActive)
        //     throw new InvalidOperationException($"Tenant '{request.TenantId}' is inactive.");

        var message = new ProcessingMessage(
            Id: Guid.NewGuid(),
            TenantId: request.TenantId,
            SchemaVersion: Current,
            EventType: request.EventType,
            Payload: request.Payload,
            CreatedAt: DateTime.UtcNow);

        // -> EventRoutingProcessorService (event queue)
        await eventQueue.EnqueueAsync(message, ct);

        return new IngestEventResponse(message.Id);
    }
}
