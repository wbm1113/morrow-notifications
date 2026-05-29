using MN.Entities;

namespace MN.Interfaces;

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Tenant?> GetByNameAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct);
    Task<Tenant> CreateAsync(Tenant tenant, CancellationToken ct);
    Task<Tenant> UpdateAsync(Tenant tenant, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
}
