using MN.Entities;
using MN.Models;

namespace MN.Interfaces;

public interface IMessageDispatcher
{
    Task DispatchAsync(ProcessingMessage message, IEnumerable<RoutingRule> matchedRules, CancellationToken ct);
}
