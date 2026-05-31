namespace MN.Core;

public static class OutboxClaimConstants
{
    /// <summary>
    /// How long a publisher instance exclusively owns an outbox row after a successful claim.
    /// Expired leases can be reclaimed by another instance (crash recovery).
    /// </summary>
    public static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromSeconds(30);
}
