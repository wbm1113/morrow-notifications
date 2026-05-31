using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.BusinessLogic;

public class DispatchOutboxPublisherService(
    IDispatchQueue dispatchQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<DispatchOutboxPublisherService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(100);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Dispatch outbox publisher started.");

        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var outboxRepo = scope.ServiceProvider.GetRequiredService<IDispatchOutboxRepository>();
            // Claim unpublished rows (lease prevents other publisher instances taking the same row).
            var batch = await outboxRepo.TryClaimUnpublishedAcrossTenantsAsync(BatchSize, _instanceId, ct);

            if (batch.Count == 0)
            {
                await Task.Delay(IdleDelay, ct);
                continue;
            }

            logger.LogDebug(
                "Fetched {Count} unpublished outbox entr(y/ies) across {TenantCount} tenant(s).",
                batch.Count,
                batch.Select(e => e.TenantId).Distinct().Count());

            var tenantGroups = batch.GroupBy(e => e.TenantId);
            foreach (var group in tenantGroups)
                await ProcessTenantGroupAsync(group, ct);
        }

        logger.LogInformation("Dispatch outbox publisher stopped.");
    }

    private async Task ProcessTenantGroupAsync(
        IGrouping<Guid, DispatchOutboxEntry> group, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.CurrentTenantId = group.Key;

        var outboxRepo = scope.ServiceProvider.GetRequiredService<IDispatchOutboxRepository>();

        // -> DeliveryProcessorService (dispatch queue)
        foreach (var entry in group)
            await dispatchQueue.EnqueueAsync(ToDispatchMessage(entry), ct);

        // Mark published only after the whole tenant group is enqueued. Not atomic with the
        // broker — crash after enqueue but before this → republish → duplicate queue messages.
        await outboxRepo.MarkPublishedAsync(group.Select(e => e.Id).ToList(), _instanceId, ct);

        logger.LogDebug(
            "Published {Count} dispatch outbox entr(y/ies) for tenant {TenantId}.",
            group.Count(), group.Key);
    }

    private static DispatchMessage ToDispatchMessage(DispatchOutboxEntry entry) =>
        new(
            Id: entry.Id,
            OriginalMessageId: entry.OriginalMessageId,
            TenantId: entry.TenantId,
            RuleId: entry.RuleId,
            ChannelType: entry.ChannelType,
            SchemaVersion: entry.SchemaVersion,
            EventType: entry.EventType,
            Payload: entry.Payload,
            CreatedAt: entry.MessageCreatedAt);
}
