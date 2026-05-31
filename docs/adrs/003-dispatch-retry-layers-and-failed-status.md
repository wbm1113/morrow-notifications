# ADR 003: Dispatch retry layers and `Failed` status semantics

**Scope:** `DeliveryProcessorService`, `MessageDispatcher`, `PrepareDispatchesForRoutingAsync`

## Context

Retries exist at multiple layers. It is easy to confuse them when reading the code.

## Three separate mechanisms

| Layer | Where | Counter | Max | Terminal state |
|-------|--------|---------|-----|----------------|
| **Event routing** | `EventRoutingProcessorService` / event queue | `ProcessingMessage.DeliveryAttempts` | 3 | Event dead-lettered |
| **Dispatch queue** | `DeliveryProcessorService` / dispatch queue | `DispatchMessage.DeliveryAttempts` | 3 | `DispatchStatus.Failed` + DLQ |
| **Channel inline** | `MessageDispatcher` | (loop, not persisted) | 2 | Throws → queue retry |

**Worst case sends per dispatch:** 3 queue passes × 2 inline channel tries = **6** HTTP attempts before DLQ.

### Channel retries (inline)

Fast, in-process retries for transient blips (timeout, momentary 503). Same queue pickup; 3s timeout per try.

### Queue retries (abandon)

Coarse, durable retry when the whole delivery pass fails. Message goes back on the dispatch queue.

## `Failed` → `Pending` in routing is **manual replay only**

`PrepareDispatchesForRoutingAsync` resets `Failed` dispatches to `Pending` and republishes the outbox when the **same event is routed again** through the event queue.

**Normal delivery failure does not hit this path.** After 3 delivery attempts, dispatch stays `Failed`; nothing re-enqueues the event automatically.

This branch exists for **operational replay** (future “re-route this event” tooling), not automatic retry.

## Consequences

- **Pros:** Clear separation—inline for blips, queue for bigger failures, routing replay for ops.
- **Cons:** Nested retries multiply attempts; `Failed` reset without a replay cap could loop if something kept re-enqueueing the event (not implemented today).