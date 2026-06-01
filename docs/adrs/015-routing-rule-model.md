# ADR 015: Routing rule model — exact event type match, string channel key

**Scope:** `RoutingRule`, `IRoutingRuleRepository`, `GetMatchingRulesAsync`, `EventRoutingProcessorService`, `RoutingRuleModels`

## Context

The spec says "be prepared to defend your choice" on the rule model. The minimum requirement is: for events matching X, dispatch to channel(s) Y. Both the expressiveness of X and the representation of Y are design decisions.

## Decision

**`RoutingRule` is a flat row: `(TenantId, EventType, ChannelType)` with a unique constraint. Matching is exact string equality on `EventType`. Fan-out is achieved by multiple rules for the same `EventType` pointing to different channels.**

### What a rule looks like

```
TenantId:    <uuid>
EventType:   "user.signup"
ChannelType: "slack"
IsActive:    true
```

A second rule `(same TenantId, "user.signup", "teams")` fans out the same event to Teams. The unique constraint on `(TenantId, EventType, ChannelType)` prevents duplicate rules per channel.

### Why exact match

**Simplicity and auditability.** For a notification router, operators need to see precisely what will fire for a given event type. Wildcard or regex rules introduce ambiguity ("does `user.*` cover `user.signup.invited`?") and ordering/priority questions ("which rule wins if two patterns match?"). Exact match has no edge cases: the rule either matches or it doesn't.

**Production systems commonly use exact match.** PagerDuty routing rules, AWS EventBridge rules (before pattern matching was added), and most webhook routing tables default to exact event type strings. Teams graduate to pattern matching when they have proven the need.

### What was considered and not chosen

| Approach | Why not |
|----------|---------|
| **Regex / glob matching** | Ordering and priority questions; hard to audit; "which rules fire?" is no longer obvious at a glance; correct behavior requires a rule priority column and conflict resolution |
| **Payload predicate matching** (`field == value`) | Requires a payload schema per event type; adds a criteria evaluation engine; worth it for alert routing, overkill for a notification dispatcher |
| **Wildcard prefix** (`user.*`) | Simpler than regex but still introduces overlap ambiguity and requires a defined match-all priority |
| **Topic/category hierarchy** | Maps well to a message bus but adds a separate concept (category) on top of event type without clear benefit |

The commented-out `RoutingRulePayloadCriteria` collection on `RoutingRule` documents where payload-level matching would attach if requirements evolved — the schema extension point exists without forcing the complexity now.

### Fan-out by multiple rules, not by array on a single rule

An alternative model is a single rule with `ChannelTypes: ["slack", "teams"]`. The flat-row approach was chosen because:

- Each rule has an independent `IsActive` flag — you can disable Slack delivery for an event type without touching the Teams rule.
- Each dispatch gets its own `NotificationDispatch` row keyed by `(OriginalMessageId, RuleId)`. A multi-channel rule would need a sub-row per channel anyway, collapsing to the same shape.
- Retry, DLQ, and status tracking are per-dispatch. Keeping rules 1:1 with dispatches makes the relationship straightforward.

### `ChannelType` as a string, not a foreign key

`RoutingRule.ChannelType` is a plain string, not a FK to a channel registry table. See [ADR 014](014-dispatcher-abstraction.md) for the full rationale. Short version: channels are a deployment concern; rules should not break when a new channel is added or an existing one is renamed.

### Why we didn't over-specify the rule model

The `RoutingRule` entity has commented-out `ChannelConfigs` and `PayloadCriteria` collections. The comment in the entity reads:

> *"finalization subject to further requirements gathering — who are we building this for and why?"*

This is the honest reason the model is minimal. A notification router for a DevOps alerting platform (many event types, fine-grained severity filtering, on-call escalation) needs payload predicates and rule priority. One for transactional emails (user.signup → email, full stop) needs almost nothing. One for a multi-product SaaS (thousands of tenants, arbitrary event schemas) might need per-tenant rule templates and wildcard matching.

Building any of those richer models speculatively — without knowing which product shape this is — means either locking in the wrong abstraction or building complexity that no tenant ever uses. Exact match with documented extension points is the minimum that is correct for the specified requirements, leaves the door open for any of the above, and doesn't force a schema migration or a rewrite of the routing engine to get there.

The right time to add payload predicates is when a real tenant says "I need to route only critical alerts to PagerDuty." The right time to add wildcards is when a tenant has 30 event types and wants a catch-all rule. Neither of those conversations has happened yet.

### What "exact match" would extend to first

If the product needed richer matching, the lowest-cost first step is **prefix matching** on `EventType` with an explicit priority column. This covers the most common case ("all `user.*` events go to Slack") without a full expression evaluator. The `RoutingRulePayloadCriteria` extension point would come second, for cases like "only send to PagerDuty if `severity == critical`."

## Consequences

- **Pros:** Predictable and auditable; no priority resolution; operators can enumerate exactly which rules fire for any event; schema is minimal.
- **Cons:** Exact match only — a tenant with 20 event types all routed to the same channel needs 20 rules; no wildcard shorthand.
- **Known limitation acknowledged:** "Rules — exact `EventType` match only" is listed in DESIGN.md. The extension path is documented above and in the commented schema on `RoutingRule`.
