# Product Requirements

## 1. Purpose and scope

Build a production-quality, locally deployable, automated Nifty intraday trading system that can prepare, observe, decide, execute, manage, reconcile, and report without chart-watching. The initial product is a modular monolith on one Windows PC/local server.

Phase 0 defines boundaries only. It does not implement a strategy, connect to Groww, or place an order.

## 2. Product principles

1. Capital protection overrides opportunity.
2. Fail closed for trading and fail open only for monitoring.
3. Decisions are deterministic, explainable, timestamped, and reproducible from retained inputs and versions.
4. Optional context may reduce confidence or block trading; it cannot directly create orders.
5. Zero trades is valid. The approximate three-trade daily maximum is a ceiling, not a target.
6. Live capability is earned through promotion gates, never enabled by configuration drift.

## 3. Users and roles

- **Administrator:** installs, manages users/secrets, changes protected configuration, activates modes, and controls the kill switch.
- **Operator:** monitors health and positions, acknowledges alerts, and can invoke authorised emergency actions.
- **Auditor/Viewer:** read-only access to decisions, configurations, lifecycle history, and reports.
- **System worker:** non-human identity with least-privilege access to scheduled operations.

## 4. Functional requirements

### Operating modes

- Backtest uses historical/replay data and a backtest broker only.
- Paper uses live/replayed data and a deterministic simulator only.
- Live uses a separately registered Groww execution gateway only after activation.
- Mode is server-authoritative, persisted, audited, prominent in all UI routes, and exposed in every operational response.
- Changing presentation state cannot change execution capability.

### Daily lifecycle

Using `Asia/Kolkata` explicitly, the system performs pre-market checks, authentication readiness, instrument synchronisation, prior-session/context loading, feed connection, warm-up, trading enablement, entry cutoff, forced closing, reconciliation, and reporting. UTC is stored internally.

The session calendar comes from a maintainable provider with versioned manual overrides. Late start, unexpected closure, restart, and connectivity loss enter a safe recovery path.

### Data and analysis

All observations carry source time, receive time, source, sequence where available, freshness, validation, and error state. The system ingests approved index, futures, options, OHLCV, OI, depth, VIX/context, and reference data only where a licensed provider supports them.

The regime engine returns regime, bias, confidence, factors for/against, data-quality score, and `TradingPermitted`. Results are immutable audit records.

### Strategies and signals

A versioned `ITradingStrategy` consumes validated snapshots and emits immutable signals. A signal cannot select final quantity, access a broker, or bypass risk. Duplicate and expired signals are rejected.

Only one simple strategy will be introduced after the platform and paper lifecycle exist. Strategy choice is deliberately outside Phase 0.

### Risk, execution, and positions

The risk engine is authoritative and returns approval/rejection, approved quantity, protected prices, reasons, and an immutable risk snapshot. The execution engine uses an explicit state machine, idempotency key, broker correlation, partial-fill accounting, and reconciliation.

Unknown submission status blocks retry and new related orders until reconciled. Local/broker position mismatch blocks new trading and raises a critical alert.

### Dashboard

Angular provides overview, signals, order/position lifecycle, strategies, risk controls, system health, audit/reporting, and emergency controls. SignalR delivers real-time updates. Trading continues safely when the browser is closed.

### Audit and reports

Persist every signal, risk decision, regime decision, configuration change, mode change, order/position transition, reconciliation, alert, provider health event, and exception correlation. Daily reports include P&L, fees, slippage, risk, trades, drawdown, and strategy version.

## 5. Non-functional requirements

- **Safety:** live disabled by default; kill switch and forced-exit paths independently testable.
- **Reliability:** idempotent handlers, bounded retries, restart recovery, database transactions/outbox where needed.
- **Security:** application authentication, RBAC, secure hashes, protected secrets, sanitised logs, input validation, TLS-ready deployment.
- **Auditability:** append-only event history for material actions; actor, reason, before/after, correlation, and time.
- **Performance:** bounded evaluation latency measured against the strategy timeframe; no optimisation before measurement.
- **Data retention:** aggregates retained by policy; raw ticks are partitioned and expired rather than kept indefinitely.
- **Availability:** optional providers degrade independently; required provider failure suspends entries.
- **Testability:** no live broker in automated tests; clocks, IDs, feeds, and gateways are replaceable.

## 6. Initial conservative outcomes

- Maximum completed/attempted entries per day: 3, with configurable lower strategy limits.
- Maximum open positions: 1.
- Default mode: paper only after Phase 1; live capability absent until Phase 9.
- Data uncertainty, broker uncertainty, reconciliation mismatch, daily loss breach, or kill switch: no new entries.
- Forced-exit time and exact monetary/percentage limits remain unset until capital, instruments, broker constraints, and user approval are known.

## 7. Out of scope now

- Strategy implementation or profitability claims
- Live or paper order execution code
- Groww credential setup
- Microservices, Kubernetes, or distributed workers
- LLM-driven trading decisions
- Unlicensed scraping
- Permanent raw-tick retention
- Options selection rules

## 8. Phase 0 acceptance criteria

- Requested architecture/security/risk/API/roadmap documents exist.
- Official Groww documentation is reviewed and limitations are explicit.
- Exact technology baseline and folder proposal are recorded.
- Safety boundaries, external-data gaps, open questions, and phased acceptance criteria are documented.
- No credential, strategy, broker integration, executable order path, or live enablement exists.
- User explicitly approves before Phase 1 scaffolding.
