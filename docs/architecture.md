# Architecture

## 1. Context

The system is a safety-critical modular monolith deployed initially on one Windows host. ASP.NET Core exposes HTTP/SignalR, a .NET worker owns continuous processing, Angular is a monitoring/configuration client, and PostgreSQL is the system of record.

```text
Angular dashboard
   │ HTTPS + SignalR
TradingSystem.Api ───── Application use cases ───── Domain model
                          │                 │
TradingSystem.Worker ─────┘                 │
                          Infrastructure adapters
                    PostgreSQL │ Groww │ data providers
```

Dependencies point inward: Domain has no infrastructure dependency; Application orchestrates domain ports; Infrastructure implements ports; Api and Worker are composition roots.

## 2. Projects and responsibilities

| Project | Responsibility | Must not contain |
|---|---|---|
| `TradingSystem.Domain` | Entities, value objects, invariants, state transitions, domain events | EF, HTTP, logging adapters |
| `TradingSystem.Application` | Commands/queries, ports, orchestration, validation, policy interfaces | Broker DTO assumptions, controllers |
| `TradingSystem.Infrastructure` | EF Core, PostgreSQL, Groww/data adapters, secret adapters, clock/calendar adapters | Strategy business decisions |
| `TradingSystem.Api` | Authenticated API, OpenAPI, SignalR, health endpoints, composition | Trading logic |
| `TradingSystem.Worker` | Schedulers and hosted process composition | Duplicate domain/application logic |
| Test projects | Unit, integration, contract, architecture checks | Live credentials or live submission |
| `trading-system-web` | Presentation, client state, typed API client | Secrets, server-authoritative decisions |

## 3. Logical modules

- Identity and access
- Configuration and mode governance
- Trading session/calendar
- Broker gateway
- Instrument/reference data
- Market-data ingestion and quality
- External context
- Indicators
- Market regime
- Strategy catalogue/evaluation
- Immutable signals
- Risk
- Execution/order lifecycle
- Position management/reconciliation
- Notifications/alerts
- Audit/reporting
- Provider/system health

Modules communicate through application contracts and domain events, not direct table access. Database schemas or table prefixes provide ownership boundaries without separate databases.

## 4. Core ports

The Phase 3 `IBrokerGateway` contract exposes mode, order submission/query/cancellation, position snapshots, and reconciliation. It is intentionally narrow and broker-neutral. Later read-only market-data and account capabilities should be separate capability interfaces instead of expanding every gateway implementation. Planned implementations:

- `BacktestBrokerGateway`: historical clock and deterministic fills; no network path.
- `PaperBrokerGateway`: live/replay observations with configurable deterministic fill model; no Groww order path.
- `GrowwBrokerGateway`: capability-gated adapter based only on official contracts; read-only until Phase 9.

The implemented paper adapter is deterministic and process-local; see [paper broker](paper-broker.md). It is registered only in Paper mode. Backtest currently has no gateway, and Live mode remains rejected at startup.

Other ports include `IMarketDataProvider`, `IGlobalMarketProvider`, `IInstitutionalFlowProvider`, `IEconomicCalendarProvider`, `INewsContextProvider`, `IExchangeCalendar`, `IClock`, `ITradingStrategy`, `IRiskEngine`, `IOrderExecution`, `IPositionReconciler`, `ISecretStore`, and `IAuditSink`.

## 5. Mode isolation

Mode is an execution boundary:

| Boundary | Backtest | Paper | Live |
|---|---|---|---|
| Configuration scope | Backtest-only record | Paper-only record | Live-only protected record |
| Data source | Historical/replay | Live/replay | Validated live |
| Broker registration | Backtest gateway | Paper gateway | Groww gateway |
| Network order command | Structurally absent | Structurally absent | Capability + activation required |
| Database session | Mode-stamped | Mode-stamped | Mode-stamped and activation-linked |

Live activation requires server-side role, recent re-authentication, explicit acknowledgement, readiness checklist, no open mismatch, healthy broker/feed/database, kill switch clear, allowed host/environment, and an expiring activation lease. It cannot be activated by an Angular-only state change.

## 6. Critical flows

### Signal to order

1. A validated, point-in-time snapshot is frozen.
2. A versioned strategy emits an immutable signal with expiry and deterministic fingerprint.
3. Deduplication and session/window validation run.
4. Risk evaluates current persisted risk, broker/feed health, exposure, spread/slippage bounds, and configuration version.
5. Rejection is stored; approval creates an order intent transactionally.
6. Execution claims the intent idempotently and submits once.
7. Response/status updates append lifecycle events and update projections.
8. Unknown outcome becomes `ReconciliationRequired`, never an automatic resubmit.

### Restart recovery

Start in `TradingSuspended`; validate database and configuration; acquire a singleton lease; load unresolved intents/orders/positions; query broker state; reconcile; restore subscriptions; warm required data; and enable new entries only when every required invariant passes.

### End of day

Stop entries at cutoff, initiate controlled exits, poll/update fills, reconcile, raise critical exceptions for unresolved exposure, freeze daily risk, and produce an immutable report. Host shutdown is not considered a successful exit.

## 7. Persistence model

Initial PostgreSQL entities cover users, settings, broker-connection metadata (no plaintext secret), instruments, candles/selected aggregates, option-chain and external snapshots, regime snapshots, strategies/versions/configuration, signals, risk decisions, orders/events, trades, positions/events, daily risk, sessions, provider health, system events, and audit logs.

Guidelines:

- `timestamptz` in UTC; exchange-local conversion at boundaries.
- Optimistic concurrency tokens on mutable projections.
- Append-only lifecycle/audit tables; no destructive correction.
- Unique constraints for signal fingerprint, client order reference, broker IDs, and event identities.
- Transactional outbox for durable internal notifications/SignalR projections.
- Range partitioning for high-volume time series; retention jobs are audited.
- Decimal precision is explicit and instrument tick/lot metadata is validated.

## 8. Order state model

Allowed states begin with `Created → ValidationPending` and then either `RejectedByRisk` or `ReadyToSubmit → Submitted`. Broker evidence can yield `BrokerAcknowledged`, `PartiallyFilled`, `Filled`, rejection/cancellation states, exit states, `Closed`, or `ReconciliationRequired`. `Failed` is terminal only when absence of broker-side execution is proven.

Transitions are centralized domain operations. Each transition records previous/new state, trigger, actor/provider, broker evidence, timestamp, sequence, correlation, and reason. A transition matrix and exhaustive unit tests are Phase 3/8 deliverables.

## 9. Resilience and observability

- Typed `HttpClient` with timeouts and Polly resilience pipelines.
- Retry GET/read operations only when idempotent and within rate budgets.
- Do not retry order POST after timeout/connection loss until queried by unique client reference.
- Circuit breakers suspend dependent trading; they do not hide failures.
- Structured Serilog events use correlation/session/signal/order IDs and redact sensitive fields.
- Health checks distinguish liveness, readiness, and `trading-readiness`.
- Metrics include broker/feed latency, staleness, evaluation duration, submission duration, rejections, mismatches, worker/database failure, and alert acknowledgement.

## 10. Scheduling and concurrency

The initial worker uses `BackgroundService`, cancellation tokens, bounded channels, and database-backed singleton leases. `TimeProvider`/`IClock` makes schedules testable. `Asia/Kolkata` is loaded as an explicit time-zone dependency; UTC remains storage time. Only one active trading coordinator may own a live account/session.

## 11. Deployment

Development uses Docker Compose for PostgreSQL and application dependencies. Production initially runs as a Windows Service or supervised local process; Angular static assets may be hosted by ASP.NET Core. Startup recovery must complete before trading readiness. Backups, restore drills, log retention, TLS termination, least-privilege service identity, and emergency shutdown are deployment-gate requirements.

## 12. Technology decisions pending

- PostgreSQL EF provider compatibility with the chosen .NET/EF patch must be confirmed before scaffolding.
- Whether Api and Worker run in one or two processes initially; one process simplifies singleton ownership, two isolates HTTP from processing.
- Exact market calendar source and licensing.
- Time-series partitioning extension versus native PostgreSQL partitions.
- Feed transport implementation in .NET after protocol/terms confirmation.

See [ADR-001](decisions/ADR-001-modular-monolith.md), the [database model](database-model.md), and the [Groww capability matrix](groww-api-capability-matrix.md).
