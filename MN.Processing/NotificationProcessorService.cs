using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.Processing;

public class NotificationProcessorService(
    IMessageQueue queue,
    IDeadLetterQueue deadLetterQueue,
    IMessageDispatcher dispatcher,
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationProcessorService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxDeliveryAttempts = 3;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Notification processor started.");

        while (!ct.IsCancellationRequested)
        {
            var batch = await queue.PeekLockBatchAsync(BatchSize, BatchWindow, ct);
            if (batch.Count == 0) continue;

            logger.LogDebug(
                "Peek-locked batch of {Count} message(s) across {TenantCount} tenant(s).",
                batch.Count,
                batch.Select(l => l.Message.TenantId).Distinct().Count());

            // Group by tenant and process each group in its own isolated DI scope.
            // Sequential (not Task.WhenAll) because SQLite serializes writers. In production
            // against SQL Server / PostgreSQL, replace the foreach with Task.WhenAll for
            // true tenant-parallel fan-out and noisy-neighbor isolation within the batch.
            var tenantGroups = batch.GroupBy(l => l.Message.TenantId);
            foreach (var group in tenantGroups)
                await ProcessTenantGroupAsync(group, ct);
        }

        logger.LogInformation("Notification processor stopped.");
    }

    private async Task ProcessTenantGroupAsync(
        IGrouping<Guid, PeekLock> group, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var ruleRepo = scope.ServiceProvider.GetRequiredService<IRoutingRuleRepository>();
        var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.CurrentTenantId = group.Key;

        foreach (var peekLock in group)
            await ProcessMessageAsync(peekLock, messageRepo, ruleRepo, tenantRepo, ct);
    }

    private async Task ProcessMessageAsync(
        PeekLock peekLock,
        IMessageRepository messageRepo,
        IRoutingRuleRepository ruleRepo,
        ITenantRepository tenantRepo,
        CancellationToken ct)
    {
        var (lockToken, message) = peekLock;

        // Increment once per attempt, regardless of outcome — mirrors ASB DeliveryCount semantics.
        message.DeliveryAttempts++;

        // First attempt creates the DB record; retries update status back to Processing.
        if (message.DeliveryAttempts == 1)
        {
            await messageRepo.CreateAsync(new NotificationMessage
            {
                Id = message.Id,
                TenantId = message.TenantId,
                SchemaVersion = message.SchemaVersion,
                EventType = message.EventType,
                Payload = message.Payload,
                Status = MessageStatus.Processing,
                CreatedAt = message.CreatedAt
            }, ct);
        }
        else
        {
            await messageRepo.UpdateStatusAsync(message.TenantId, message.Id, MessageStatus.Processing, null, ct);
        }

        try
        {
            var tenant = await tenantRepo.GetByIdAsync(message.TenantId, ct);
            if (tenant is null || !tenant.IsActive)
            {
                await DeadLetterAsync(lockToken, message, "Tenant not found or inactive.", messageRepo, ct);
                return;
            }

            var rules = await ruleRepo.GetMatchingRulesAsync(message.TenantId, message.EventType, ct);
            if (rules.Count == 0)
            {
                await DeadLetterAsync(lockToken, message, $"No matching routing rules for event type '{message.EventType}'.", messageRepo, ct);
                return;
            }

            await dispatcher.DispatchAsync(message, rules, ct);

            await messageRepo.UpdateStatusAsync(message.TenantId, message.Id, MessageStatus.Dispatched, null, ct);
            await queue.CompleteAsync(lockToken, ct);

            logger.LogInformation(
                "Message {MessageId} dispatched for tenant {TenantId} via {RuleCount} rule(s).",
                message.Id, message.TenantId, rules.Count);
        }
        catch (Exception ex)
        {
            if (message.DeliveryAttempts >= MaxDeliveryAttempts)
            {
                logger.LogError(ex,
                    "Message {MessageId} exceeded {Max} delivery attempts. Dead-lettering.",
                    message.Id, MaxDeliveryAttempts);
                await DeadLetterAsync(lockToken, message, $"Max delivery attempts exceeded: {ex.Message}", messageRepo, ct);
            }
            else
            {
                logger.LogWarning(ex,
                    "Message {MessageId} attempt {Attempt}/{Max} failed. Abandoning for retry.",
                    message.Id, message.DeliveryAttempts, MaxDeliveryAttempts);
                await queue.AbandonAsync(lockToken, ct);
            }
        }
    }

    private async Task DeadLetterAsync(
        Guid lockToken,
        ProcessingMessage message,
        string reason,
        IMessageRepository messageRepo,
        CancellationToken ct)
    {
        var dlm = new DeadLetterMessage
        {
            Id = Guid.NewGuid(),
            TenantId = message.TenantId,
            OriginalMessageId = message.Id,
            SchemaVersion = message.SchemaVersion,
            EventType = message.EventType,
            Payload = message.Payload,
            FailureReason = reason,
            FailedAt = DateTime.UtcNow
        };

        await deadLetterQueue.SendAsync(dlm, ct);
        await messageRepo.UpdateStatusAsync(message.TenantId, message.Id, MessageStatus.DeadLettered, reason, ct);
        await queue.CompleteAsync(lockToken, ct);

        logger.LogWarning(
            "Message {MessageId} for tenant {TenantId} sent to dead letter: {Reason}",
            message.Id, message.TenantId, reason);
    }
}

