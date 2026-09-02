# Automated Indian-Market Trading System

Safety-first automated intraday trading platform for the Indian market. The **Phase 7 first-strategy paper lifecycle is implemented**. No Groww order execution or live-order code exists.

## Status

- Phase: 7 - opening-range breakout in paper mode
- Current development gate: Nifty and Sensex monitor their cash indices and execute options in paper mode; Natural Gas monitors only the nearest MCX Natural Gas Mini future and issues durable four-lot manual-execution paper alerts. Natural Gas is permanently excluded from Groww order routing.
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
- Daily Groww access tokens entered by an administrator are encrypted with ASP.NET Core Data Protection before PostgreSQL persistence; plaintext tokens are never returned to Angular or written to normal logs.
- Tests cannot resolve a live broker or load production credentials.

## Documentation

- [Price-action strategy engine](docs/price-action-strategy-engine.md)

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
8. A Groww live F&O adapter and automatic Nifty/Sensex execution worker exist behind a
   password-confirmed, daily-expiring controlled-live gate. Normal
   operation remains Paper; no live automation is registered and no live order is placed
   unless broker validation, reconciliation checks, and explicit daily activation are
   completed. Controlled validation and subsequent eligible orders use five lots.
9. Market-data tests cover stale/future/invalid/duplicate/out-of-order/gap handling, health latching, Groww normalization, OHLCV/OI aggregation, SMA, EMA, VWAP, ATR, and persistence orchestration.
10. Regime tests cover gap continuation/rejection/reversal, bullish/bearish trends, volatility expansion/compression, conflicting evidence, low-quality blocking, and replay persistence.
11. Strategy tests cover breakout qualification, no-signal gates, cooldown/trade limits, risk sizing/rejection, partial fills, entry, exit, realised P&L reporting, and proof that rejection submits no order.
12. Automated paper-position tests cover directional stop/target exits, the 15:15 intraday cutoff, and stale-price exit blocking.

## Groww read-only setup

Generate an access token using Groww's officially supported Trading API page. An administrator can enter it in the protected dashboard workflow; the backend encrypts it before PostgreSQL persistence and automatically retries the feed without an application restart. The environment variable remains a backend-only fallback. Never put the token in `appsettings.json`, `.env`, Angular, logs, or Git:

```powershell
$env:GROWW_ACCESS_TOKEN = '<temporary access token>'
dotnet run --project src/TradingSystem.Api
Remove-Item Env:GROWW_ACCESS_TOKEN
```

Groww documents that manually generated access tokens expire daily at 06:00. The dashboard shows the status and next expiry, but token generation/approval is still a daily manual Groww action. API-key approval and TOTP token-generation flows are not automated because unattended approval and secret-retention requirements remain unresolved.

## Known limitations

- Docker is absent on the current verification host, so the checked-in PostgreSQL test is skipped here.
- Account recovery, MFA enrollment, and user-administration UI are not implemented.
- Native time-series partitions and retention jobs wait for measured Phase 5 ingestion volume.
- SignalR publishes the authenticated live Nifty and paper-automation workspace projection.
- The worker emits only a safe foundation heartbeat.
- Paper orders, fills, cancellations, idempotency keys, order numbering, and positions are reconstructed from an append-only PostgreSQL journal after restart.
- Phase 8 mandatory paper risk remains active even when permissive strategy evaluation is enabled:
  complete-lot quantity is bounded by per-trade risk, aggregate premium exposure, daily loss
  including open losses, open-position capacity, data freshness, reconciliation, and the kill
  switch. The dashboard exposes portfolio exposure and risk-at-stop telemetry.
- An administrator can explicitly ignore the daily-loss entry stop for the current IST paper
  session. The action is audited, automatically expires the next day, and cannot affect live mode.
- Fill prices are explicit deterministic test/replay inputs. They are not a claim of realistic spread, latency, fees, or slippage modeling.
- Backtest and Groww execution gateways are not implemented, and resolving an execution gateway outside Paper mode fails closed.
- Groww streaming feed support is not implemented: official documentation describes the Python SDK but does not publish a .NET or wire-level feed contract.
- Instrument synchronization upserts documented CASH/FNO types atomically but does not deactivate missing instruments because the CSV has no documented completeness/version guarantee.
- The Azure paper environment polls the documented Groww Nifty quote endpoint during the NSE session and persists validated observations/candles. Streaming, holiday-calendar integration, and gap backfill remain incomplete.
- Native PostgreSQL candle partitioning and retention jobs remain deferred until Phase 5 volume is measured in a paper soak test.
- Regime thresholds are conservative initial hypotheses, not evidence of profitability, and must be calibrated only through replay/out-of-sample analysis.
- Strategy signals and risk decisions are persisted before paper submission. Nifty is the signal instrument; qualifying bullish/bearish signals deterministically select a near-expiry call/put, validate its quote, size in whole lots, reject an identical active contract/direction, and submit only to the paper broker. The coordinator reconstructs actual option entries after restart, reconciles before acting, isolates per-position quote failures, monitors premium protection and 15:15 exits, and audits closures. See `docs/phase-8-risk-execution-hardening.md`.
- Phase 8.5 adds append-only option price-path research, exit-quality metrics, decision-funnel analytics and advisory recommendations. It never changes strategy configuration automatically. See `docs/phase-8-5-strategy-intelligence.md`.
- The structure-quality layer records whether chop, trend maturity, directional conflict or limited
  room rejects each candidate. Its evidence is visible in research logs and performance reports.
- Paper entries now enforce the structure-quality veto: only aligned clean/developing trends may
  proceed, while chop, mature moves, ambiguous transitions and short-term directional conflicts are
  persisted as `EntryQualityRejected` research outcomes.
- Paper option P&L uses a versioned Groww/NSE charge schedule per executed order, including partial exits, rather than a generic turnover estimate. Brokerage, STT, exchange/IPFT, SEBI, GST and stamp duty are retained in the trade report.
- Session startup imports up to seven days of official Groww one-minute candles for Nifty spot and the nearest Nifty future. Spot OHLC drives the chart and the ORB, opening-range-retest, and VWAP-pullback setups; futures volume contributes confirmation without universally vetoing valid price action. The dashboard exposes each readiness prerequisite independently.
- The Azure paper environment is deployed at `https://sarthico.com` and `https://www.sarthico.com`. It remains monitoring/demo-only until the remaining promotion gates are complete.
- The currently deployed workspace does not yet contain this local Phase 7 increment. Deployment is intentionally deferred until explicit approval. See `docs/live-nifty-workspace.md` and `docs/paper-trading-automation.md`.
- npm production dependency audit reports zero known vulnerabilities; CI rejects high/critical findings.

## Disclaimer

This software automates user-defined controls; it is not investment advice and does not promise returns. No strategy may be called profitable without statistically valid backtesting, out-of-sample evaluation, realistic fees/slippage, and paper-trading evidence.
