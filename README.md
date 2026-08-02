# Automated Nifty Trading System

Safety-first automated intraday trading platform for the Indian market. The **Phase 7 first-strategy paper lifecycle is implemented**. No Groww order execution or live-order code exists.

## Status

- Phase: 7 - opening-range breakout in paper mode
- Next gate: Phase 7 recovery/audit limitation review before Phase 8
- Live trading: prohibited and disabled by design
- Git repository: initialized on `main`

Priority order: capital protection, correctness, deterministic behaviour, auditability, reliability, security, performance, trade count, and only then any profitability hypothesis.

## Pinned technology

| Component | Version |
|---|---:|
| .NET SDK | 10.0.302 |
| ASP.NET Core / EF Core | 10.0.10 |
| Npgsql EF provider | 10.0.3 |
| Angular core | 22.1.0 |
| Angular CLI / build | 22.1.2 |
| Node.js | 24.15.0 LTS |
| TypeScript | 6.0.3 |
| PostgreSQL | 18.4 |

Sources: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), [EF Core releases](https://learn.microsoft.com/en-us/ef/core/what-is-new/), [Angular compatibility](https://angular.dev/reference/versions), [Node releases](https://nodejs.org/en/about/previous-releases), and [PostgreSQL versioning](https://www.postgresql.org/support/versioning/).

## Repository structure

```text
src/
  TradingSystem.Api/
  TradingSystem.Application/
  TradingSystem.Domain/
  TradingSystem.Infrastructure/
  TradingSystem.Worker/
tests/
  TradingSystem.UnitTests/
  TradingSystem.IntegrationTests/
  TradingSystem.ContractTests/
  TradingSystem.ArchitectureTests/
trading-system-web/
docs/
docker-compose.yml
TradingSystem.slnx
```

## Safety boundaries

- Backtest, paper, and live modes are server-side capabilities and persisted scopes, not UI labels.
- Live mode is rejected by validated startup options and no live gateway exists.
- Strategies will emit immutable proposals; only future risk/execution layers may submit.
- Unknown broker outcomes will be reconciled, never blindly retried.
- The Groww adapter exposes only profile, quote, historical-candle, and instrument-master GET operations; it contains no order mutation.
- Append-only decisions, signals, events, snapshots, trades, and audits cannot be updated or deleted through the DbContext.
- Broker secret columns do not exist; only opaque secret references may be persisted.
- Tests cannot resolve a live broker or load production credentials.

## Documentation

- [Product requirements](docs/product-requirements.md)
- [Architecture](docs/architecture.md)
- [Database model](docs/database-model.md)
- [Paper broker](docs/paper-broker.md)
- [Market-data engine](docs/market-data-engine.md)
- [Market-regime engine](docs/market-regime-engine.md)
- [First paper strategy](docs/first-paper-strategy.md)
- [Azure paper deployment](docs/azure-deployment.md)
- [Groww capability matrix](docs/groww-api-capability-matrix.md)
- [Security model](docs/security-model.md)
- [Risk controls](docs/risk-controls.md)
- [Development roadmap](docs/development-roadmap.md)
- [ADR-001: modular monolith](docs/decisions/ADR-001-modular-monolith.md)

## Build and test

Prerequisites: .NET SDK 10.0.302, Node.js 24.15.0, npm 11.12.1, and Docker Desktop with Compose.

```powershell
dotnet restore TradingSystem.slnx
dotnet build TradingSystem.slnx --configuration Release
dotnet test TradingSystem.slnx --configuration Release

Set-Location trading-system-web
npm ci
npm run build
npm run test:ci
```

The PostgreSQL Testcontainers test is opt-in:

```powershell
$env:RUN_POSTGRES_TESTS = 'true'
dotnet test tests/TradingSystem.IntegrationTests --configuration Release
Remove-Item Env:RUN_POSTGRES_TESTS
```

## PostgreSQL setup

Copy `.env.example` to `.env`, replace its placeholder password, and start PostgreSQL:

```powershell
docker compose up -d postgres
docker compose ps
```

Set the design-time connection only for the migration command. The application does not auto-migrate:

```powershell
$env:TRADING_SYSTEM_DB = '<local PostgreSQL connection string>'
dotnet tool restore
dotnet ef database update `
  --project src/TradingSystem.Infrastructure `
  --startup-project src/TradingSystem.Infrastructure `
  --configuration Release
Remove-Item Env:TRADING_SYSTEM_DB
```

Store the runtime connection string outside source control:

```powershell
dotnet user-secrets set "ConnectionStrings:TradingDatabase" "<local connection string>" `
  --project src/TradingSystem.Api
```

## One-time administrator setup

Set `IdentityBootstrap:Enabled`, `Username`, `Email`, and `Password` through .NET user-secrets, then start the API once. ASP.NET Core Identity hashes the password and assigns the Administrator role. The process intentionally stops after success so bootstrap cannot remain silently enabled.

For Azure, use the manually dispatched `Azure administrator bootstrap` workflow. Add
`BOOTSTRAP_ADMIN_PASSWORD` as an `azure-paper` environment secret first. The workflow
temporarily supplies the bootstrap settings to App Service, removes all four settings,
restarts the application, and verifies the administrator through the antiforgery-protected
login endpoint. Delete the GitHub environment secret after the workflow succeeds.

Remove all bootstrap secrets before restarting:

```powershell
dotnet user-secrets remove "IdentityBootstrap:Enabled" --project src/TradingSystem.Api
dotnet user-secrets remove "IdentityBootstrap:Username" --project src/TradingSystem.Api
dotnet user-secrets remove "IdentityBootstrap:Email" --project src/TradingSystem.Api
dotnet user-secrets remove "IdentityBootstrap:Password" --project src/TradingSystem.Api
```

Run with `dotnet run --project src/TradingSystem.Api`. Obtain an antiforgery token from `GET /api/security/antiforgery-token` before login/logout mutations. Development OpenAPI is at `/openapi/v1.json`; `/health/live` checks the process and `/health/ready` checks PostgreSQL.

## Phase 7 verification

1. Backend builds with zero warnings/errors.
2. Unit, integration, architecture, and foundation contract tests pass.
3. With Docker, the opt-in test applies the migration to PostgreSQL 18.4 and persists configuration/audit records.
4. Angular build and tests pass.
5. NuGet has no known vulnerable packages; npm has no high/critical findings.
6. Groww fake-response tests cover required headers, profile, quote/OI, historical candles, instrument CSV, documented failures, throttling, malformed JSON, cancellation, and secret redaction.
7. Reflection tests prove the Groww read-only contract contains no order or trade mutation method.
8. No `GrowwBrokerGateway` or broker order endpoint exists; `Trading__Mode=Live` prevents startup.
9. Market-data tests cover stale/future/invalid/duplicate/out-of-order/gap handling, health latching, Groww normalization, OHLCV/OI aggregation, SMA, EMA, VWAP, ATR, and persistence orchestration.
10. Regime tests cover gap continuation/rejection/reversal, bullish/bearish trends, volatility expansion/compression, conflicting evidence, low-quality blocking, and replay persistence.
11. Strategy tests cover breakout qualification, no-signal gates, cooldown/trade limits, risk sizing/rejection, partial fills, entry, exit, realised P&L reporting, and proof that rejection submits no order.

## Groww read-only setup

Generate an access token using Groww's officially supported Trading API page. Supply it only to the backend process through the configured environment variable; do not put it in `appsettings.json`, `.env`, PostgreSQL, Angular, or Git:

```powershell
$env:GROWW_ACCESS_TOKEN = '<temporary access token>'
dotnet run --project src/TradingSystem.Api
Remove-Item Env:GROWW_ACCESS_TOKEN
```

Groww documents that manually generated access tokens expire daily at 06:00. API-key approval and TOTP token-generation flows are not automated in Phase 4 because unattended approval and secret-retention requirements remain unresolved.

## Known limitations

- Docker is absent on the current verification host, so the checked-in PostgreSQL test is skipped here.
- Account recovery, MFA enrollment, and user-administration UI are not implemented.
- Native time-series partitions and retention jobs wait for measured Phase 5 ingestion volume.
- SignalR has a secured hub boundary but no operational projections.
- The worker emits only a safe foundation heartbeat.
- The Phase 3 paper store is intentionally in-memory. Durable event persistence and process-restart recovery are Phase 7/8 hardening work; paper state is lost when the process exits.
- Fill prices are explicit deterministic test/replay inputs. They are not a claim of realistic spread, latency, fees, or slippage modeling.
- Backtest and Groww execution gateways are not implemented, and resolving an execution gateway outside Paper mode fails closed.
- Groww streaming feed support is not implemented: official documentation describes the Python SDK but does not publish a .NET or wire-level feed contract.
- Instrument synchronization upserts documented CASH/FNO types atomically but does not deactivate missing instruments because the CSV has no documented completeness/version guarantee.
- There is no scheduled live polling loop yet; Phase 5 provides the validated processing pipeline consumed by later scheduling/feed work.
- Native PostgreSQL candle partitioning and retention jobs remain deferred until Phase 5 volume is measured in a paper soak test.
- Regime thresholds are conservative initial hypotheses, not evidence of profitability, and must be calibrated only through replay/out-of-sample analysis.
- Paper lifecycle state and reports remain process-local; durable restart reconstruction and append-only lifecycle persistence must be completed before Phase 8 promotion.
- Azure deployment code is prepared but not provisioned. It is restricted to paper mode and remains monitoring/demo-only until database migration/bootstrap jobs, durable Data Protection keys, and paper-state restart recovery are complete.
- npm reports three moderate development-only transitive findings through Angular CLI's MCP dependency; CI rejects high/critical findings.

## Disclaimer

This software automates user-defined controls; it is not investment advice and does not promise returns. No strategy may be called profitable without statistically valid backtesting, out-of-sample evaluation, realistic fees/slippage, and paper-trading evidence.
