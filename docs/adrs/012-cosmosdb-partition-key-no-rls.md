# ADR 012: Azure Cosmos DB evaluated and rejected as primary store

**Scope:** `AppDbContext`, `TenantSessionContextInterceptor`, [ADR 001](001-transactional-outbox-for-dispatch-publish.md), [ADR 009](009-tenant-isolation-fail-closed.md), [ADR 004](004-outbox-without-tenant-query-filter.md)

## Context

Azure Cosmos DB for NoSQL is a natural fit for a multi-tenant SaaS workload: `TenantId` as a partition key gives physical data locality, the service is fully managed, and it scales per-partition automatically. It was evaluated as the backing store for this system.

## Decision

**Do not use Azure Cosmos DB. Use a relational store (Azure SQL) as the production target.**

## Reasons

### 1. The outbox pattern requires multi-entity transactions

ADR 001's core guarantee is that `NotificationDispatch` rows and `DispatchOutboxEntry` rows are written in a **single atomic commit**. This prevents split-brain between the dispatch record and the queue payload.

Cosmos DB supports multi-document transactions only within a single partition of a single container. `NotificationDispatch` and `DispatchOutboxEntry` are different entity types that would live in different containers — a cross-container transaction is not possible. Workarounds (combining them into one container, or accepting eventual consistency between the two writes) both undermine the invariant the outbox is designed to enforce.

### 2. No Row-Level Security equivalent

ADR 009 Layer 4 is a `TenantSessionContextInterceptor` stub that in production would call `sp_set_session_context` on Azure SQL, enabling RLS as a **database-enforced backstop**: even a compromised application layer cannot read another tenant's rows without the session variable set.

Cosmos DB has no equivalent. There is no RLS, no session context API, and no server-side predicate enforcement. The partition key provides structural data locality, but a cross-partition query with `EnableCrossPartitionQuery = true` returns all matching documents — the database does not reject it. Application-layer guards (ADR 009 Layers 1–3) would be the entire isolation story with no backstop, which is a weaker security posture for a multi-tenant system handling potentially sensitive notification payloads.

### 3. The data model is inherently relational

The pipeline relies on:

- Foreign key relationships (`NotificationDispatch → NotificationMessage → RoutingRule`).
- Aggregation queries across dispatches to compute parent message status ([ADR 010](010-parent-message-status-aggregation.md)).
- The outbox drain ordering unpublished rows across tenants by `CreatedAt` ([ADR 004](004-outbox-without-tenant-query-filter.md)).

These are natural SQL queries. Modeling them in Cosmos DB would require denormalisation, fan-out writes, or change feed consumers — all of which add complexity without adding value for a workload that is not document-oriented.

### 4. Cosmos DB pricing is consumption-based in ways that penalise this pattern

The outbox publisher polls unpublished rows on a short interval across all tenants. In Cosmos DB, cross-partition queries consume RUs proportional to the number of physical partitions scanned. At high tenant counts, a polling loop becomes expensive. Azure SQL charges for compute and storage, not per-query RU consumption, which is a better fit for a background worker that runs continuously.

## What would make Cosmos DB appropriate

If the workload shifted toward:

- Document-shaped payloads with no cross-entity transactions.
- Extremely high write throughput per tenant that saturates SQL.
- A requirement for global multi-region writes (Cosmos DB multi-master).

…then Cosmos DB would be worth revisiting, likely paired with a separate relational store for the transactional outbox.

## Consequences

- **Chosen path (Azure SQL):** Supports multi-entity transactions, RLS via session context (ADR 009 Layer 4), natural joins and aggregations, and familiar EF migration tooling.
- **Trade-off accepted:** Manual horizontal sharding if a single tenant grows to a scale that saturates one SQL instance; mitigated by the deployment-per-tenant tier described in ADR 009.
- **This ADR is a record of rejection, not a recommendation to revisit** without addressing the transaction and RLS gaps above.
