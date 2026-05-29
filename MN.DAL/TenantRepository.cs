using Microsoft.EntityFrameworkCore;
using MN.Entities;
using MN.Interfaces;

namespace MN.DAL;

public class TenantRepository(AppDbContext db) : ITenantRepository
{
    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<Tenant?> GetByNameAsync(string name, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Name == name, ct);

    public async Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct) =>
        await db.Tenants.OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken ct)
    {
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    public async Task<Tenant> UpdateAsync(Tenant tenant, CancellationToken ct)
    {
        tenant.UpdatedAt = DateTime.UtcNow;
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenant = await db.Tenants.FindAsync([id], ct);
        if (tenant is not null)
        {
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        await db.Tenants.AnyAsync(t => t.Id == id && t.IsActive, ct);
}
