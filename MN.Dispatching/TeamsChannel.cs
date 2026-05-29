using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.Dispatching;

/// <summary>
/// Stub Teams channel. Replace the body of SendAsync with an Adaptive Card POST
/// to the tenant's incoming webhook URL when ready.
/// </summary>
public class TeamsChannel(ILogger<TeamsChannel> logger) : INotificationChannel
{
    public string ChannelType => ChannelTypes.Teams;

    public Task SendAsync(ProcessingMessage message, RoutingRule rule, CancellationToken ct)
    {
        logger.LogInformation(
            "[TEAMS CHANNEL - STUB] TenantId={TenantId} MessageId={MessageId} EventType={EventType}",
            message.TenantId, message.Id, message.EventType);

        return Task.CompletedTask;
    }
}
