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

### Alternatives considered (not chosen as default)

| Approach | Why not default |
|----------|-----------------|
| **Schema-per-tenant** | Every migration, provision, and query becomes tenant-aware; heavy for a router serving many small tenants. |
| **Database-per-tenant** | Stronger isolation, but same provisioning/ops scaling problem at high tenant count. |
| **Deployment-per-tenant (IaC)** | Same app can run on a dedicated stack (own compute, DB, Service Bus, secrets) provisioned from a template. Viable for **enterprise customers** who contract and pay for hard isolation—not the product default. Ops and cost scale with tenant count; shared pool + row-level guards is the right baseline. |

This codebase targets the **shared** model. Dedicated IaC is a back-pocket **tier escalation** (compliance, data residency, noisy-neighbor at infra layer), not a rewrite—same containers, different boundary.

### `IsAdminScope`

Reserved for legitimate cross-tenant admin reads (e.g. future admin API). **Not** used by the outbox publisher—we use per-tenant grouping instead.

## Consequences

- **Pros:** Strong default isolation; hard to accidentally query another tenant’s data; workers mirror request-scoped patterns.
- **Cons:** Outbox is a deliberate filter exception; DEBUG builds throw on unscoped queries to filtered tables (`UnscopedTenantQueryInterceptor`).
- **Production:** Pair ORM filters with Azure SQL RLS for defense in depth.