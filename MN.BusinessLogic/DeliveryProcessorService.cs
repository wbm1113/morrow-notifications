using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Interfaces;
using MN.Models;

namespace MN.BusinessLogic;

public class DeliveryProcessorService(
    IDispatchQueue dispatchQueue,
    IDeadLetterQueue deadLetterQueue,
    IMessageDispatcher dispatcher,
    IRateLimiterService rateLimiterService,
    IServiceScopeFactory scopeFactory,
    ILogger<DeliveryProcessorService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxDeliveryAttempts = 3;
    private static readonly TimeSpan TimeToWaitInBetweenCheckingQueue = TimeSpan.FromMilliseconds(100);
    // Stand-in for ASB ScheduledEnqueueTimeUtc — hold the message off-queue so rate-limited
    // abandons do not hot-loop. Does not consume delivery attempts.
    private static readonly TimeSpan RateLimitDeferralDurationToStopHotLooping =
        RateLimitConstants.DeliveryDeferralDurationToStopHotLooping;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Delivery processor started.");

        while (!ct.IsCancellationRequested)
        {
            // Dispatch queue items enqueued by DispatchOutboxPublisherService.
            var batch = await dispatchQueue.PeekLockBatchAsync(BatchSize, TimeToWaitInBetweenCheckingQueue, ct);
            if (batch.Count == 0) continue;

            logger.LogDebug(
                "Peek-locked {Count} dispatch item(s) across {TenantCount} tenant(s).",
                batch.Count,
                batch.Select(l => l.Message.TenantId).Distinct().Count());

            var tenantGroups = batch.GroupBy(l => l.Message.TenantId);
            foreach (var group in tenantGroups)
                await ProcessTenantGroupAsync(group, ct);
        }

        logger.LogInformation("Delivery processor stopped.");
    }

    private async Task ProcessTenantGroupAsync(
        IGrouping<Guid, PeekLock<DispatchMessage>> group, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dispatchRepo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var ruleRepo = scope.ServiceProvider.GetRequiredService<IRoutingRuleRepository>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.CurrentTenantId = group.Key;

        foreach (var peekLock in group)
            await ProcessDispatchAsync(peekLock, dispatchRepo, messageRepo, ruleRepo, ct);
    }

    private async Task ProcessDispatchAsync(
        PeekLock<DispatchMessage> peekLock,
        IDispatchRepository dispatchRepo,
        IMessageRepository messageRepo,
        IRoutingRuleRepository ruleRepo,
        CancellationToken ct)
    {
        var (lockToken, dispatch) = peekLock;

        var acquireResult = rateLimiterService.TryAcquire(dispatch.TenantId);
        if (acquireResult == AcquireResult.RateLimitExceeded)
        {
            logger.LogDebug(
                "Dispatch {DispatchId} rate-limited for tenant {TenantId}. Requeueing after {Delay}.",
                dispatch.Id, dispatch.TenantId, RateLimitDeferralDurationToStopHotLooping);
            await dispatchQueue.AbandonAsync(lockToken, ct, RateLimitDeferralDurationToStopHotLooping);
            return;
        }

        dispatch.DeliveryAttempts++;

        try
        {
            var existing = await dispatchRepo.GetByIdAsync(dispatch.TenantId, dispatch.Id, ct);
            // Publisher enqueues then sets PublishedAt — not atomic with the broker. Crash in between
            // leaves the row unpublished, so republish puts the same dispatch on the queue again.
            // Skip send if this dispatch already succeeded.
            if (existing?.Status == DispatchStatus.Succeeded)
            {
                await dispatchQueue.CompleteAsync(lockToken, ct);
                return;
            }

            var rule = await ruleRepo.GetByIdAsync(dispatch.TenantId, dispatch.RuleId, ct);
            if (rule is null || !rule.IsActive)
            {
                await DeadLetterDispatchAsync(
                    lockToken, dispatch,
                    $"Routing rule '{dispatch.RuleId}' not found or inactive.",
                    dispatchRepo, messageRepo, ct);
                return;
            }

            // -> MessageDispatcher
            await dispatcher.DispatchAsync(dispatch, rule, ct);

            // At-least-once gap: crash after send but before Succeeded → redelivery still sees Pending
            // and may send again. Tolerated on purpose — prefer a rare duplicate over losing the send.
            await dispatchRepo.UpdateStatusAsync(
                dispatch.TenantId, dispatch.Id, DispatchStatus.Succeeded, ct);
            await dispatchQueue.CompleteAsync(lockToken, ct);
            await UpdateParentMessageStatusAsync(dispatch, dispatchRepo, messageRepo, ct);

            logger.LogInformation(
                "Dispatch {DispatchId} delivered for event {MessageId} via {ChannelType}.",
                dispatch.Id, dispatch.OriginalMessageId, dispatch.ChannelType);
        }
        catch (Exception ex)
        {
            if (dispatch.DeliveryAttempts >= MaxDeliveryAttempts)
            {
                logger.LogError(ex,
                    "Dispatch {DispatchId} exceeded {Max} delivery attempts. Dead-lettering.",
                    dispatch.Id, MaxDeliveryAttempts);
                await DeadLetterDispatchAsync(
                    lockToken, dispatch,
                    $"Max delivery attempts exceeded: {ex.Message}",
                    dispatchRepo, messageRepo, ct);
            }
            else
            {
                logger.LogWarning(ex,
                    "Dispatch {DispatchId} attempt {Attempt}/{Max} failed. Abandoning for retry.",
                    dispatch.Id, dispatch.DeliveryAttempts, MaxDeliveryAttempts);
                await dispatchQueue.AbandonAsync(lockToken, ct);
            }
        }
    }

    private async Task DeadLetterDispatchAsync(
        Guid lockToken,
        DispatchMessage dispatch,
        string reason,
        IDispatchRepository dispatchRepo,
        IMessageRepository messageRepo,
        CancellationToken ct)
    {
        await deadLetterQueue.SendAsync(new DeadLetterMessage
        {
            Id = Guid.NewGuid(),
            TenantId = dispatch.TenantId,
            OriginalMessageId = dispatch.OriginalMessageId,
            DispatchId = dispatch.Id,
            ChannelType = dispatch.ChannelType,
            SchemaVersion = dispatch.SchemaVersion,
            EventType = dispatch.EventType,
            Payload = dispatch.Payload,
            FailureReason = reason,
            FailedAt = DateTime.UtcNow
        }, ct);

        await dispatchRepo.UpdateStatusAsync(
            dispatch.TenantId, dispatch.Id, DispatchStatus.Failed, ct);
        await dispatchQueue.CompleteAsync(lockToken, ct);
        await UpdateParentMessageStatusAsync(dispatch, dispatchRepo, messageRepo, ct);

        logger.LogWarning(
            "Dispatch {DispatchId} for event {MessageId} dead-lettered: {Reason}",
            dispatch.Id, dispatch.OriginalMessageId, reason);
    }

    private static async Task UpdateParentMessageStatusAsync(
        DispatchMessage dispatch,
        IDispatchRepository dispatchRepo,
        IMessageRepository messageRepo,
        CancellationToken ct)
    {
        var dispatches = await dispatchRepo.GetByOriginalMessageAsync(
            dispatch.TenantId, dispatch.OriginalMessageId, ct);

        if (dispatches.Any(d => d.Status == DispatchStatus.Pending))
            return;

        var succeeded = dispatches.Count(d => d.Status == DispatchStatus.Succeeded);
        var failed = dispatches.Count(d => d.Status == DispatchStatus.Failed);

        var status = failed == 0
            ? MessageStatus.Dispatched
            : succeeded > 0
                ? MessageStatus.PartiallyDispatched
                : MessageStatus.DeliveryFailed;

        string? failureReason = failed > 0
            ? $"{failed} of {dispatches.Count} channel dispatch(es) failed."
            : null;

        await messageRepo.UpdateStatusAsync(
            dispatch.TenantId, dispatch.OriginalMessageId, status, failureReason, ct);
    }
}
