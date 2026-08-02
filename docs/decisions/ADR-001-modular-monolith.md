# ADR-001: Start as a Modular Monolith

- Status: Proposed
- Date: 2026-07-30
- Decision owners: Project owner and principal architect
- Gate: Accept before Phase 1 scaffolding

## Context

The product combines market ingestion, deterministic analysis, risk, execution, reconciliation, configuration, and audit. It initially runs on one Windows PC/local server and must prioritise correctness and recoverability over independent scaling. Premature process distribution would add network failure modes, distributed transactions, deployment complexity, clock/ordering ambiguity, and operational burden.

At the same time, direct coupling between strategies, Groww, persistence, and presentation would make safety controls difficult to test and could allow execution bypasses.

## Decision

Build a modular monolith using:

- `TradingSystem.Domain` for pure model and invariants
- `TradingSystem.Application` for use cases and ports
- `TradingSystem.Infrastructure` for persistence and external adapters
- `TradingSystem.Api` and `TradingSystem.Worker` as composition/hosting projects
- Separate test projects and Angular frontend

Logical modules own contracts and persistence boundaries. Dependencies point inward. Strategies emit signals only; risk and execution retain exclusive authority to create broker commands. Durable application events use a transactional outbox where asynchronous projection is required.

API and worker may initially share one deployment process or be separately hosted from the same monolith after a Phase 1 decision. That choice does not change module boundaries.

## Consequences

### Positive

- One deployable system and database simplify local operation, transactions, recovery, backup, and debugging.
- Domain and application logic remain testable without Groww/PostgreSQL/UI.
- Execution authority can be structurally isolated from strategies.
- Module boundaries allow later extraction based on measured need.

### Trade-offs

- Discipline and architecture tests are required to prevent cross-module coupling.
- A single process can share failure impact; supervised restart and isolation of background loops are required.
- PostgreSQL and host capacity constrain scale, acceptable for the initial account/instrument scope.
- Independent module deployment is unavailable until an explicit extraction decision.

## Rejected alternatives

- **Microservices now:** unjustified operational and distributed-consistency cost for one host/account.
- **Single layered project:** weak compile-time boundaries and greater risk of broker/persistence coupling.
- **Serverless functions:** poor fit for persistent feeds, deterministic session ownership, and local Windows deployment.

## Boundary enforcement

- Architecture dependency tests in Phase 1.
- No Infrastructure references from Domain/Application.
- No broker gateway dependency from strategy implementations.
- Controllers/components contain no business rules.
- Module-owned schemas/table prefixes and application contracts; no opportunistic cross-module table access.
- Live gateway registration exists only in an explicitly activated live composition profile.

## Revisit triggers

Reconsider extraction only if measured needs show independent scaling, isolation, deployment cadence, regulatory boundary, or team ownership that outweighs distributed-system risk. Any extraction requires a new ADR covering data ownership, delivery guarantees, reconciliation, observability, security, and rollback.
