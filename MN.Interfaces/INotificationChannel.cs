using MN.Entities;
using MN.Models;

namespace MN.Interfaces;

public interface INotificationChannel
{
    string ChannelType { get; }
    Task SendAsync(ProcessingMessage message, RoutingRule rule, CancellationToken ct);
}
