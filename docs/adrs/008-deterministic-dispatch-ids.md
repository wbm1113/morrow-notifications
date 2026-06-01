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

This was a pragmatic choice for the time budget: the schema already uses `Guid` primary keys, queue payloads carry a single `Id`, and redelivery needed a **stable** lookup key — not a formally chosen idempotency standard.

## Alternatives considered

| Approach | Why not (for this scope) |
|----------|---------------------------|
| **`Guid.NewGuid()` per fan-out** | Simple, but event redelivery creates new rows; `Succeeded` skip logic never fires; duplicate channel sends. |
| **Composite PK `(OriginalMessageId, RuleId)`** | Correct in SQL, but everything else already keys off one `Guid` (dispatch queue, outbox, DLQ, logs). Would add a second identifier everywhere or refactor PK shape. |
| **Random PK + separate idempotency column** | Same stable-key idea, extra column and “lookup by natural key, mutate by surrogate” — more moving parts for the same behavior. |
| **`Guid.CreateVersion5` (RFC 4122)** | Same deterministic-UUID pattern, more idiomatic than rolling SHA256. Chosen approach is equivalent in intent; SHA256 was quick and good enough. |
| **Unique constraint + catch duplicate on insert** | Reactive; still need a stable id for queue messages and correlation; doesn’t replace “compute the same id on retry.” |

**What we optimized for:** one `Guid` that fits existing tables, survives queue redelivery, and lets routing upsert/skip by primary key without a dedupe table.

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