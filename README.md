# morrow-notifications

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

No Docker, no external services. The datastore is a SQLite file (`morrow-notifications.db`) created automatically on first run.

---

## Run

```bash
dotnet run --project morrow-notifications/morrow-notifications.csproj
```

The API starts on `http://localhost:5252`.

---

## Exercising the API

Several `.http` files are in `morrow-notifications/`. Open them natively in Visual Studio or in VS Code with the [REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client) extension — each request has a **Send Request** link above it.

| File | Purpose |
|---|---|
| `morrow-notifications.core-loop.http` | The happy path: create tenant → rule → ingest. Start here. |
| `morrow-notifications.tenants.http` | Full tenant CRUD including activate/deactivate. |
| `morrow-notifications.rules.http` | Full routing rule CRUD; includes a fan-out example (same event type, two channels). |
| `morrow-notifications.events.http` | Ingest: matched event, unrouted event (→ dead-letter), unknown tenant. |
| `morrow-notifications.dead-letters.http` | Read dead-lettered messages, all tenants or per-tenant. |

Each file has `@tenantId` (and `@ruleId` where needed) at the top — paste the IDs returned from earlier requests before running individual calls.

---

## Architecture

See DESIGN.md.

---

## Running the Tests

Four tests are in `MN.Tests/`:

```bash
dotnet test MN.Tests/MN.Tests.csproj
```

They spin up the real application pipeline in-process (via `WebApplicationFactory`) against an isolated per-run SQLite database and verify:

- Tenant B cannot read Tenant A's routing rules
- Tenant B's events are not dispatched by Tenant A's rules
- Tenant A exhausting its rate limit does not block Tenant B
- All entities with a `TenantId` property implement `ITenantScoped` (contract enforcement)

---

## Known Limitations and What I'd Do Differently

See DESIGN.md for the full list.

---

## AI Tools

Built with **GitHub Copilot** (Claude Sonnet 4.6) throughout — architecture, scaffolding, implementation, and tests. All code was reviewed and iterated on interactively.  Opus 4.6 (or 4.7) would have likely yielded better results in a shorter time frame, but is not available on my personal Copilot plan.

Double-checked and polished with Gemini's 3.5 Thinking model (using concat-source.ps1).