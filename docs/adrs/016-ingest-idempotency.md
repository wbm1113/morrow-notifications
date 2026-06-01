# ADR 016: Ingest idempotency — known gap at the API boundary

**Scope:** `IngestionService`, `EventsController`, `ProcessingMessage`, `IMessageRepository.EnsureProcessingAsync`

## Context

`IngestionService.IngestAsync` assigns a new `Guid.NewGuid()` to every accepted event before enqueuing it. There is no idempotency key on `POST /api/events`.

The failure mode: a client POSTs an event, the server processes it and returns `202 Accepted`, but the response is lost in transit (network drop, load balancer timeout, client crash). The client has no way to know whether the event was accepted. Standard practice is to retry — which creates a second, identical event in the pipeline with a different `MessageId`. Both will be routed and dispatched independently.

This is a known gap, not an oversight. The spec explicitly says: *"Authentication is out of scope"* and the focus is on isolation, routing, and rate limiting. Idempotency at the API boundary falls outside the required scope. It is documented here because it is the kind of failure-mode question that comes up in a principal-level review.

## What partial protection exists today

`EnsureProcessingAsync` in `IMessageRepository` upserts `NotificationMessage` by `MessageId`. This protects against **queue redelivery** of the same message — if the event queue redelivers a `ProcessingMessage` that was already routed, routing is idempotent (deterministic dispatch IDs mean already-`Succeeded` dispatches are skipped — see [ADR 008](008-deterministic-dispatch-ids.md)).

This does **not** help with the API boundary case: two separate `POST /api/events` calls produce two separate `MessageId` values and are treated as two distinct events end-to-end.

## What a fix would look like

**Client-supplied `Idempotency-Key` header** (standard pattern, used by Stripe, Braintree, etc.):

1. Client generates a UUID per logical event and sends it as `Idempotency-Key: <uuid>`.
2. Server stores `(TenantId, IdempotencyKey) → MessageId` in a short-TTL cache (Redis) or a DB table with a TTL index.
3. On a duplicate key, return the original `202` with the original `MessageId` — no second enqueue.
4. TTL: 24 hours covers any realistic retry window; older keys are not stored.

The `MessageId` in the response already gives clients a correlation handle. The only missing piece is accepting a client-supplied key at the boundary and deduplicating on it before `Guid.NewGuid()` is called.

**Why not use the payload hash as the key?**  
Payload-based deduplication conflates "same logical event" with "same payload content." Two separate `user.signup` events with identical payloads (e.g., a user deleted and re-created with the same email) would be incorrectly collapsed. A client-supplied key is explicit intent from the producer.

## Current behaviour under retry

Without a fix, a client that retries on timeout will produce duplicate notifications on all matched channels. Given that channels are Slack/Teams stubs today, the practical impact is a duplicate log line. Under real channels, this is a visible duplicate message — which [ADR 002](002-at-least-once-delivery.md) accepts as tolerable for the current scope.

## Consequences

- **Gap:** Client retry on timeout creates a duplicate event; no deduplication at the API boundary.
- **Mitigation path:** `Idempotency-Key` header + short-TTL store; does not require changes to the pipeline below `IngestionService`.
- **Not in scope for this submission** per the spec's time budget guidance — deferred to the known limitations list in DESIGN.md.
