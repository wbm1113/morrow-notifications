# ADR 017: Event replay

**Scope:** `EventRoutingProcessorService`, `PrepareDispatchesForRoutingAsync`, `DispatchOutboxPublisherService`, `DeliveryProcessorService`

## The problem

When a dispatch fails permanently (exhausts retries, rule goes inactive, channel error), the
`NotificationDispatch` row is marked `Failed` and the message is dead-lettered. There needs to be
a way to replay the event without duplicating work for channels that already succeeded.

## Decision

There are two distinct replay paths depending on whether the original `MessageId` is preserved.

### Path 1: Re-POST to `/api/events` (new `MessageId`)

`IngestionService` calls `Guid.NewGuid()` on every accepted request. A re-POST produces a new
`MessageId`, which produces new dispatch IDs (`SHA256(newMessageId + ruleId)`). The notification
processor finds no existing dispatches for the new ID and creates entirely fresh
`NotificationDispatch` and `DispatchOutboxEntry` rows. The original `Failed` rows are never
touched — they remain `Failed` permanently and the new rows flow through the pipeline
independently.

This is the only replay option available via the public API today. It works, but it leaves
orphaned `Failed` rows from the original run and has no deduplication against the original
`MessageId`.

### Path 2: Operational re-enqueue of the original `ProcessingMessage` (same `MessageId`)

If the original `ProcessingMessage` (with its original `Id`) is placed back onto the event queue
directly (an operational action, not an API call), `PrepareDispatchesForRoutingAsync` sees the
same `MessageId` and branches per existing dispatch status:

| Existing dispatch status | Behaviour |
|---|---|
| `Succeeded` | Skipped — channel already delivered, do not re-send. |
| `Pending` (outbox published) | Skipped — delivery is already in flight. |
| `Pending` (outbox not published) | Orphan recovery — re-creates the outbox entry only. |
| `Failed` | Reset to `Pending`, `DispatchOutboxEntry.PublishedAt` cleared — flows through the outbox publisher and delivery processor as normal. |
| No row | Fresh dispatch — inserted as normal. |

This replays only failed channels and is a no-op for channels that already succeeded. There is
no API endpoint for this today — it requires direct operational access to re-enqueue the message.

## Force-replaying a succeeded channel

There is intentionally no API for this. Resetting a `Succeeded` dispatch would require:

1. Setting `NotificationDispatch.Status` back to `Pending`.
2. Clearing `DispatchOutboxEntry.PublishedAt` (and `ClaimedBy` / `ClaimedUntil`).

Step 2 is necessary because the outbox publisher only picks up rows where `PublishedAt IS NULL`.
Resetting the dispatch status alone leaves the outbox entry published — the publisher never
re-enqueues it and nothing happens.

Force-replay is a deliberate operational action (re-sending an already-delivered notification is
a meaningful side effect) and should remain a manual DB operation rather than an exposed endpoint.

## Replay vs. accidental double-post

Re-ingesting the same event intentionally (replay) and a client accidentally POSTing twice are
structurally identical from the pipeline's perspective — both produce a new `MessageId` via
`Guid.NewGuid()` in `IngestionService`, and both result in independent routing and dispatch.
The idempotency protection described above (skip `Succeeded` dispatches, deterministic dispatch
IDs) only applies within a single `MessageId` lineage. It does not deduplicate across two
separate ingest calls.

The fix — a client-supplied `Idempotency-Key` header deduplicated before `NewGuid()` is called —
is documented in [ADR 016](016-ingest-idempotency.md).

## At-least-once gap on replay

If the process crashes after the channel send but before `UpdateStatusAsync(Succeeded)`, the
dispatch row stays `Pending`. A subsequent replay or redelivery will re-attempt the send. This
is an accepted at-least-once trade-off — see ADR 002.
