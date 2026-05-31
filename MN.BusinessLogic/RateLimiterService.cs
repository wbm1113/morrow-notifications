using System.Threading.RateLimiting;
using MN.Core;
using MN.Interfaces;

namespace MN.BusinessLogic;

public class RateLimiterService : IRateLimiterService, IDisposable
{
    private readonly Dictionary<Guid, (SlidingWindowRateLimiter Limiter, int RequestsPerMinute)> _limiters = new();
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

    public AcquireResult TryAcquire(Guid tenantId)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!_limiters.TryGetValue(tenantId, out var entry))
                return AcquireResult.TenantNotFound;

            using var lease = entry.Limiter.AttemptAcquire();
            return lease.IsAcquired ? AcquireResult.Acquired : AcquireResult.RateLimitExceeded;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void ConfigureTenant(Guid tenantId, int requestsPerMinute)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_limiters.TryGetValue(tenantId, out var existing))
            {
                if (existing.RequestsPerMinute == requestsPerMinute) return;
                existing.Limiter.Dispose();
                _limiters.Remove(tenantId);
            }

            var limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = requestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });

            _limiters[tenantId] = (limiter, requestsPerMinute);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void RemoveTenant(Guid tenantId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_limiters.Remove(tenantId, out var entry))
                entry.Limiter.Dispose();
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _rwLock.EnterWriteLock();
        try
        {
            foreach (var entry in _limiters.Values)
                entry.Limiter.Dispose();
            _limiters.Clear();
        }
        finally
        {
            _rwLock.ExitWriteLock();
            _rwLock.Dispose();
        }
    }
}
