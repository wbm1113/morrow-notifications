using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.BusinessLogic;

public class EventRoutingProcessorService(
    IEventQueue eventQueue,
    IDeadLetterQueue deadLetterQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<EventRoutingProcessorService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxDeliveryAttempts = 3;
    private static readonly TimeSpan TimeToWaitInBetweenCheckingQueue = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Event routing processor started.");

        while (!ct.IsCancellationRequested)
        {
            // Event queue — enqueued by IngestionService.
            var batch = await eventQueue.PeekLockBatchAsync(BatchSize, TimeToWaitInBetweenCheckingQueue, ct);
            if (batch.Count == 0) continue;

            logger.LogDebug(
                "Peek-locked {Count} event(s) across {TenantCount} tenant(s).",
                batch.Count,
                batch.Select(l => l.Message.TenantId).Distinct().Count());

            var tenantGroups = batch.GroupBy(l => l.Message.TenantId);
            foreach (var group in tenantGroups)
                await ProcessTenantGroupAsync(group, ct);
        }

        logger.LogInformation("Event routing processor stopped.");
    }

    private async Task ProcessTenantGroupAsync(
        IGrouping<Guid, PeekLock<ProcessingMessage>> group, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var dispatchRepo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        var ruleRepo = scope.ServiceProvider.GetRequiredService<IRoutingRuleRepository>();
        var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.CurrentTenantId = group.Key;

        foreach (var peekLock in group)
            await ProcessEventAsync(peekLock, messageRepo, dispatchRepo, ruleRepo, tenantRepo, ct);
    }

    private async Task ProcessEventAsync(
        PeekLock<ProcessingMessage> peekLock,
        IMessageRepository messageRepo,
        IDispatchRepository dispatchRepo,
        IRoutingRuleRepository ruleRepo,
        ITenantRepository tenantRepo,
        CancellationToken ct)
    {
        var (lockToken, message) = peekLock;
        message.DeliveryAttempts++;

        await messageRepo.EnsureProcessingAsync(new NotificationMessage
        {
            Id = message.Id,
            TenantId = message.TenantId,
            SchemaVersion = message.SchemaVersion,
            EventType = message.EventType,
            Payload = message.Payload,
            Status = MessageStatus.Processing,
            CreatedAt = message.CreatedAt
        }, ct);

        try
        {
            var tenant = await tenantRepo.GetByIdAsync(message.TenantId, ct);
            if (tenant is null || !tenant.IsActive)
            {
                await DeadLetterEventAsync(lockToken, message, "Tenant not found or inactive.", messageRepo, ct);
                return;
            }

            // DAL owns the logic of rule matching which isn't ideal.  probably would want to pull
            // the rules out and pass those to a service that figures out which rules match.
            var rules = await ruleRepo.GetMatchingRulesAsync(message.TenantId, message.EventType, ct);
            if (rules.Count == 0)
            {
                await DeadLetterEventAsync(
                    lockToken, message,
                    $"No matching routing rules for event type '{message.EventType}'.",
                    messageRepo, ct);
                return;
            }

            // Transactional outbox: dispatch + outbox rows in one commit. No direct dispatch-queue
            // enqueue here — avoids orphan Pending rows if we crash before publish.
            var scheduled = await dispatchRepo.PrepareDispatchesForRoutingAsync(message, rules, ct);

            // -> DispatchOutboxPublisherService (outbox table)

            await messageRepo.UpdateStatusAsync(
                message.TenantId, message.Id, MessageStatus.FanOutComplete, null, ct);
            await eventQueue.CompleteAsync(lockToken, ct);

            logger.LogInformation(
                "Event {MessageId} routed for tenant {TenantId}: {Scheduled}/{RuleCount} dispatch(es) scheduled via outbox.",
                message.Id, message.TenantId, scheduled, rules.Count);
        }
        catch (Exception ex)
        {
            if (message.DeliveryAttempts >= MaxDeliveryAttempts)
            {
                logger.LogError(ex,
                    "Event {MessageId} exceeded {Max} routing attempts. Dead-lettering.",
                    message.Id, MaxDeliveryAttempts);
                await DeadLetterEventAsync(
                    lockToken, message,
                    $"Max routing attempts exceeded: {ex.Message}",
                    messageRepo, ct);
            }
            else
            {
                logger.LogWarning(ex,
                    "Event {MessageId} routing attempt {Attempt}/{Max} failed. Abandoning for retry.",
                    message.Id, message.DeliveryAttempts, MaxDeliveryAttempts);
                await eventQueue.AbandonAsync(lockToken, ct);
            }
        }
    }

    private async Task DeadLetterEventAsync(
        Guid lockToken,
        ProcessingMessage message,
        string reason,
        IMessageRepository messageRepo,
        CancellationToken ct)
    {
        await deadLetterQueue.SendAsync(new DeadLetterMessage
        {
            Id = Guid.NewGuid(),
            TenantId = message.TenantId,
            OriginalMessageId = message.Id,
            SchemaVersion = message.SchemaVersion,
            EventType = message.EventType,
            Payload = message.Payload,
            FailureReason = reason,
            FailedAt = DateTime.UtcNow
        }, ct);

        await messageRepo.UpdateStatusAsync(
            message.TenantId, message.Id, MessageStatus.DeadLettered, reason, ct);
        await eventQueue.CompleteAsync(lockToken, ct);

        logger.LogWarning(
            "Event {MessageId} for tenant {TenantId} dead-lettered during routing: {Reason}",
            message.Id, message.TenantId, reason);
    }
}
