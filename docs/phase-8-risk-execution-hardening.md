# Phase 8 — Risk and execution hardening

## Status

The Phase 8 engineering scope is implemented in paper mode. Promotion remains blocked until the
manual multi-session soak and the opt-in PostgreSQL restart test are completed and reviewed. No
Groww order endpoint exists in this release.

Paper-environment note (20 August 2026): daily-loss enforcement is temporarily disabled through
`PaperTrading:Automation:EnforceDailyLossLimit=false` for controlled paper research. The ₹5,000
threshold, policy, audited administrator override, reporting and tests remain intact. Re-enabling
the switch restores enforcement without a code change. This exception is prohibited in live mode.

## Safety invariants

- Every paper entry is uniquely correlated by immutable signal ID and client reference.
- An identical active option instrument and direction cannot be entered twice. The suppressed
  opportunity is retained as `DuplicateActiveContract` research evidence.
- Permissive paper strategy evaluation cannot bypass mandatory portfolio risk. Quantity is floored
  to complete lots and constrained by ₹5,000 risk per trade, five lots, ₹200,000 aggregate premium
  exposure, eight distinct open positions, daily loss including open losses, fresh data,
  reconciliation, and the kill switch.
- A quote failure isolates the affected position, continues monitoring other positions, blocks new
  entries, and never fabricates a stop/target fill.
- Entry uses the offer-derived simulation price; protective monitoring and exits use the executable
  bid when available. Dashboard mark-to-market uses LTP and is tracked independently per position.
- Journal submission is append-before-mutate. A database failure with an uncertain commit outcome
  invalidates the in-memory projection and forces authoritative journal replay before another
  broker operation. The same client reference is never blindly resubmitted with changed content.
- Startup and every automation cycle reconstruct unresolved entries/exits, reconcile expected and
  paper-broker positions, and block on any mismatch.
- Kill-switch activation blocks entries and requests controlled exits using reconciled positions.
  Missing executable quotes leave positions open and visibly unmonitored rather than pretending
  they were closed.

## Execution-state and failure coverage

Automated coverage includes domain order-state transitions, idempotent submission, conflicting
duplicate rejection, bounded partial fills, partial-fill cancellation, entry/exit netting,
reconciliation mismatch, restart continuation from a partial fill, restart idempotency and order
sequence, append failure before commit, unknown commit outcome followed by replay, corrupt journal
sequence rejection, stale/out-of-order market data, forced intraday exit, stop/target priority,
kill-switch rejection, duplicate active-contract suppression, portfolio exposure sizing, and daily
loss including unrealised losses.

## Dashboard evidence

The authenticated dashboard exposes open-position count, aggregate premium capital exposure,
aggregate risk at current protective stops, daily loss consumed versus the configured limit,
per-position current premium/P&L, quote-unavailable alerts, reconciliation alerts, order/trade
outcomes, and the audited emergency kill switch. Daily reports retain P&L, drawdown, strategy,
regime, time bucket, days-to-expiry, and exit reason.

## Manual promotion verification

1. Run at least five complete weekday paper sessions, including one App Service restart with an
   active position and one controlled quote outage.
2. Confirm no identical contract/direction pair is concurrently active.
3. Confirm approved quantity never exceeds whole-lot risk/exposure capacity.
4. Activate the kill switch with multiple positions; verify valid quotes close and a missing quote
   remains visibly unresolved until it can be reconciled.
5. Restart during a partial-fill simulation and confirm the same client reference resumes without
   an additional submission event.
6. Enable `RUN_POSTGRES_TESTS=true` in an environment with PostgreSQL/Docker and run the durable
   migration, token-recovery, and broker-restart integration tests.
7. Export the 30-day paper report and review P&L, costs, drawdown, exit reasons, strategy/regime
   breakdown, quote incidents, and reconciliation incidents.

## Known limitations and risk review

- Paper fills do not model queue position, exchange latency, exact taxes/fees, gaps through a stop,
  or a changing quote during submission. Results are not evidence of live profitability.
- Cross-database atomicity between strategy/risk persistence and the broker journal is recovered by
  immutable IDs and replay, not a distributed transaction.
- The current Azure deployment intentionally uses one App Service instance. Multi-instance worker
  leadership is not implemented; scale-out is prohibited for the paper automation worker.
- PostgreSQL restart tests remain opt-in on this workstation because Docker is unavailable.
- Weekly carry remains disabled. Live mode remains disabled and Phase 9 is not authorised.

## Security review

No credentials, tokens, broker account data, or new anonymous endpoints were added. Portfolio risk
telemetry is returned only through the existing administrator-authorised workspace API. Structured
errors identify instruments/correlation IDs but never include the Groww access token.
