# ADR 013: Observability — structured logging, distributed tracing, and metrics

**Scope:** `IngestionService`, `EventRoutingProcessorService`, `DispatchOutboxPublisherService`, `DeliveryProcessorService`, `InMemoryDeadLetterQueue`, `ProcessingMessage`, `DispatchMessage`

## Context

The pipeline is fully async across four stages separated by durable queues:

```
POST /api/events → [Event Queue] → EventRoutingProcessorService
  → [Dispatch Queue] → DeliveryProcessorService → Slack / Teams
```

In production this is:

- **Event queue / dispatch queue:** Azure Service Bus (ASB) sessions, one session per tenant.
- **Dead-letter queue:** ASB built-in DLQ per queue.
- **Database:** Azure Cosmos DB (see [ADR 012](012-cosmosdb-partition-key-no-rls.md)) or Azure SQL.
- **Compute:** Azure Container Apps or AKS; multiple instances of each processor.

Currently there is no correlation ID propagation across queue hops, no structured log enrichment with pipeline context, no metrics, and no distributed trace that spans all four stages. A message that enters the pipeline is invisible until it reaches a terminal state or is inspected in the database. DLQ entries are silent.

This ADR covers what observability must look like for a production deployment, recognising that the in-process queues in the current codebase are stand-ins.

## Decision

**Adopt OpenTelemetry (OTel) SDK with three signals — traces, metrics, logs — exported to Azure Monitor. Use `MessageId` as the root correlation unit propagated via W3C `traceparent` on ASB message properties.**

---

### Distributed tracing

#### Instrumentation points

| Stage | `ActivitySource` span name | Key attributes |
|-------|---------------------------|----------------|
| Ingest | `mn.ingest` | `tenant.id`, `message.id`, `event.type` |
| Event routing | `mn.route` | `tenant.id`, `message.id`, `rule.count` |
| Outbox publish | `mn.outbox.publish` | `tenant.id`, `dispatch.id` |
| Delivery | `mn.deliver` | `tenant.id`, `dispatch.id`, `channel.type`, `attempt` |

#### Cross-queue context propagation

ASB supports arbitrary `ApplicationProperties` on messages. The root `Activity.Id` (W3C `traceparent`) from ingest is written to the ASB message as `traceparent` (and `tracestate` if set). Each downstream processor reads the property, creates a child `Activity` with the remote parent context, and continues the trace.

`ProcessingMessage` and `DispatchMessage` carry a `TraceContext` string property for this. The in-memory queue ignores it today; the ASB consumer would call `ActivityContext.TryParse` before creating its span.

This produces a single end-to-end distributed trace per ingested event, visible in Azure Monitor Application Insights as a transaction with four child spans across potentially four separate process instances.

#### DLQ traces

Any message dead-lettered at the event or dispatch level emits a span with `error = true`, `dlq.reason`, and the full exception if present. ASB DLQ messages are then traceable back to the originating ingest trace.

---

### Structured logging

All `ILogger` calls use `BeginScope` with a dictionary that includes:

```csharp
{ "TenantId", tenantId }
{ "MessageId", messageId }
{ "DispatchId", dispatchId }   // delivery stage only
{ "Stage", "ingest|route|outbox|deliver" }
```

This ensures every log line emitted during processing of a specific message carries the same correlation fields, queryable in Log Analytics with:

```kusto
AppTraces
| where Properties.MessageId == "<id>"
| order by TimeGenerated asc
```

Log levels follow standard conventions: `Information` for normal pipeline progress, `Warning` for retried operations and rate-limit hits, `Error` for DLQ entries and terminal failures.

---

### Metrics

Using `System.Diagnostics.Metrics` (`Meter` / `Counter` / `Histogram`), exported via OTel to Azure Monitor custom metrics.

| Metric | Type | Dimensions |
|--------|------|------------|
| `mn.events.ingested` | Counter | `tenant_id`, `event_type` |
| `mn.events.routed` | Counter | `tenant_id` |
| `mn.dispatches.attempted` | Counter | `tenant_id`, `channel_type` |
| `mn.dispatches.succeeded` | Counter | `tenant_id`, `channel_type` |
| `mn.dispatches.failed` | Counter | `tenant_id`, `channel_type` |
| `mn.ratelimit.hits` | Counter | `tenant_id`, `stage` (`ingest`\|`delivery`) |
| `mn.dlq.entries` | Counter | `tenant_id`, `entry_type` (`event`\|`dispatch`) |
| `mn.dispatch.latency` | Histogram (ms) | `tenant_id`, `channel_type` |
| `mn.ingest_to_delivery.latency` | Histogram (ms) | `tenant_id` |

`mn.ingest_to_delivery.latency` is the wall-clock delta from `NotificationMessage.CreatedAt` to `NotificationDispatch` reaching a terminal state — end-to-end pipeline latency per tenant.

---

### Health checks (`IHealthCheck`)

Registered on the `/healthz` endpoint, used by Container Apps / AKS liveness and readiness probes:

| Check | Readiness | Liveness |
|-------|-----------|---------|
| Database connectivity (Cosmos DB / Azure SQL) | Yes | Yes |
| ASB namespace reachability | Yes | No |
| Event queue depth < configurable threshold | Yes | No |
| Dispatch queue depth < configurable threshold | Yes | No |
| DLQ depth = 0 (degraded, not unhealthy) | Yes | No |

Queue depth checks use the ASB Management Client (`ServiceBusAdministrationClient`). A non-zero DLQ depth marks readiness as `Degraded` — the service continues running but triggers an Azure Monitor alert.

---

### Alerting baselines (Azure Monitor)

| Signal | Condition | Severity |
|--------|-----------|---------|
| `mn.dlq.entries` | Any increment | 2 (Error) |
| `mn.ratelimit.hits{stage=ingest}` | > N/min per tenant | 3 (Warning) |
| `mn.dispatch.latency` P99 | > 30s | 2 (Error) |
| DLQ depth (ASB built-in) | > 0 for 5 min | 1 (Critical) |
| Health check degraded | Consecutive failures | 2 (Error) |

---

### Wire-up in `Program.cs`

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("MorrowNotifications")
        .AddAzureMonitorTraceExporter())
    .WithMetrics(m => m
        .AddMeter("MorrowNotifications")
        .AddAzureMonitorMetricExporter())
    .WithLogging(l => l
        .AddAzureMonitorLogExporter());
```

No changes to business logic are required beyond adding `BeginScope` enrichment and `ActivitySource.StartActivity` calls at stage entry points.

---

## Consequences

- **Pros:** Single `MessageId` threads all log lines, spans, and metrics for one event across four async stages and multiple process instances; rate-limit and DLQ pressure surfaces before it becomes an incident; OTel SDK is vendor-neutral — exporter swap (e.g. Datadog) requires no code changes.
- **Cons:** W3C context propagation requires `traceparent` in ASB `ApplicationProperties` — the in-memory queue stand-in does not propagate this today; `ProcessingMessage` and `DispatchMessage` need a `TraceContext` property added before the ASB producer/consumer is wired up.
- **Current gap:** `InMemoryDeadLetterQueue` (`ConcurrentBag`) has no persistence, no replay, and no alerting. In production this is replaced by the ASB built-in DLQ; until then, DLQ entries are silent and the `mn.dlq.entries` counter is the only signal.
