# ADR 011: Shared ingest and delivery rate limiting

**Scope:** `RateLimiterService`, `IngestionService`, `DeliveryProcessorService`, `RateLimitConstants`, `ChannelTypes`

## Context

Per-tenant rate limiting applies at:

1. **Ingest** — reject with 429 before enqueue.
2. **Delivery** — defer dispatch queue item (does not consume delivery attempts; see [ADR 006](006-delivery-rate-limit-deferral.md)).

Both call `TryAcquire` on the **same** `SlidingWindowRateLimiter` per tenant (`RateLimitPerMinute`).

Fan-out multiplies pressure: one ingested event can produce multiple dispatch items (Slack + Teams), each consuming another permit at delivery.

## Decision (current)

**Single shared limiter per tenant** — demo simplicity, one knob on `Tenant.RateLimitPerMinute`.

Deferral duration when delivery is rate-limited: **3 seconds** (`RateLimitConstants.DeliveryDeferralDurationToStopHotLooping`). Reduces hot-loop frequency vs 1s; does **not** solve sustained overload or queue depth growth ([ADR 005](005-in-memory-queues-and-backpressure.md)).

### Tripwire for future changes

`RateLimitSurfaceAreaTests` reflects over `ChannelTypes` public string constants, multiplies by `PermitsConsumedPerChannel`, and fails if the result exceeds `MaxRateLimitSurfaceArea`. Adding a channel (e.g. Discord) without bumping the max forces a deliberate re-read of this ADR.

## Under sustained load

1. Ingest fills window → 429 on new events.
2. Already-accepted events fan out to dispatch queue.
3. Delivery hits same window → defer 3s, repeat.
4. Dispatch queue depth grows until window slides or load drops.

## Production alternative (not implemented)

Split limiters: **ingest budget** (events/min) vs **outbound budget** (sends/min, sized for fan-out). Or APIM at ingest + worker limit at delivery.

## Consequences

- **Pros:** One limit to configure; tripwire prevents silent proliferation of shared acquire sites.
- **Cons:** Ingest and delivery compete; 3s defer is anti-hot-loop only; fan-out not reflected in ingest cap.
- **When adding a channel:** Bump `MaxRateLimitSurfaceArea`, update this ADR, and consider split limiters.