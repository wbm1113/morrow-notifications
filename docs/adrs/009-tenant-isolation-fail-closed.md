# ADR 009: Tenant isolation (fail-closed, shared schema)

**Scope:** `ITenantContext`, `AppDbContext`, repositories, background processors, [ADR 004](004-outbox-without-tenant-query-filter.md)

## Context

Multi-tenant notification router: one deployment serves many tenants. Data leaks between tenants are unacceptable. Options include schema-per-tenant, row-level security only, or application-layer isolation.

## Decision

**Shared schema, row-level tenant id, fail-closed application guards.**

### Four layers

1. **Repositories** — queries take explicit `tenantId` (defense in depth).
2. **EF global query filters** — on `RoutingRule`, `NotificationMessage`, `NotificationDispatch`; bound to scoped `ITenantContext`. No context → **empty results** (not all rows).
3. **`SaveChanges` mutation guard** — tenant-scoped writes require `CurrentTenantId` (stamps `TenantId` from context) or explicit `IsAdminScope` for cross-tenant admin operations.
4. **`TenantSessionContextInterceptor` stub** — no-op on SQLite; production would set SQL session context for Azure SQL RLS.

### Background workers

Each processor groups work by tenant, opens a fresh DI scope, sets `CurrentTenantId = group.Key`, then reads/writes. Same pattern in event routing, outbox publish (mark published), and delivery.

**Exception:** `DispatchOutboxEntry` has **no** query filter—the publisher drains unpublished rows across tenants ([ADR 004](004-outbox-without-tenant-query-filter.md)). Rows are still tenant-stamped; writes use `CurrentTenantId` per group.

### Not chosen: schema-per-tenant

Would complicate migrations, provisioning, and every query for a router serving many small tenants—unnecessary at this scale.

### `IsAdminScope`

Reserved for legitimate cross-tenant admin reads (e.g. future admin API). **Not** used by the outbox publisher—we use per-tenant grouping instead.

## Consequences

- **Pros:** Strong default isolation; hard to accidentally query another tenant’s data; workers mirror request-scoped patterns.
- **Cons:** Outbox is a deliberate filter exception; DEBUG builds throw on unscoped queries to filtered tables (`UnscopedTenantQueryInterceptor`).
- **Production:** Pair ORM filters with Azure SQL RLS for defense in depth.