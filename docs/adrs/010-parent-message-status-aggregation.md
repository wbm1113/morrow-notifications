# ADR 010: Parent message status aggregation

**Scope:** `NotificationMessage.Status`, `DeliveryProcessorService.UpdateParentMessageStatusAsync`

## Context

One ingested event fans out to multiple dispatches (e.g. Slack + Teams). Each dispatch succeeds or fails independently. The parent `NotificationMessage` needs a single status for API/audit once we know the outcome.

## Decision

After each dispatch reaches a **terminal** state (`Succeeded` or `Failed`), `UpdateParentMessageStatusAsync` runs:

1. Load all dispatches for `(TenantId, OriginalMessageId)`.
2. If **any** dispatch is still `Pending` → **do not update** parent (still in flight).
3. Otherwise aggregate:

| Condition | Parent status |
|-----------|---------------|
| All dispatches `Succeeded` | `Dispatched` |
| Mix of `Succeeded` and `Failed` | `PartiallyDispatched` |
| All dispatches `Failed` | `DeliveryFailed` |

When any failed: set `failureReason` to e.g. `"1 of 2 channel dispatch(es) failed."`

### Earlier lifecycle (not aggregated here)

- `Processing` — routing started (`EnsureProcessingAsync`)
- `FanOutComplete` — routing finished, dispatches scheduled via outbox
- `DeadLettered` — event-level DLQ during routing (no rules, inactive tenant, max routing retries)

## Rationale for `DeliveryFailed` vs `PartiallyDispatched`

**All failed** means the notification never reached any channel—different from “some channels worked.” Callers and ops can treat `DeliveryFailed` as a total miss.

## Consequences

- **Pros:** Clear parent semantics; partial success is visible; aggregation waits until no `Pending` dispatches remain.
- **Cons:** Parent status lags slowest channel; concurrent dispatch completions may race (last writer wins on parent update—acceptable at this scope).
- **Per-channel DLQ:** One channel failing does not dead-letter the event or other channels.