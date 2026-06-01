# ADR 001: Transactional outbox for dispatch publish

**Scope:** `EventRoutingProcessorService`, `PrepareDispatchesForRoutingAsync`, `DispatchOutboxPublisherService`

## Context

Event routing must fan out one event into one or more channel dispatches (Slack, Teams, …). Each dispatch needs:

1. A durable **state row** (`NotificationDispatch`) for lifecycle tracking.
2. A **message on the dispatch queue** so `DeliveryProcessorService` can send it.

Writing the DB row and enqueueing to a broker are two different systems. A crash between them causes **split-brain**.

## Problem: orphan dispatches

Without an outbox, routing might:

1. Insert `NotificationDispatch` rows (`Pending`).
2. Enqueue `DispatchMessage` items.

| Crash point | Result |
|-------------|--------|
| After DB, before queue | **Orphan:** `Pending` dispatches that never get delivered. |
| After queue, before DB | Queue items with no dispatch record to track. |

## Decision

**Routing never enqueues to the dispatch queue directly.**

`PrepareDispatchesForRoutingAsync` writes `NotificationDispatch` + `DispatchOutboxEntry` in **one DB commit**. A separate background worker (`DispatchOutboxPublisherService`) polls unpublished outbox rows and publishes to the dispatch queue.

Dispatch Id is deterministic from `(OriginalMessageId, RuleId)` so event redelivery does not create duplicate rows for channels that already succeeded.

## Why outbox claiming (`ClaimedUntil` / `ClaimedBy`)

The publisher is designed to **scale horizontally** — multiple `DispatchOutboxPublisherService` instances can drain the same outbox table. Without a claim step, two instances could read the same unpublished rows in the same poll window and both enqueue them, amplifying duplicates beyond the crash/republish case we already tolerate.

**Claim before enqueue:**

1. `TryClaimUnpublishedAcrossTenantsAsync` atomically sets `ClaimedUntil` (30s lease) and `ClaimedBy` (instance id) on a batch of rows via `ExecuteUpdateAsync`.
2. Only that instance enqueues and calls `MarkPublishedAsync`, which requires matching `ClaimedBy` — another worker cannot mark rows it did not claim.
3. On success, `PublishedAt` is set and the lease fields are cleared.

**If a publisher crashes mid-batch**, the lease expires and another instance can reclaim the row. Until then, the row is skipped by the claim query (`ClaimedUntil == null || ClaimedUntil < now`), avoiding concurrent double-publish while work is in flight.

This is separate from the transactional outbox *write* at routing time; it addresses **multi-publisher concurrency** at the drain step.

## Publish step: not atomic with the broker

The publisher **claims** rows (see above), **enqueues**, then sets `PublishedAt`.

Enqueue and DB mark still cannot be one transaction with Azure Service Bus (or our in-memory queue).

| Failure | Effect |
|---------|--------|
| Crash after enqueue, before `PublishedAt` | Row still unpublished → republish → **duplicate queue messages**. |
| Crash after `PublishedAt`, before enqueue | Avoided by ordering (we never mark published without enqueueing the whole tenant group first). |

Duplicates on the dispatch queue are tolerated. `DeliveryProcessorService` skips send when dispatch status is already `Succeeded` (see [ADR 002](002-at-least-once-delivery.md)).

## Orphan recovery (routing retry)

If dispatch exists as `Pending` but outbox row is missing (partial commit edge case), routing retry adds the missing outbox entry. See comments in `DispatchRepository.PrepareDispatchesForRoutingAsync`.

## Consequences

- **Pros:** No orphan `Pending` dispatches from routing; clear separation between “decided to dispatch” (DB) and “published for delivery” (queue).
- **Cons:** At-least-once on the dispatch queue; publisher and delivery must tolerate duplicates.
- **Production:** Same pattern with ASB; outbox table stays the source of publish intent.