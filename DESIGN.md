
# Design

## Philosophy

Everything runs in-process on SQLite. No Docker, no external services, no infrastructure to fight. For an interview project the right call is something reviewable and runnable in under a minute — not a realistic production topology.

---

## Data Model

Three entities are persisted to the database via EF Core. `DeadLetterMessage` is an in-memory DTO in `MN.Models` — never mapped to a table.

| Entity | Purpose |
|---|---|
| `Tenant` | Root aggregate. Owns `RateLimitPerMinute` and `IsActive`. |
| `RoutingRule` | Scoped to a tenant. Maps `EventType → ChannelType`. One row = one dispatch target. |
| `NotificationMessage` | Written on first dequeue. Tracks status through `Processing → Dispatched / DeadLettered`. Retries update the existing row rather than creating a new one. |
| `DeadLetterMessage` | In-memory DTO in `MN.Models`. Stored in a `ConcurrentBag` inside `InMemoryDeadLetterQueue`. Not an EF entity — survives only for the process lifetime. |

---

## Tenant Isolation

**1. Repository layer** — every query method takes a `tenantId` parameter and filters explicitly. A caller cannot accidentally omit it.

**2. EF Core `HasQueryFilter`** — `RoutingRule` and `NotificationMessage` have a global query filter bound to a scoped `ITenantContext`. The filter is **fail-closed**: if neither `CurrentTenantId` is set nor `IsAdminScope` is explicitly `true`, queries on those tables return nothing. Set `CurrentTenantId` at the top of a controller action or processor scope and every EF query that runs in that scope is automatically filtered. Admin paths that legitimately need cross-tenant reads set `IsAdminScope = true` explicitly — there is no `null`-means-passthrough sentinel.

**3. `SaveChangesAsync` mutation guard** — global query filters protect reads only; they do not intercept `INSERT` or `UPDATE`. `AppDbContext` overrides `SaveChangesAsync` to scan the EF change tracker before every commit. Any `Added` or `Modified` entity that carries a `TenantId` property has that property force-stamped with the value from `ITenantContext`. A bug in a caller that trusts a body-supplied `TenantId` therefore cannot produce a cross-tenant write. The guard fires when `CurrentTenantId` is set and `IsAdminScope` is false — admin paths set `IsAdminScope = true` and skip stamping by design.

**4. `TenantSessionContextInterceptor` stub** — no-op locally (SQLite has no session context). In production against Azure SQL with Row-Level Security this would call `sp_set_session_context` before each command. Uses `DbCommandInterceptor` (command-level) rather than `DbConnectionInterceptor` (connection-level) so that pool reuse doesn't bleed one tenant's session context into the next request.

**Background worker thread scope** — the processor creates a fresh DI scope per *tenant group* via `IServiceScopeFactory`, setting `tenantContext.CurrentTenantId` once for the group. Tenant identity travels in the message envelope, not in ambient web thread state.

**Trade-offs:** four layers means more things to keep in sync; the RLS piece in particular puts logic inside the database. Schema/database-per-tenant gives stronger isolation but is operationally much heavier — shared database is the right default for shared-infrastructure SaaS.  Admin will need an elevated user to bypass RLS for multi-tenant operations, which would be sticky to do all in one app, and kind of forces internal admin control into a separate web app that has to be maintained.

---

## Rate Limiting

**Algorithm:** `SlidingWindowRateLimiter` — 60s window, 6 segments (10s resolution). Sliding window eliminates the burst spike at a fixed-window boundary. One limiter per `TenantId` in a `ConcurrentDictionary`; tenants are fully isolated from each other.

**On limit exceeded:** immediate 429, `QueueLimit = 0`. Unknown tenants are rejected with 404 via `IsKnownTenant` (a dictionary lookup, no DB hit) before `TryAcquire` is called, so a rate-limit response always means "known tenant, window exhausted" — the two cases are distinguishable by callers.

**Where state lives:** in-process singleton. Doesn't survive restarts or scale-out — Redis or Azure API Management would replace this in production.

---

## Ingestion, Queue, Processing & Dispatch

The full path from HTTP request to channel delivery:

```
HTTP POST /api/events
  → EventsController      — rate-limit check (fast path, no DB hit)
  → IngestionService      — tenant validation, Channel<T> write
  → Channel<T>            — in-process unbounded queue
  → NotificationProcessorService  — BackgroundService consumer loop
  → MessageDispatcher     — fan-out across matched routing rules
  → INotificationChannel  — Slack / Teams (stubs)
```

**EventsController / IngestionService** — Rate limiter is checked first (dictionary lookup only, no DB hit). On a granted lease, `IngestionService` validates the tenant and enqueues the message. In production, this layer should give way to Azure API Management: APIM handles auth, per-tenant rate limiting, request routing, and observability at the gateway level — keeping the application service focused purely on processing.

**In-process queue** — A `Channel<T>` stand-in for a real queue broker. In production this is replaced with Azure Service Bus; the `IMessageQueue` interface is shaped to match ASB's peek-lock API, so the swap is a single implementation class.

**Peek-lock processing** — `PeekLockBatchAsync` moves messages to an in-flight dictionary before processing them. The processor calls `CompleteAsync` on success or `AbandonAsync` on transient failure — abandon re-enqueues for retry. This matches the ASB messaging pattern the production implementation would use.

**Commit-on-pull with retry** — The first time a message is processed its DB record is created (`CreateAsync`). On retries after abandon, the record already exists and is updated back to `Processing`. Up to `MaxDeliveryAttempts = 3` total attempts are made on transient dispatch failures before the message is dead-lettered. Permanent failures (no matching rules, tenant inactive) dead-letter immediately without retry.

**Scope-per-tenant-group** — Each tenant group gets its own DI scope (one `DbContext`, one `ITenantContext`). Groups run sequentially because SQLite serializes writers; in production the `foreach` becomes `Task.WhenAll` for tenant-parallel fan-out.

**Tenant re-validation at processing time** — The processor re-checks tenant active status when it dequeues the message. A tenant that was valid at ingest time may have been deactivated in the window between enqueue and processing; that message is dead-lettered rather than dispatched.

**Fan-out dispatch** — The dispatcher loads all `RoutingRule` rows matching `(tenantId, eventType)` and calls the corresponding `INotificationChannel` for each rule sequentially. An event type mapped to both Slack and Teams will be delivered to both in one pass. Rules that reference an unregistered channel type are skipped with a warning rather than aborting the dispatch.

**Dead-letter paths** — Four conditions send a message to dead letter: tenant not found or inactive, no matching routing rules, max delivery attempts exceeded (`MaxDeliveryAttempts = 3`), or unhandled exception on the final attempt. All paths write to the in-memory `ConcurrentBag` *and* update `NotificationMessage.Status` to `DeadLettered` in the database. Transient dispatch exceptions before the limit is reached trigger `AbandonAsync` for retry instead.

---

## Dispatcher Abstraction

```csharp
public interface INotificationChannel
{
    string ChannelType { get; }
    Task SendAsync(ProcessingMessage message, RoutingRule rule, CancellationToken ct);
}
```

`MessageDispatcher` resolves all `INotificationChannel` implementations at startup into a `IReadOnlyDictionary<string, INotificationChannel>`. Adding a channel (email, webhook, SMS) is a single new class registration — zero changes to the dispatcher. Current Slack/Teams implementations are logging stubs; real HTTP calls are a contained change inside each class.

---

## Known Limitations & What I'd Do Differently

**Queue:** `Channel<T>` is an in-process stand-in for Azure Service Bus. In-flight messages are lost on crash (the `ConcurrentDictionary` is not durable), and the lock timeout expiry that real ASB provides is not simulated — a stranded in-flight message never re-surfaces. `IMessageQueue` maps directly onto ASB's `ReceiveMessagesAsync` / `CompleteMessageAsync` / `AbandonMessageAsync`, so the migration is a single implementation swap.

**Dead letters:** in-memory `ConcurrentBag`, lost on restart. Production needs a durable store with replay/inspection tooling.

**Rate limiter:** in-process singleton, doesn't survive restarts or scale-out.  Implementation requires restart for new/updated tenants. Redis sliding window or Azure API Management at scale.

**Tenant validation — no caching:** every ingest request hits the DB. A short-lived cache with TTL-based invalidation on tenant update is the standard fix.

**Batch processing — sequential within SQLite:** groups are processed sequentially because SQLite serializes writers. In production, `foreach` → `Task.WhenAll` for tenant-parallel fan-out and noisy-neighbor isolation.

**Channel config:** no per-tenant store for webhook URLs, connector tokens, etc. Stubs log; real dispatch needs a config store resolved at dispatch time.

**Channel type strings:** rules hard-code `"slack"` / `"teams"`. A category-to-channel mapping layer would decouple rule definitions from integrations — deliberately deferred as premature.

**Rules engine:** `EventType → ChannelType`, exact string match. A practical next step: first-class predicates on event key patterns or payload fields.

**Idempotency:** no idempotency keys. Retries after a network timeout produce duplicates. Client-supplied key in Redis with short TTL is the fix.

**Payload:** opaque string, passed through unchanged. Template management is out of scope.

**Service layer:** controllers call repositories directly. Fine now; grows unwieldy as tenant lifecycle logic (cascading deactivation, audit logging, rate limiter sync) accumulates.

**Admin panel** The admin functions would need RLS override; I'd consider making a completely separate full stack web app just for admin purposes.

**Rule-based rate limiting** Nothing stops a tenant from having a boatload of rules that hogs resources - rate limiting (or just limiting the total number of rules at the admin level) would address this.