# ADR 007: Two-queue architecture (event queue vs dispatch queue)

**Scope:** `IEventQueue`, `IDispatchQueue`, pipeline processors, `DESIGN.md` pipeline section

## Context

An ingested event may fan out to multiple channels (Slack, Teams, …). We could model the whole flow on **one queue**—each message carries “deliver to channel X”—or split work across stages.

## Decision

Use **two queues** with distinct responsibilities:

| Queue | Payload | Producer | Consumer | Work |
|-------|---------|----------|----------|------|
| **Event** | `ProcessingMessage` | `IngestionService` | `EventRoutingProcessorService` | Match rules, write DB, outbox |
| **Dispatch** | `DispatchMessage` | `DispatchOutboxPublisherService` | `DeliveryProcessorService` | Rate limit, send, per-channel retry/DLQ |

Between them: transactional outbox + DB (`NotificationMessage`, `NotificationDispatch`) — see [ADR 001](001-transactional-outbox-for-dispatch-publish.md).

## Why not one queue?

- **Separation of concerns:** routing (DB-heavy, rule matching) vs delivery (HTTP-heavy, channel retries).
- **Independent failure domains:** routing failure dead-letters the event; delivery failure dead-letters one dispatch—other channels for the same event continue.
- **Independent scaling:** scale routing workers vs delivery workers (ASB: separate processors / session handlers).
- **Different retry semantics:** event routing retries (max 3) vs dispatch delivery retries (max 3) vs channel inline retries (max 2)—see [ADR 003](003-dispatch-retry-layers-and-failed-status.md).
- **Outbox fits naturally** between “fan-out decided” and “send to channel.”

## Consequences

- **Pros:** Clear pipeline story; matches how teams operate notification systems at scale.
- **Cons:** More moving parts than a single queue; must keep DB, outbox, and dispatch queue consistent (outbox addresses routing→dispatch handoff).
- **Production:** `IEventQueue` / `IDispatchQueue` swap to ASB topics or queues; `SessionId = TenantId` on both for tenant affinity.