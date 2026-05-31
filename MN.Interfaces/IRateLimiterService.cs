using MN.Core;

namespace MN.Interfaces;

public interface IRateLimiterService
{
    AcquireResult TryAcquire(Guid tenantId);
    void ConfigureTenant(Guid tenantId, int requestsPerMinute);
    void RemoveTenant(Guid tenantId);
}
