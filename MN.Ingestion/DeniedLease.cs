using System.Threading.RateLimiting;

namespace MN.Ingestion;

/// <summary>
/// Returned when a tenant has no rate limiter configured (unknown tenant reached this point).
/// Always denied. Distinct from an exhausted limiter.
/// </summary>
internal sealed class DeniedLease : RateLimitLease
{
    public override bool IsAcquired => false;
    public override IEnumerable<string> MetadataNames => [];
    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        metadata = null;
        return false;
    }
    protected override void Dispose(bool disposing) { }
}
