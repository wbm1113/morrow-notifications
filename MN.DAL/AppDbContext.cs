using Microsoft.EntityFrameworkCore;
using MN.Core;
using MN.Entities;
using MN.Interfaces;

namespace MN.DAL;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<NotificationMessage> Messages => Set<NotificationMessage>();
    public DbSet<NotificationDispatch> Dispatches => Set<NotificationDispatch>();
    public DbSet<DispatchOutboxEntry> DispatchOutbox => Set<DispatchOutboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);

            // maybe not necessary.  would help with devops notifications (e.g. 'Acme Corp' is down
            // only means 1 thing).  also if you want to give tenants different URLs and use names
            // as a slug.
            e.HasIndex(t => t.Name).IsUnique();

            e.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<RoutingRule>(e =>
        {
            e.HasKey(r => r.Id);

            // One rule per channel per event type — bounded multi-channel fan-out per ingest.
            // stops tenant from footgunning with duplicate routing rules per channel per event type
            e.HasIndex(r => new { r.TenantId, r.EventType, r.ChannelType }).IsUnique();

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

            // quick status lookups.  "show me all my failed notifications" -> fast.
            // might want to split this status out into two though, as the status is
            // doing 2 jobs.  it shows the status of the notification and the status
            // of the children at the same time, probably not good.
            e.HasIndex(m => new { m.TenantId, m.Status });
            
            e.HasIndex(m => m.TenantId);
            e.Property(m => m.EventType).IsRequired().HasMaxLength(200);
            e.Property(m => m.Payload).IsRequired().HasMaxLength(NotificationLimits.MaxPayloadLength);
        });

        modelBuilder.Entity<NotificationDispatch>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.TenantId, d.OriginalMessageId });
            e.Property(d => d.ChannelType).IsRequired().HasMaxLength(50);
        });

        modelBuilder.Entity<DispatchOutboxEntry>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.PublishedAt);
            e.HasIndex(o => o.ClaimedUntil);
            e.Property(o => o.ClaimedBy).HasMaxLength(64);
            e.HasIndex(o => new { o.TenantId, o.OriginalMessageId });
            e.Property(o => o.ChannelType).IsRequired().HasMaxLength(50);
            e.Property(o => o.EventType).IsRequired().HasMaxLength(200);
            e.Property(o => o.Payload).IsRequired().HasMaxLength(NotificationLimits.MaxPayloadLength);
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

        modelBuilder.Entity<NotificationDispatch>()
            .HasQueryFilter(d => tenantContext.IsAdminScope
                              || (tenantContext.CurrentTenantId.HasValue
                                  && d.TenantId == tenantContext.CurrentTenantId.Value));
    }

    // Mutation guard: global query filters protect reads only.
    // Tenant-scoped writes require CurrentTenantId (stamp TenantId from context) or
    // IsAdminScope (cross-tenant admin). Fail closed on missing context so a new code
    // path cannot silently persist tenant-scoped data without isolation in place.
    public override int SaveChanges()
    {
        ApplyTenantMutationGuard();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTenantMutationGuard();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ApplyTenantMutationGuard()
    {
        if (tenantContext.IsAdminScope)
            return;

        var tenantScopedMutations = ChangeTracker.Entries<ITenantScoped>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        if (tenantScopedMutations.Count == 0)
            return;

        if (!tenantContext.CurrentTenantId.HasValue)
        {
            throw new InvalidOperationException(
                "Cannot save tenant-scoped entities without ITenantContext.CurrentTenantId set. " +
                "Set CurrentTenantId before writing tenant-scoped data, or IsAdminScope for cross-tenant admin operations.");
        }

        foreach (var entry in tenantScopedMutations)
            entry.Entity.TenantId = tenantContext.CurrentTenantId.Value;
    }
}
