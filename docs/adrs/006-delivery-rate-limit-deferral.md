# ADR 006: Delivery rate-limit deferral is not a failed attempt

**Scope:** `DeliveryProcessorService`, `RateLimitDeferralDurationToStopHotLooping`, `InMemoryMessageQueue.AbandonAsync`

## Context

Rate limiting applies at **two choke points**:

1. **Ingest** — reject with 429 before enqueue (`IngestionService`).
2. **Delivery** — tenant window may still be full when many dispatches fire at once.

When delivery hits the limit, we must not spin in a tight loop re-reading the same dispatch item.

## Decision

On `AcquireResult.RateLimitExceeded` at delivery:

1. **Return before** `dispatch.DeliveryAttempts++` — does **not** count toward max delivery attempts (3).
2. **Abandon** the queue item with `RateLimitConstants.DeliveryDeferralDurationToStopHotLooping` (3 seconds).

In-memory queue: message sits in a delayed list until `UtcNow + delay`, then returns to the channel. Stand-in for ASB `ScheduledEnqueueTimeUtc` or visibility timeout.

## Why not increment delivery attempts?

Rate limit is **expected backpressure**, not channel failure. Burning retry budget on “tenant is busy” would dead-letter dispatches that would succeed moments later.

## Why a delay at all?

Without deferral, abandon immediately re-enqueues and the worker hot-loops on the same rate-limited item.

## Consequences

- **Pros:** Clear semantics; delivery retries reserved for real send failures.
- **Cons:** 1s delay is arbitrary; in-memory timer is not true lock expiry.