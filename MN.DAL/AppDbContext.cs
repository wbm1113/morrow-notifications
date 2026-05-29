using Microsoft.EntityFrameworkCore;
using MN.Entities;
using MN.Interfaces;

namespace MN.DAL;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<NotificationMessage> Messages => Set<NotificationMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Name).IsUnique();
            e.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<RoutingRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.TenantId, r.EventType });
            e.Property(r => r.EventType).IsRequired().HasMaxLength(200);
            e.Property(r => r.ChannelType).IsRequired().HasMaxLength(50);
            e.HasOne(r => r.Tenant)
             .WithMany(t => t.RoutingRules)
             .HasForeignKey(r => r.TenantId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.TenantId, m.Status });
            e.HasIndex(m => m.TenantId);
            e.Property(m => m.EventType).IsRequired().HasMaxLength(200);
        });

        // Global query filters: ORM-layer tenant isolation.
        // Fail-closed by default: if neither CurrentTenantId nor IsAdminScope is set,
        // queries on these tables return nothing. Admin paths that legitimately need
        // cross-tenant reads must explicitly set tenantContext.IsAdminScope = true.
        // Pair with TenantSessionContextInterceptor (sp_set_session_context) for engine-level
        // defense-in-depth against Azure SQL Row-Level Security in production.
        modelBuilder.Entity<RoutingRule>()
            .HasQueryFilter(r => tenantContext.IsAdminScope
                              || (tenantContext.CurrentTenantId.HasValue
                                  && r.TenantId == tenantContext.CurrentTenantId.Value));

        modelBuilder.Entity<NotificationMessage>()
            .HasQueryFilter(m => tenantContext.IsAdminScope
                              || (tenantContext.CurrentTenantId.HasValue
                                  && m.TenantId == tenantContext.CurrentTenantId.Value));
    }

    // Mutation guard: global query filters protect reads only.
    // This override stamps TenantId on any Added/Modified entity that carries one,
    // so a bug in a caller that trusts a body-supplied TenantId can never produce a
    // cross-tenant write. Only applied when a tenant scope is active (IsAdminScope is
    // false and CurrentTenantId is set); admin paths skip stamping by not setting
    // tenant scope.
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (tenantContext.IsAdminScope)
        {
            return base.SaveChangesAsync(cancellationToken);
        }

        if (tenantContext.CurrentTenantId.HasValue)
        {
            foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified))
                    continue;

                entry.Entity.TenantId = tenantContext.CurrentTenantId.Value;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
