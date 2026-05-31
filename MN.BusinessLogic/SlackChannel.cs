using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.BusinessLogic;

/// <summary>
/// Stub Slack channel. Replace the body of SendAsync with the Slack Web API call
/// (POST to https://slack.com/api/chat.postMessage) when a real token is available.
/// </summary>
public class SlackChannel(ILogger<SlackChannel> logger) : INotificationChannel
{
    public string ChannelType => ChannelTypes.Slack;

    public Task SendAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct)
    {
        logger.LogInformation(
            "[SLACK CHANNEL - STUB] TenantId={TenantId} DispatchId={DispatchId} EventType={EventType}",
            dispatch.TenantId, dispatch.Id, dispatch.EventType);

        return Task.CompletedTask;
    }
}
