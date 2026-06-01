# ADR 014: Dispatcher abstraction — `INotificationChannel` and `MessageDispatcher`

**Scope:** `INotificationChannel`, `IMessageDispatcher`, `MessageDispatcher`, `SlackChannel`, `TeamsChannel`, `DeliveryProcessorService`, `ChannelTypes`

## Context

The spec requires that adding a new notification channel (email, SMS, webhook, etc.) does not require changes to the routing engine. The failure mode called out explicitly is "extensible in name only" — an interface that exists but forces every new channel to touch routing logic, add a switch statement, or modify `DeliveryProcessorService`.

## Decision

**Two-interface design: `INotificationChannel` is the channel seam; `IMessageDispatcher` encapsulates retry and dispatch orchestration.**

### `INotificationChannel`

```csharp
public interface INotificationChannel
{
    string ChannelType { get; }
    Task SendAsync(DispatchMessage dispatch, RoutingRule rule, CancellationToken ct);
}
```

Each channel implementation declares its own string key via `ChannelType`. `SlackChannel` returns `"slack"`, `TeamsChannel` returns `"teams"`. The key matches `RoutingRule.ChannelType` — that string is the only coupling between the rule model and the channel implementations.

### `MessageDispatcher` — route by key, not by type

`MessageDispatcher` receives `IEnumerable<INotificationChannel>` via DI and builds a dictionary at construction time:

```csharp
_channels = channels.ToDictionary(c => c.ChannelType, StringComparer.OrdinalIgnoreCase);
```

Dispatch is a lookup, not a switch:

```csharp
if (!_channels.TryGetValue(rule.ChannelType, out var channel))
    throw new InvalidOperationException(...);

await channel.SendAsync(dispatch, rule, ct);
```

`DeliveryProcessorService` calls `IMessageDispatcher.DispatchAsync` and has no knowledge of individual channels. `MessageDispatcher` owns the inline retry loop (2 attempts, 3s timeout per attempt) — channel implementations do not need to handle retries themselves.

### What adding a new channel actually requires

1. Create a class that implements `INotificationChannel` in `MN.BusinessLogic` (or a separate assembly).
2. Return the channel's string key from `ChannelType`.
3. Register it with DI in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<INotificationChannel, EmailChannel>();
   ```
4. Add the key as a constant in `ChannelTypes` (optional but required to trigger the rate limit surface area tripwire — see [ADR 011](011-shared-ingest-and-delivery-rate-limiting.md)).

No changes to `DeliveryProcessorService`, `MessageDispatcher`, `EventRoutingProcessorService`, or any routing logic.

### Why `ChannelType` is a string, not an enum

An enum would require modifying `ChannelTypes` (or the enum itself) and recompiling `MN.Core` each time a channel is added. A string key keeps the channel contract in the implementing class. `ChannelTypes` constants exist as a safe registry for known types and as the anchor for the rate limit surface area test — but they are not required by the interface.

### Why `RoutingRule` carries `ChannelType` as a string

The rule model stores the channel key as a plain string column. This means:

- Rules survive new channel types without a schema migration.
- A rule can reference a channel type that isn't yet deployed — the dispatcher throws at runtime, not at rule creation time.
- No foreign key constraint to a channel registry table; channels are a deployment concern, not a data concern.

The trade-off: invalid `ChannelType` values on rules are caught at dispatch time, not at rule creation time. Mitigation: the admin API validates `ChannelType` against `ChannelTypes` constants on write.

## What `SendAsync` receives

`SendAsync` gets the full `DispatchMessage` (tenant id, event type, payload, dispatch id) and the matched `RoutingRule`. The rule carries channel-specific config if needed — `RoutingRule` has commented-out `ChannelConfigs` and `PayloadCriteria` collections for this purpose. A webhook channel, for example, would read `rule.ChannelConfigs` for the target URL rather than requiring it in the payload.

## Consequences

- **Pros:** `DeliveryProcessorService` is fully closed to new channels; adding a channel is three lines of code + one registration; channel implementations are independently testable.
- **Cons:** Invalid channel key on a rule surfaces at dispatch time rather than rule creation; no compile-time guarantee that a registered channel covers all enum values (doesn't apply — deliberately string-keyed).
- **Production extension point:** webhook channel would read destination URL from `RoutingRule.ChannelConfigs`; SMS channel would read E.164 number from the same; neither requires a schema change to the core routing tables.
