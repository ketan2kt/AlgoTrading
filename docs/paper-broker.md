# Phase 3 Paper Broker

## Scope

Phase 3 defines `IBrokerGateway` in the application layer and implements a deterministic `PaperBrokerGateway`. It has no HTTP client, authentication, secrets, Groww model, market feed, or live-order route.

The sandbox proves a deterministic order and position lifecycle before any external broker adapter exists:

1. A caller submits a validated request with a unique client reference, instrument, side, quantity, deterministic execution price, and maximum fill quantity per cycle.
2. The gateway acknowledges it with a monotonic `PAPER-##########` identifier.
3. Repeating the identical request returns the existing snapshot. Reusing the reference with different content throws `DuplicateBrokerOrderException`.
4. `IPaperBrokerControl` advances fills explicitly. A bounded fill size makes partial fills repeatable.
5. Filled quantities update a simulated position. Opposing fills offset and can close that position.
6. An acknowledged or partially filled order can cancel its unfilled remainder.
7. Reconciliation compares expected and simulated positions. Any mismatch sets `TradingPermitted` to false.

## Mode boundary

The dependency-injection composition exposes this gateway only when the server-authoritative mode is `Paper`. Backtest mode has no gateway in Phase 3 and fails resolution. Live mode is rejected during startup validation. `IPaperBrokerControl` is a replay/test control surface and must not be exposed through an API controller.

## Durability and recovery

The paper broker uses no randomness, wall-clock delay, network request, or background race. Fill progression occurs only when explicitly advanced. Execution price is supplied by a trusted future replay/market-data orchestrator; the gateway validates it but does not invent market prices.

Submissions, partial fills, completed fills, and cancellations are written to an append-only PostgreSQL journal before the in-memory projection changes. Startup replays that journal in strict sequence to reconstruct orders, idempotency keys, broker-order numbering, and open positions. A missing, duplicate, unknown, or inconsistent event fails recovery and prevents normal startup. A journal write failure leaves the in-memory projection unchanged.

The current Azure topology is deliberately one App Service instance. Cross-process journal writers, realistic spread/slippage/fees, broker-unknown outcomes, and crash testing at every persistence boundary remain Phase 8 hardening work. Strategy signals, risk decisions, and final lifecycle reports still require their complete durable audit chain before shadow-mode promotion.

## Manual verification

```powershell
dotnet test tests/TradingSystem.UnitTests --configuration Release `
  --filter "FullyQualifiedName~PaperBrokerGatewayTests|FullyQualifiedName~TradingOrderStateTests"
```

Expected coverage includes identical duplicate submission, conflicting duplicate rejection, partial and complete fills, cancellation, entry-to-exit closure, mismatch blocking, restart reconstruction, persistence failure, and Backtest-mode isolation.
