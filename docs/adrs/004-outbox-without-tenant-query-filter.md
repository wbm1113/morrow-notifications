# ADR 004: Outbox excluded from EF tenant query filter

**Scope:** `DispatchOutboxEntry`, `AppDbContext`, `DispatchOutboxRepository`, `DispatchOutboxPublisherService`

## Context

Most tenant-scoped entities (`NotificationMessage`, `NotificationDispatch`, `RoutingRule`) use an EF **global query filter** bound to `ITenantContext.CurrentTenantId`.

`DispatchOutboxPublisherService` must drain unpublished outbox rows **across all tenants** in one poll (oldest first, batch of 50).

## Options considered

### A. Add query filter + per-tenant polling

Add the same filter as dispatches; loop active tenants, set `CurrentTenantId`, query each.

- **Pros:** ORM consistency—every tenant table filtered the same way.
- **Cons:** N queries per poll cycle; more complex batching/ordering across tenants.

### B. Add filter + `IgnoreQueryFilters` / admin scope on publisher

- **Pros:** Filter exists for accidental reads elsewhere.
- **Cons:** Publisher still bypasses it; `IgnoreQueryFilters` or admin scope is misleading ceremony.

### C. No query filter on outbox (chosen)

Outbox is **infrastructure**: a durable publish queue keyed by tenant on each row, but read globally by the publisher worker.

- Routing writes outbox rows with `CurrentTenantId` set (mutation guard).
- `MarkPublishedAsync` runs per tenant group with `CurrentTenantId` set.
- `GetUnpublishedAsync` is intentionally cross-tenant.

## Decision

**Do not add an EF query filter on `DispatchOutboxEntry`.**

Rows remain `ITenantScoped`; tenant is stamped on write. The exception is documented here and in code comments.

**Future naming (optional):** rename `GetUnpublishedAsync` → `GetUnpublishedAcrossTenantsAsync` to make the cross-tenant read explicit at the API boundary.

## Consequences

- **Pros:** Simple global drain; no admin scope or `IgnoreQueryFilters` on outbox reads.
- **Cons:** One entity breaks “all tenant tables are filtered” uniformity—must be called out in review.
- **Mitigation:** Explicit `TenantId` on rows; publisher groups by tenant before mark; routing queries outbox with explicit `TenantId` in `DispatchRepository`.