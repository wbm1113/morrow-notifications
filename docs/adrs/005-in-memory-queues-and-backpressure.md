# ADR 005: In-memory queues as stand-ins; backpressure in production

**Scope:** `InMemoryMessageQueue`, `BackgroundService` processors, production ASB migration

## Context

`IEventQueue` and `IDispatchQueue` are implemented as **unbounded in-memory channels** with peek-lock semantics (complete / abandon). Background workers poll with ~100ms idle waits when empty.

**Backpressure** is a common concern: what happens when ingest outpaces delivery?

## What backpressure means

When downstream (delivery) is slower than upstream (ingest), work piles up. **Backpressure** is the mechanism that slows or rejects upstream instead of absorbing infinite backlog.

Today: ingest accepts events (subject to per-tenant rate limit), queues grow in memory, processors keep polling—**no signal from dispatch queue depth back to ingest**.

## Decision (this codebase)

**In-memory queues are intentional stand-ins**, not production topology.

- Unbounded growth and RAM use under spike: **accepted at demo scope**.
- Messages lost on process crash: **accepted** (see DESIGN.md).
- 100ms idle polling: simulates worker loops, not production tuning.

## Production (not implemented here)

Replace with **Azure Service Bus** (or similar):

- Durable backlog, horizontal scale-out of consumers.
- `SessionId = TenantId` for tenant affinity.
- Peek-lock / complete / abandon maps directly to current interfaces.

ASB **does not eliminate** backpressure—it **moves** backlog to the broker. You still care about:

- Queue depth and oldest-message age (lag SLAs).
- Cost and size limits at extreme depth.
- Shedding load at ingest (429/503) or APIM when lag exceeds tolerance.

**Existing partial backpressure:** per-tenant rate limit at ingest (429). It is **not** tied to dispatch queue depth or delivery lag.

## Consequences

- **Pros:** Runnable locally in one process; queue contracts (`IMessageQueue<T>`) swap cleanly.
- **Cons:** No durability, no depth-based flow control, not scale-out safe.
- ASB handles **durability and scale-out**, not unbounded lag tolerance—queue depth, oldest-message age, and load shedding at ingest remain design concerns.
