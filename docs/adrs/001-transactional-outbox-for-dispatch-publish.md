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

## Publish step: not atomic with the broker

The publisher **claims** rows with a short lease (`ClaimedUntil` / `ClaimedBy`), **enqueues**, then sets `PublishedAt`. Only one publisher instance can claim a given row at a time; expired leases are reclaimable after a crash.

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