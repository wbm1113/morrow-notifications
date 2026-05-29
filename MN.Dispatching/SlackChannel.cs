using Microsoft.Extensions.Logging;
using MN.Core;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.Dispatching;

/// <summary>
/// Stub Slack channel. Replace the body of SendAsync with the Slack Web API call
/// (POST to https://slack.com/api/chat.postMessage) when a real token is available.
/// </summary>
public class SlackChannel(ILogger<SlackChannel> logger) : INotificationChannel
{
    public string ChannelType => ChannelTypes.Slack;

    public Task SendAsync(ProcessingMessage message, RoutingRule rule, CancellationToken ct)
    {
        logger.LogInformation(
            "[SLACK CHANNEL - STUB] TenantId={TenantId} MessageId={MessageId} EventType={EventType}",
            message.TenantId, message.Id, message.EventType);

        return Task.CompletedTask;
    }
}
