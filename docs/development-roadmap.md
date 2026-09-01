# Development Roadmap and Backlog

Every phase ends with implementation, automated tests, manual verification, known limitations, security review, risk review, updated documentation, and explicit user approval. A later phase is not authorised by approval of an earlier one.

## Phase 0 — Discovery and documentation

**Scope:** official Groww capability review, product/architecture/security/risk boundaries, decisions, external-data questions, and backlog.

**Acceptance criteria**

- All requested documents exist and link to authoritative sources.
- Supported, partial, unverified, deprecated, and unsupported Groww capabilities are distinguished.
- Technology baseline and folder structure are proposed.
- No executable strategy, broker call, credential, or order path exists.
- Open questions and safety gates are recorded.

**Current known limitations:** documentation has not been validated against a real subscribed account; Groww feed and unattended auth require confirmation; repository is not yet Git-initialised.

## Phase 1 — Repository foundation

**Status:** Implemented on 2026-07-30; awaiting promotion approval.

**Backlog**

- Initialise Git with secret-safe `.gitignore`, editor settings, licence decision, and branch policy.
- Pin .NET/Node/package versions; create solution/projects and Angular workspace.
- Add central package management, nullable/warnings/analyzers, formatting, and architecture tests.
- Add PostgreSQL Docker Compose with health checks and non-secret local defaults.
- Add Serilog, OpenAPI, liveness/readiness, SignalR shell, and strongly typed configuration.
- Add basic local authentication/RBAC and an always-visible server-sourced mode banner.
- Add CI build, unit tests, Angular lint/test/build, secret scan, and dependency audit.

**Acceptance criteria**

- Clean checkout starts dependencies with documented commands.
- Backend and Angular build/tests pass without credentials or live network access.
- Live mode/gateway does not exist in DI; default visible mode is non-live.
- Health endpoints distinguish liveness/readiness and leak no secrets.
- Login/RBAC/antiforgery happy and denial paths are tested.
- README contains exact locked versions and local setup.

**Verification note:** backend and frontend builds/tests pass with bounded single-worker commands. The current machine does not have Docker, so the PostgreSQL Compose definition was not runtime-tested. Persistent database-backed identity, antiforgery UI integration, and migrations remain Phase 2 work.

## Phase 2 — Domain and database

**Status:** Implemented on 2026-08-02; awaiting promotion approval.

**Backlog:** core value objects/enums/entities, mode-scoped configuration, lifecycle events, EF mappings/migrations, audit/outbox, concurrency and retention model.

**Acceptance criteria**

- Required entities persist with UTC timestamps, explicit precision, constraints, mode/session ownership, and migrations.
- Immutable records and unique idempotency constraints are enforced by tests.
- Audit before/after redaction and configuration concurrency are tested.
- PostgreSQL integration tests run in an isolated database.

**Verification note:** an opt-in Testcontainers test applies the migration and verifies configuration/audit persistence against PostgreSQL 18.4 when `RUN_POSTGRES_TESTS=true`. Docker is absent on the current host, so that test is skipped here; EF model, append-only enforcement, API, and domain tests execute without Docker.

## Phase 3 - Broker contract and paper sandbox

**Status (implemented 2026-08-02; awaiting promotion approval):** broker-neutral `IBrokerGateway`, deterministic `PaperBrokerGateway`, explicit replay fill control, domain transition guards, idempotent client references, partial fills, cancellation, position netting/closure, reconciliation, and strict Paper-mode registration. No Backtest or Groww gateway was added.

**Acceptance criteria**

- Paper lifecycle covers validation/rejection, acknowledgement, partial fill, cancel, entry, exit, close, and duplicate submission.
- Reconciliation mismatch blocks trading, and non-Paper modes cannot resolve the paper gateway.
- Automated architecture tests prove live gateway is absent.
- Simulations are repeatable because prices and fill-cycle sizes are explicit and no randomness is used.

**Known limitation:** durable event logging, process-restart reconstruction, unknown broker outcomes, and crash-point recovery remain Phase 7/8 acceptance criteria. The Phase 3 state store is deliberately in-memory.

## Phase 4 - Groww read-only integration

**Status (implemented 2026-08-02; awaiting promotion approval):** official contracts re-reviewed; backend-only manual access-token provider, user profile, quote/OI data, non-deprecated historical candles, bounded/schema-validated instrument CSV, atomic CASH/FNO upsert, HttpClientFactory resilience, strict error mapping, and sanitised response fixtures implemented. No order commands exist.

**Acceptance criteria**

- Read-only capability works against explicit user-provided credentials without logging secrets.
- Contract tests cover success, errors, unknown fields/enums, throttling, timeout, malformed and stale payloads.
- Instrument import is atomic/versioned; feed reconnect/gap handling is demonstrated.
- Adapter package contains no reachable order submission.

**Known limitations:** the feed remains unsupported because Groww publishes Python SDK behavior but no .NET/wire protocol; automatic API-key/TOTP token generation awaits clarification of daily approval and secret-retention rules; live credential validation was not performed; instrument checksum/versioning is undocumented.

## Phase 5 - Market-data engine

**Status (implemented 2026-08-02; awaiting promotion approval):** Groww quote normalization, source/receive UTC timestamps, cumulative-volume deltas, stale/future/duplicate/out-of-order/gap rejection, latched feed health, deterministic candle aggregation, candle/provider persistence, SMA/EMA/VWAP/ATR, configuration validation, and health API implemented.

**Acceptance criteria**

- Duplicate/out-of-order/gap/stale data tests pass.
- Indicator results match trusted fixtures at numeric boundaries.

**Known limitations:** scheduled ingestion, gap backfill, streaming, multi-timeframe warm-up, native partitions, and automated retention await measured paper-soak requirements. Raw observations are deliberately not persisted indefinitely.
- Trading readiness goes false on required-data breach and recovers only after warm-up.
- Retention and replay are documented and tested.

## Phase 6 - Market-regime engine

**Status (implemented 2026-08-02; awaiting promotion approval):** deterministic gap/trend/volatility/range classification, directional bias, bounded confidence, supporting and contradicting evidence, data-quality/trading gate, append-only persistence, latest monitoring projection, and representative historical replay tests implemented.

**Acceptance criteria**

- Rule boundaries and conflicting/missing input scenarios are tested.
- Same versioned snapshot/configuration yields the same result.
- Every result stores factors for/against and trading permission.
- Historical replay report states limitations without profitability claims.

**Known limitations:** scheduled summary construction, option-chain/VIX/external inputs, confidence calibration, and statistical threshold evaluation remain later work. No profitability claim is made.

## Phase 7 - First strategy in paper mode

**Status (functional lifecycle implemented 2026-08-02; promotion gate remains open):** versioned opening-range breakout, immutable proposal, preliminary risk sizing/rejection, idempotent paper entry, partial fills, opposing exit, position closure, and realised-P&L report are implemented and tested.

**Acceptance criteria**

- Complete paper lifecycle succeeds under replay and live-data paper mode.
- Every entry/invalidating/expiry/cooldown rule has tests.
- Duplicate/expired signals cannot execute.
- Fees and slippage are modeled; results are reproducible and not represented as proof of profit.

**Status update (local, 2026-08-03):** append-only broker recovery, scheduled completed-candle evaluation, regime persistence, ORB evaluation, conservative risk sizing, duplicate suppression, entry-slippage rejection, configurable turnover-cost estimation, SL/target/time/kill-switch exits, pre-action reconciliation, closure audit, and dashboard monitoring are implemented. Automated tests pass. The promotion gate remains open until controlled replay, PostgreSQL restart testing, and a live-market paper soak are manually verified. Cross-store atomicity and instruction-boundary failure injection move to Phase 8 hardening; shadow/live promotion remains prohibited.

**Option paper-execution increment (local, 2026-08-03):** Nifty remains the monitored and charted signal instrument. A qualifying directional signal selects a deterministic near-expiry ATM Nifty call or put, requires validated live bid/offer, volume, open interest, spread and premium, applies option-premium protective prices, sizes only in complete broker lots, and enters/exits through the durable paper broker. Restart reconstruction and reconciliation use the actual option instrument. No Groww order endpoint is called.

**Session-readiness increment (local, 2026-08-03):** instrument synchronisation now includes Nifty futures and persists the official Groww symbol. Startup backfills up to seven days of one-minute Nifty spot and nearest-future history, live polling continues for the nearest future, and ORB relative-volume confirmation uses aligned futures candles rather than unavailable index volume. The authenticated dashboard exposes six deterministic readiness gates.

**Live workspace increment (local, awaiting deployment approval):** the authenticated Nifty command view now also shows automation readiness, daily trade/P&L state, active paper position, and an audited administrator kill switch. It does not add live order capability. Production activation additionally requires secure daily Groww authentication and an in-session paper soak.

## Phase 8 — Risk and execution hardening

**Status (engineering implemented 2026-08-17; promotion evidence pending):** mandatory portfolio-safe
paper sizing, complete-lot risk/exposure limits, unrealised-loss daily stop, distinct-contract
concurrency, duplicate active-contract suppression, append-before-mutate broker journal,
unknown-commit replay, partial-fill/restart recovery, reconciliation blocking, per-position quote
isolation, kill-switch/forced exits, portfolio dashboard telemetry, failure simulations, and a Phase 8
runbook are implemented. Automated live orders remain disabled. Promotion is blocked until the documented
multi-session soak and opt-in PostgreSQL restart verification are reviewed.

### Phase 9 controlled-live foundation (in progress, 2026-09-01)

- Added a Groww F&O execution adapter for create, status-by-reference, cancellation,
  trades/fill-price retrieval, positions, and reconciliation.
- Added official Groww Smart Order OCO protection for broker-side target and stop-loss.
- The execution HTTP client has no automatic retry policy. Unknown POST outcomes block
  resubmission and require reference lookup/reconciliation.
- Added an administrator-only, password-confirmed, daily live-arm record. It is disabled
  by server configuration, restricted to NIFTY/SENSEX and exactly one lot per order.
- Live strategy automation is not yet connected and no real order has been placed.
- Promotion remains blocked until restart/partial-fill/rejection tests, controlled UI,
  full reconciliation, and minimum-quantity market validation pass.

**Acceptance criteria**

- Concurrent approvals cannot exceed daily/exposure limits.
- Unknown POST outcome is never blindly retried.
- Restart/disconnect/partial-fill/mismatch/forced-exit tests pass.
- Kill switch blocks entries and reconciles exit attempts.
- Paper soak test has no unresolved state divergence.

**Verification:** see `docs/phase-8-risk-execution-hardening.md` for the acceptance matrix, manual
verification, security/risk review, and known limitations.

## Phase 8.5 — Strategy intelligence and validation

**Status update (local, 2026-08-17):** engineering implementation is complete; paper evidence and controlled replay are pending. The system persists minute-bucketed option price paths, measures MFE, MAE, profit giveback, exit quality, expectancy and profit factor, aggregates the decision funnel, and produces sample-size-gated advisory recommendations. It cannot modify strategy parameters automatically. See `docs/phase-8-5-strategy-intelligence.md`.

**Shadow structure update (local, 2026-08-18):** trend efficiency, chop, swing consistency, move maturity and remaining-room scoring are implemented as a non-blocking counterfactual layer. Its verdict and evidence are persisted and compared with realised results; paper execution remains unchanged pending review.

## Phase 9 — Groww order integration

**Backlog:** re-review create/modify/cancel/query contracts, implement behind build/config/activation gates, controlled minimum-quantity test plan, disable after test.

**Acceptance criteria**

- Explicit approval for each controlled test; test uses permitted market/account conditions.
- Client reference, broker acknowledgement, fills, protection, cancellation and reconciliation are evidenced.
- Ambiguous outcomes suspend trading.
- Live activation automatically expires and is disabled after test.

## Phase 10 — Additional strategies and context

**Backlog:** one candidate/provider at a time, licensing/terms, normalized confidence/availability, ablation and out-of-sample evaluation.

**Acceptance criteria**

- Each addition has independent contract/rule/replay tests and measurable evaluation criteria.
- Optional outage is safe.
- Inputs without measurable value or reliable provenance are rejected.
- No LLM directly influences order creation.

## Phase 11 — Deployment

**Backlog:** Windows Service/supervision, production static hosting, startup/recovery, TLS, backups/restores, log retention, updates/rollback, emergency runbook, paper soak.

**Acceptance criteria**

- Automatic startup enters suspended recovery and reaches readiness only after reconciliation.
- Backup restore and disaster/restart drills pass.
- Secrets are protected under least-privilege service identity.
- Operator can execute and verify emergency shutdown/kill procedures.
- Approved-duration paper soak meets defined reliability thresholds.

## Promotion sequence

Backtesting → paper trading → shadow mode → minimum-quantity live testing → restricted live trading → normal live operation.

Promotion evidence must include test report, manual verification record, open-risk register, security review, limitations, rollback/recovery plan, and signed user approval. No strategy skips a stage.

## Cross-cutting definition of done

- Small testable methods, dependency injection, nullable enabled, cancellation tokens, no static mutable business state.
- UTC internally; explicit `Asia/Kolkata` conversion at session boundaries.
- Business logic absent from controllers and Angular components.
- Structured logs and audit events are sanitised and correlated.
- Documentation/setup/migrations/fixtures updated.
- Build, tests, lint, and secret scan pass.
- No live broker test runs automatically.

## External-data discovery backlog

For GIFT Nifty, global indices, crude, USD/INR, India VIX, FII/DII, economic events, news, and exchange calendar, evaluate:

1. Authoritative source and legal/licensing/redistribution terms.
2. API/stream contract, coverage, timestamps, revision policy, quotas, and cost.
3. Freshness/SLA and historical availability for replay.
4. Identifier, holiday/time-zone, and corporate-action normalization.
5. Failure behaviour, confidence model, retention, and audit evidence.
6. Whether ablation/out-of-sample evidence justifies complexity.

No scraping is approved. FII/DII remains previous-session context unless a verified real-time source is later contracted.

## Immediate approval gate

Phase 0 is the stopping point. Phase 1 may begin only after the user reviews this baseline, resolves or explicitly defers blocking choices, and grants approval.
