namespace MN.Core;

/// <summary>
/// Shared rate-limit knobs. Delivery fan-out is bounded by <see cref="ChannelTypes"/> count;
/// each channel increases pressure on the shared per-tenant sliding window at delivery.
/// Bump <see cref="MaxRateLimitSurfaceArea"/> when adding a channel — see
/// docs/adrs/011-shared-ingest-and-delivery-rate-limiting.md and MN.Tests.RateLimitSurfaceAreaTests.
/// </summary>
public static class RateLimitConstants
{
    /// <summary>
    /// Stand-in for ASB scheduled enqueue on delivery deferral — hold item off-queue to avoid hot-loop.
    /// </summary>
    public static readonly TimeSpan DeliveryDeferralDurationToStopHotLooping = TimeSpan.FromSeconds(3);

    /// <summary>Weight per channel when estimating shared-limiter surface area.</summary>
    public const int PermitsConsumedPerChannel = 1;

    /// <summary>
    /// Maximum allowed <c>ChannelTypes member count × PermitsConsumedPerChannel</c>.
    /// Increase deliberately when adding a new channel type.
    /// </summary>
    public const int MaxRateLimitSurfaceArea = 2;
}
