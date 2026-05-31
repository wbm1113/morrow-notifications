using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.BusinessLogic;

/// <summary>
/// Stub Teams channel. Replace the body of SendAsync with an Adaptive Card POST
/// to the tenant's incoming webhook URL when ready.
/// </summary>
public class TeamsChannel(ILogger<TeamsChannel> logger) : INotificationChannel
{
    public string ChannelType => ChannelTypes.Teams;

    public Task SendAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct)
    {
        logger.LogInformation(
            "[TEAMS CHANNEL - STUB] TenantId={TenantId} DispatchId={DispatchId} EventType={EventType}",
            dispatch.TenantId, dispatch.Id, dispatch.EventType);

        return Task.CompletedTask;
    }
}
