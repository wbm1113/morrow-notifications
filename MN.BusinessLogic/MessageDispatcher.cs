using Microsoft.Extensions.Logging;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.BusinessLogic;

public class MessageDispatcher(
    IEnumerable<INotificationChannel> channels,
    ILogger<MessageDispatcher> logger) : IMessageDispatcher
{
    private const int MaxChannelAttempts = 2;
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(3);

    private readonly IReadOnlyDictionary<string, INotificationChannel> _channels =
        channels.ToDictionary(c => c.ChannelType, StringComparer.OrdinalIgnoreCase);

    public async Task DispatchAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct)
    {
        if (!_channels.TryGetValue(rule.ChannelType, out var channel))
            throw new InvalidOperationException(
                $"No channel implementation registered for type '{rule.ChannelType}'.");

        for (var attempt = 1; attempt <= MaxChannelAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ChannelTimeout);
                // -> INotificationChannel (Slack / Teams)
                await channel.SendAsync(dispatch, rule, timeoutCts.Token);
                return;
            }
            catch (Exception ex) when (attempt < MaxChannelAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Channel {ChannelType} attempt {Attempt}/{Max} failed for dispatch {DispatchId}. Retrying.",
                    rule.ChannelType, attempt, MaxChannelAttempts, dispatch.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Channel {ChannelType} attempt {Attempt}/{Max} failed for dispatch {DispatchId}. No retries left.",
                    rule.ChannelType, attempt, MaxChannelAttempts, dispatch.Id);
                throw;
            }
        }
    }
}
