using MN.Entities;
using MN.Models;

namespace MN.Interfaces;

public interface IMessageDispatcher
{
    Task DispatchAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct);
}
