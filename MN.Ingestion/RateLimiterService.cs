using System.Threading.RateLimiting;
using MN.Interfaces;

namespace MN.Ingestion;

public class RateLimiterService : IRateLimiterService, IDisposable
{
    private readonly Dictionary<Guid, (SlidingWindowRateLimiter Limiter, int RequestsPerMinute)> _limiters = new();

    // ReaderWriterLockSlim allows concurrent reads on the hot path (TryAcquire / IsKnownTenant)
    // while serialising writes (ConfigureTenant / RemoveTenant / Dispose).

    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

    public bool IsKnownTenant(Guid tenantId)
    {
        _rwLock.EnterReadLock();
        try { return _limiters.ContainsKey(tenantId); }
        finally { _rwLock.ExitReadLock(); }
    }

    public RateLimitLease TryAcquire(Guid tenantId)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!_limiters.TryGetValue(tenantId, out var entry))
                return new DeniedLease();

            // Read lock is still held here, so RemoveTenant / ConfigureTenant cannot
            // dispose this limiter until we exit — ObjectDisposedException is no longer possible.
            return entry.Limiter.AttemptAcquire();
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
                SegmentsPerWindow = 6,             // 10-second resolution
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
