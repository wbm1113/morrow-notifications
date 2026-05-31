# ADR 008: Deterministic dispatch IDs

**Scope:** `DispatchMessage.CreateId`, `PrepareDispatchesForRoutingAsync`, `NotificationDispatch`

## Context

Events can be **redelivered** on the event queue (abandon after transient routing failure, crash before complete). Without stable dispatch identity, each retry could create new dispatch rows and duplicate channel sends for rules that already succeeded.

## Decision

Dispatch Id is **deterministic** from `(OriginalMessageId, RuleId)`:

```csharp
// SHA256(originalMessageId bytes || ruleId bytes) → Guid (first 16 bytes)
DispatchMessage.CreateId(message.Id, rule.Id)
```

The same event + rule always maps to the same `NotificationDispatch` and `DispatchOutboxEntry` Id.

## Behavior on event redelivery

`PrepareDispatchesForRoutingAsync` loads existing dispatches by Id and branches:

| Existing status | Action |
|-----------------|--------|
| `Succeeded` | Skip — channel already delivered |
| `Pending` | Orphan recovery or wait for outbox publisher |
| `Failed` | Manual replay only — reset to `Pending`, republish outbox ([ADR 003](003-dispatch-retry-layers-and-failed-status.md)) |
| (none) | Insert new dispatch + outbox |

## Consequences

- **Pros:** Idempotent fan-out on event retry; no duplicate rows for completed channels; dispatch Id doubles as correlation id end-to-end.
- **Cons:** Id is not a sequential or human-readable identifier; rule change semantics (same rule Id, different config) still reuse the same dispatch row.
- **Related:** Does not solve post-send duplicate delivery ([ADR 002](002-at-least-once-delivery.md))—that is a delivery-layer concern.