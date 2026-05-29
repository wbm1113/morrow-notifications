using Microsoft.Extensions.Logging;
using MN.Entities;
using MN.Interfaces;
using MN.Models;

namespace MN.Dispatching;

public class MessageDispatcher(
    IEnumerable<INotificationChannel> channels,
    ILogger<MessageDispatcher> logger) : IMessageDispatcher
{
    private readonly IReadOnlyDictionary<string, INotificationChannel> _channels =
        channels.ToDictionary(c => c.ChannelType, StringComparer.OrdinalIgnoreCase);

    public async Task DispatchAsync(ProcessingMessage message, IEnumerable<RoutingRule> matchedRules, CancellationToken ct)
    {
        foreach (var rule in matchedRules)
        {
            if (!_channels.TryGetValue(rule.ChannelType, out var channel))
            {
                logger.LogWarning(
                    "No channel implementation registered for type '{ChannelType}'. Rule {RuleId} skipped.",
                    rule.ChannelType, rule.Id);
                continue;
            }

            await channel.SendAsync(message, rule, ct);
        }
    }
}
