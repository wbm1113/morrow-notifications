# Design

## Philosophy

In-process on SQLite — runnable in under a minute, not a production topology.

---

## Data Model

| Entity | Purpose |
|---|---|
| `Tenant` | Root aggregate. `RateLimitPerMinute`, `IsActive`. |
| `RoutingRule` | `EventType → ChannelType`. Unique `(TenantId, EventType, ChannelType)`. |
| `NotificationMessage` | Event audit trail: `Processing → FanOutComplete → Dispatched / PartiallyDispatched / DeliveryFailed / DeadLettered`. Upserted by message Id on each attempt. |
| `NotificationDispatch` | Per-channel dispatch record keyed by deterministic `(OriginalMessageId, RuleId)`. Tracks `Pending → Succeeded / Failed`. |
| `DispatchOutboxEntry` | Transactional outbox: dispatch payload written in the same commit as `NotificationDispatch`; `PublishedAt` null until enqueued. |
| `DeadLetterMessage` | In-memory DTO only (`ConcurrentBag`), not persisted. Event-level or dispatch-level entries. |

Queue payloads (`ProcessingMessage`, `DispatchMessage`) live in `MN.Models` and are not persisted directly.

---

## Tenant Isolation

Four layers, fail-closed by default:

1. **Repositories** — every query takes explicit `tenantId`.
2. **EF global query filter** — bound to scoped `ITenantContext`; no context → empty results.
3. **`SaveChanges` mutation guard** — force-stamps `TenantId` on writes from context, not request body.
4. **`TenantSessionContextInterceptor` stub** — no-op on SQLite; would call `sp_set_session_context` for Azure SQL RLS.

Each processor sets `CurrentTenantId` per tenant group via a fresh DI scope. Admin cross-tenant reads set `IsAdminScope = true`.

Schema-per-tenant would force every migration, rule lookup, and message write to be schema-aware and provisioned per onboarded tenant; unnecessary complexity for a shared notification router serving many small tenants.

---

## Rate Limiting

`SlidingWindowRateLimiter` per tenant (60s window, 6 segments). Exceeded → 429 at ingest; on delivery, requeue with a 3s visibility delay (stand-in for ASB scheduled redelivery). In-process singleton — APIM or Redis at scale.

Rate limiting applies at **ingest** (reject before enqueue) and **delivery** (defer dispatch item retry when the window is full — does not consume delivery attempts).

---

## Pipeline

```
POST /api/events
  → IngestionService                rate limit → tenant check → enqueue event
  → Event queue                     IEventQueue (peek-lock)
  → EventRoutingProcessorService    record event, match rules, write dispatch + outbox rows
  → DispatchOutboxPublisherService  publish outbox → dispatch queue
  → Dispatch queue                  IDispatchQueue (peek-lock)
  → DeliveryProcessorService        rate limit → send → retry/DLQ per channel
  → Slack / Teams                   stubs
```

**Ingestion** — rate limit before DB; keeps the invariant on any future ingest path.

**Event routing** — peek-lock batch locally; `EnsureProcessingAsync` upserts by event Id; up to 3 queue-level retries on transient failure, immediate DLQ on permanent failure (no rules, inactive tenant). `PrepareDispatchesForRoutingAsync` writes `NotificationDispatch` + `DispatchOutboxEntry` in one commit (no direct queue access). Dispatch Id is deterministic from `(OriginalMessageId, RuleId)` so event redelivery does not duplicate succeeded channels. Orphan recovery: `Pending` dispatch with no outbox row gets an outbox entry on redelivery.

**Outbox publish** — background worker claims unpublished rows (lease per instance), enqueues to the dispatch queue, marks `PublishedAt`. Crash after enqueue but before mark can duplicate queue items; delivery worker skips dispatches already `Succeeded`.

**Delivery** — one send per dispatch item; 3s timeout, 2 inline retries; any failure → dispatch-level DLQ (other channels for the same event are unaffected). Rate-limited items are abandoned with a 3s visibility delay so the worker does not hot-loop. Parent event status aggregates when all dispatches reach a terminal state: all succeeded → `Dispatched`; mixed → `PartiallyDispatched`; all failed → `DeliveryFailed`.

**Production queues** — `IEventQueue` and `IDispatchQueue` model peek-lock (complete / abandon). Enqueue swaps cleanly; consumers would move to separate `ServiceBusSessionProcessor` instances with `SessionId = TenantId`.

---

## Known limitations / what I'd do with more time

- **Queue / DLQ** — in-memory, lost on crash; visibility delay on abandon is a coarse timer, not lock-expiry simulation.
- **Rate limiter** — in-process, no scale-out; Redis or APIM when multiple instances.
- **Config reads** — fine to hammer the DB at this scale; under load, snapshot rule config into dispatch payloads and/or cache tenant+rules in Redis (outbox/dispatch state stays relational).
- **Processing** — sequential tenant groups (SQLite); `Task.WhenAll` + ASB sessions in production.
- **Channels** — no webhook config store; stubs only. Also needs hard timeouts on HTTP calls.
- **Rules** — exact `EventType` match only.
- **Idempotency** — no outbound dedupe; crash between send and complete can still duplicate on ASB redelivery.
- **Admin** — RLS bypass plus admin query wonkiness demands separate admin app.
