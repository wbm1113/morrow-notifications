using System.Threading.RateLimiting;

namespace MN.Interfaces;

public interface IRateLimiterService
{
    bool IsKnownTenant(Guid tenantId);
    RateLimitLease TryAcquire(Guid tenantId);
    void ConfigureTenant(Guid tenantId, int requestsPerMinute);
    void RemoveTenant(Guid tenantId);
}
