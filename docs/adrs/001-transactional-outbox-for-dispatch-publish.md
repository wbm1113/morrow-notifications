# ADR 001: Transactional outbox for dispatch publish

**Scope:** `EventRoutingProcessorService`, `PrepareDispatchesForRoutingAsync`, `DispatchOutboxPublisherService`

## The problem

When routing an event, two things need to happen:

1. Write a `NotificationDispatch` row to the DB — the durable record that tracks lifecycle state.
2. Put a `DispatchMessage` on the dispatch queue — so `DeliveryProcessorService` knows to send it.

These are two different systems. They cannot share a transaction. A crash between them leaves the system in an inconsistent state:

| Crash point | Result |
|-------------|--------|
| After DB write, before enqueue | `Pending` dispatch row that will never be delivered. Nothing retries it — silent failure. |
| After enqueue, before DB write | Queue message pointing at a dispatch record that doesn't exist yet. |

The naive "write DB then enqueue" approach produces **orphan dispatches** — `Pending` rows that sit forever with no delivery worker aware of them.

## Decision

**Collapse the problem back to one system — the database.**

Routing writes `NotificationDispatch` + `DispatchOutboxEntry` in a **single DB transaction**. That commit either happens or it doesn't. The queue is not touched during routing at all.

A separate background worker (`DispatchOutboxPublisherService`) polls for unpublished outbox rows and enqueues them to the dispatch queue. If the publisher crashes mid-batch, the rows are simply re-drained on the next pass — no orphans, because intent is durably recorded in the DB before anything is enqueued.

Dispatch Id is deterministic from `(OriginalMessageId, RuleId)` so if the event is redelivered and routing runs again, dispatches that already `Succeeded` are skipped. See [ADR 008](008-deterministic-dispatch-ids.md).

## The trade-off accepted

The outbox does not eliminate duplicates — it **moves** them. The publisher claims rows, enqueues, then marks `PublishedAt`. If it crashes after enqueue but before the mark, the row looks unpublished and gets re-enqueued on the next pass — a duplicate on the dispatch queue.

This is tolerated deliberately. We already had to accept at-least-once delivery from the queue layer (ASB redelivers on lock expiry). Handling a duplicate dispatch message is the same problem: the delivery worker checks if the dispatch is already `Succeeded` before calling the channel, and skips if so. See [ADR 002](002-at-least-once-delivery.md).

The ordering guarantee that prevents the reverse case: `MarkPublishedAsync` is called only after the entire tenant group is enqueued — so we never mark a row published without having enqueued it first.

## Why the claiming mechanism (`ClaimedUntil` / `ClaimedBy`)

Once the publisher can scale horizontally, two instances polling at the same time would both see the same unpublished rows and both enqueue them — amplifying duplicates before any crash happens.

The claim is a distributed lease implemented in the DB, the same concept as a queue peek-lock:

1. `TryClaimUnpublishedAcrossTenantsAsync` atomically sets `ClaimedUntil` (30s) and `ClaimedBy` (instance id) on a batch of rows via a single `ExecuteUpdateAsync`.
2. Only the claiming instance can call `MarkPublishedAsync` — it requires a matching `ClaimedBy`.
3. On success, `PublishedAt` is set and the lease fields are cleared.
4. If the publisher crashes mid-batch, the lease expires and another instance reclaims the rows.

This is separate from the transactional outbox *write* at routing time — that addresses atomicity. The claim addresses **concurrent publisher instances** at the drain step.

## Orphan recovery

If a `Pending` dispatch exists but its outbox row is missing (a partial commit edge case), routing retry detects the gap and inserts the missing outbox entry. This is a defensive recovery path, not the normal flow.

## Consequences

- **Pros:** No orphan `Pending` dispatches from routing; intent is durable before delivery is attempted; publisher scales horizontally without coordination beyond the claim lease.
- **Cons:** At-least-once on the dispatch queue; delivery must tolerate duplicates (it does).
- **Production:** Same pattern with ASB; the outbox table remains the source of truth for publish intent regardless of broker.