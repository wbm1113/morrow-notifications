# morrow-notifications

Multi-tenant notification platform — ingest events, apply per-tenant routing rules, dispatch to Slack/Teams (stub channels).

## For reviewers (start here)

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) only. No Docker, no external services.

**One command — build, unit tests, start API, full API smoke suite, stop API:**

```powershell
./verify.ps1
```

Mac/Linux:

```bash
chmod +x verify.sh && ./verify.sh
```

Expected output ends with `ALL CHECKS PASSED`. The PowerShell path runs **20 unit tests** plus **51 API checks**; the bash script runs the same unit tests plus a shorter curl smoke path.

**Manual path** (if you prefer):

```bash
dotnet test MN.Tests/MN.Tests.csproj
dotnet run --project morrow-notifications/morrow-notifications.csproj
```

Then either:

- `./run-e2e-tests.ps1` — full API smoke suite (PowerShell), or
- Open `morrow-notifications/morrow-notifications.core-loop.http` in VS Code ([REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client)) or Visual Studio and run all three requests top-to-bottom (no copy/paste — IDs chain automatically).

API base URL: `http://localhost:5252`

---

## Run

```bash
dotnet run --project morrow-notifications/morrow-notifications.csproj
```

SQLite database (`morrow-notifications.db`) is created automatically on first run.

---

## Exercising the API

| File | Purpose |
|---|---|
| `morrow-notifications.core-loop.http` | **Start here** — self-contained happy path (tenant → rule → ingest). |
| `morrow-notifications.tenants.http` | Full tenant CRUD including activate/deactivate. |
| `morrow-notifications.rules.http` | Full routing rule CRUD; multi-channel fan-out example. |
| `morrow-notifications.events.http` | Ingest: matched, unrouted (→ dead-letter), unknown tenant. |
| `morrow-notifications.dead-letters.http` | Read dead-lettered messages. |

Files other than `core-loop.http` use `@tenantId` / `@ruleId` placeholders at the top — paste IDs from earlier responses, or use `run-e2e-tests.ps1` / `verify.ps1` for automated coverage.

---

## Architecture

See [DESIGN.md](DESIGN.md). Non-obvious trade-offs are also captured in [docs/adrs/](docs/adrs/README.md).

---

## Running the Tests

```bash
dotnet test MN.Tests/MN.Tests.csproj
```

In-process tests (via `WebApplicationFactory`) verify:

- Tenant B cannot read Tenant A's routing rules
- Tenant B's events are not dispatched by Tenant A's rules
- Tenant A exhausting its rate limit does not block Tenant B
- Partial channel failure dead-letters only the failed dispatch
- Outbox lease claiming under concurrent publishers

---

## Known Limitations and What I'd Do Differently

See [DESIGN.md](DESIGN.md).

---

## AI Tools

Built with **GitHub Copilot** (Claude Sonnet 4.6) throughout — architecture, scaffolding, implementation, and tests. All code was reviewed and iterated on interactively.

Double-checked and polished with Gemini's 3.5 Thinking model (using `concat-source.ps1`) along with Cursor AI.
