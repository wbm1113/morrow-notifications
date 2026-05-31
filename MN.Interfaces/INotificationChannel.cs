using MN.Entities;
using MN.Models;

namespace MN.Interfaces;

public interface INotificationChannel
{
    string ChannelType { get; }
    Task SendAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct);
}
