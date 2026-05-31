using MN.Entities;

namespace MN.Interfaces;

public interface IDispatchOutboxRepository
{
    /// <summary>
    /// Atomically claims up to <paramref name="max"/> unpublished rows for this publisher instance.
    /// Rows with an active lease held by another instance are skipped.
    /// </summary>
    Task<IReadOnlyList<DispatchOutboxEntry>> TryClaimUnpublishedAcrossTenantsAsync(
        int max, string claimerId, CancellationToken ct);

    Task MarkPublishedAsync(IReadOnlyList<Guid> ids, string claimerId, CancellationToken ct);
}
