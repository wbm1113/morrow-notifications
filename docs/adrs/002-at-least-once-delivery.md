# ADR 002: At-least-once delivery (prefer duplicate over lost send)

**Scope:** `DeliveryProcessorService`, `MessageDispatcher`, channel stubs

## Context

The pipeline is **at-least-once** end-to-end: events and dispatches can be redelivered from queues after crashes, abandons, or lock expiry.

Two duplicate scenarios matter at delivery:

### A. Outbox republish (handled)

Publisher enqueued but crashed before `PublishedAt`. Same dispatch lands on the queue twice. **Mitigation:** if DB status is already `Succeeded`, complete the queue item without calling the channel.

### B. Post-send, pre-commit (accepted gap)

```
Channel send succeeds (HTTP 200)
  → crash before UpdateStatusAsync(Succeeded)
  → queue redelivers
  → dispatch still Pending
  → send again → duplicate notification
```

Skip-if-`Succeeded` does **not** help here—the first send never reached the DB.

## Decision

**Tolerate a rare duplicate notification rather than optimize for exactly-once outbound delivery.**

We do not implement a full exactly-once pipeline (distributed transactions with Slack/Teams, two-phase outbound outbox, etc.) at this scope.

## Slack / Teams deduping

**Do not assume channels dedupe for us.**

| Channel | Automatic dedup? |
|---------|------------------|
| Slack incoming webhooks | No — each POST is a new message. |
| Teams incoming webhooks | No — each POST is a new message. |
| Slack `chat.postMessage` | **Opt-in** — `client_msg_id` dedupes within ~30 minutes if we pass the same id. |

If duplicates became a product problem in production, we would add an **explicit idempotency key** (e.g. `dispatchId`) on outbound calls—not try to make the entire pipeline exactly-once.

## High-compliance channels (not implemented)

For channels where duplicates are unacceptable (financial alerts, legal/compliance SMS), we would **not** try to make the whole pipeline exactly-once across our DB and a third-party API—that requires cooperation from the provider. The goal is **at-most-once visible delivery** with **at-least-once internal retries**.

Typical approach for those channels only:

1. **Provider idempotency** — pass deterministic `dispatch.Id` ([ADR 008](008-deterministic-dispatch-ids.md)) on every outbound call so a retry after crash returns the original send instead of delivering twice.
2. **Send ledger** — extend dispatch status with a claim-before-send step (`Sending` + lease, similar to outbox claiming). Persist provider acknowledgment (`providerMessageId`) before completing the queue item. Redelivery skips or reconciles when status is already `Accepted`/`Succeeded`.
3. **Channel selection** — use vendors that document idempotency-key behavior; flag routing rules or channel types that require the stricter path.

Slack/Teams stubs stay on the tolerant at-least-once path above; high-stakes adapters get the ledger + idempotency contract.

## Consequences

- **Pros:** Simpler delivery path; favors “notification delivered” over “never duplicate.”
- **Cons:** Narrow crash window can still double-send; concurrent redelivery while `Pending` can also double-send (no distributed lock).
- **Acceptable because:** Duplicate Slack/Teams message is annoying, not financial loss; window is small (post-send to DB commit).