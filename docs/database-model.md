# Database Model

Phase 2 uses PostgreSQL 18.4 through EF Core 10.0.10 and Npgsql EF provider 10.0.3. The initial migration is `InitialTradingSchema` under `TradingSystem.Infrastructure/Persistence/Migrations`.

## Design rules

- Application tables live in the `trading` schema.
- Identifiers are application-generated UUIDs.
- Timestamps are UTC `timestamptz`; exchange-local trading dates use `date`.
- Mutable records carry a concurrency token and created/updated timestamps.
- Signals, decisions, snapshots, lifecycle events, trades, system events, and audit records are append-only.
- JSON payloads use PostgreSQL `jsonb`; prices and money have explicit precision.
- Mode-owned records include Backtest, Paper, or Live in their unique scope.
- Unique indexes enforce signal fingerprints, client order references, broker IDs, candle identities, daily/session scope, and configuration scope.

## Table groups

| Group | Tables |
|---|---|
| Identity | `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `user_tokens`, `role_claims` |
| Configuration | `application_settings`, `broker_connections`, `strategies`, `strategy_versions`, `strategy_configurations` |
| Market data | `instruments`, `candles`, `option_chain_snapshots`, `external_market_snapshots`, `market_regime_snapshots` |
| Decision/execution | `signals`, `risk_decisions`, `orders`, `order_events`, `trades`, `positions`, `position_events` |
| Operations | `daily_risk`, `trading_sessions`, `provider_health`, `system_events`, `audit_logs` |

## Security and audit

`BrokerConnection` stores only an opaque `SecretReference`; it has no broker-secret or access-token column. ASP.NET Core Identity stores password hashes and security metadata.

Audit records contain actor, action, entity type/ID, reason, redacted before/after JSON, correlation ID, and UTC occurrence time. `TradingDbContext` rejects modification or deletion of every append-only entity before issuing SQL. Material configuration commands must write through `IAuditWriter`.

## Migration policy

- Migrations are generated in Infrastructure and reviewed as source.
- The application never applies migrations automatically at startup.
- Operators apply migrations explicitly with a secure connection string.
- Production deployment requires a database backup and tested recovery procedure.
- Destructive migrations require a separate review and rollback plan.

## Retention

Raw ticks are intentionally absent. Candles and selected snapshots are retained. Native PostgreSQL range partitioning and automated retention wait until Phase 5 establishes actual cadence and storage volume. Audit, order, trade, position, and risk evidence is excluded from ordinary time-series retention.
