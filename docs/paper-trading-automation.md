# Automated paper trading

## Scope

This Phase 7 increment joins the existing live Groww Nifty ingestion, deterministic market-regime engine, Opening Range Breakout strategy, conservative risk engine, durable paper broker, and dashboard. It never calls a Groww order endpoint. `PaperTrading:Automation:Enabled` defaults to `false`; promotion requires an explicit configuration change after review.

## Decision cycle

Every five seconds the coordinator reconstructs the current session from PostgreSQL and the append-only paper-broker journal. Before any action it reconciles the expected open position with the recovered broker position. A mismatch blocks trading.

New entries require all of the following:

- Paper mode, automation enabled, weekday session, and inactive kill switch;
- fresh validated Groww data within the configured staleness limit;
- at least 21 completed one-minute candles;
- a completed 09:15-09:30 opening range;
- a persisted previous-session close;
- positive validated volume for VWAP and relative-volume confirmation;
- a permitted deterministic market regime;
- a qualifying Opening Range Breakout signal;
- risk approval, including quantity, daily trade/loss, exposure, and reward-to-risk limits;
- live entry slippage no greater than the configured 0.10%;
- entry before 14:30 Asia/Kolkata.

The paper fill price is the latest validated live observation and entry movement beyond 0.10% is rejected. One position is allowed. An open position is monitored against its immutable stop and target and is closed at a fresh observed price when either is crossed, when the administrator activates the kill switch, or at/after 15:15. Stale data never fabricates an exit. Reported realised P&L deducts a configurable conservative round-trip turnover cost (5 basis points by default); it is an estimate, not an exact Indian tax/brokerage calculator.

## Recovery and audit

Signals and risk decisions are persisted before submission. Entry reference `{signalId}-ENTRY` and exit reference `{signalId}-EXIT` make submission idempotent. On restart, filled entries without a filled exit are treated as open and reconciled with the recovered paper broker. Closures create an append-only audit record containing prices, quantity, reason, and realised P&L.

## Dashboard

The protected Angular dashboard shows automation state, its current blocking/readiness reason, trades today, realised/unrealised P&L, active direction/quantity, and signal overlays. The administrator-only kill switch uses antiforgery protection and records each change in the audit log.

## Manual verification before deployment

1. Keep `PaperTrading__Automation__Enabled=false`; verify the dashboard reports `Disabled`.
2. Enable it only in a non-live paper environment and supply a current Groww token.
3. During an NSE session verify fresh one-minute candles accumulate and readiness reasons change deterministically.
4. Verify no entry can occur before 09:30 or after 14:30, with stale data, missing volume/previous close, or an active kill switch.
5. With a controlled replay/fake feed, produce one qualifying breakout and verify signal, risk decision, one entry fill, chart overlay, and open-position P&L.
6. Restart the process and verify the same open position is recovered without a duplicate entry.
7. Cross the stop and target separately in controlled runs; verify one idempotent exit and audited P&L.
8. Activate the kill switch with a fresh price and verify emergency exit; verify stale data blocks a fabricated exit.

## Known limitations

- Nifty cash index quote volume may be absent from the provider. The strategy intentionally remains blocked rather than substituting synthetic volume; a validated Nifty futures volume source is the likely next data improvement.
- The first persisted day cannot evaluate because a previous-session close is unavailable. Historical gap backfill is not yet part of startup.
- Exchange holidays and exceptional closures are not integrated yet.
- Simulated fills include an entry-slippage guard and configurable turnover-cost estimate, but do not model an exact broker tariff, tax schedule, queue position, bid/ask spread, or latency. Results must not be treated as profitability evidence.
- PostgreSQL integration/restart tests are checked in but remain opt-in on this workstation because Docker is unavailable.
- This increment is local only until the user explicitly approves deployment.
