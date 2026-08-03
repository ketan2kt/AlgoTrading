# Automated paper trading

## Scope

This Phase 7 increment joins the existing live Groww Nifty ingestion, deterministic market-regime engine, Opening Range Breakout strategy, conservative risk engine, durable paper broker, and dashboard. It never calls a Groww order endpoint. `PaperTrading:Automation:Enabled` defaults to `false`; promotion requires an explicit configuration change after review.

## Decision cycle

Every five seconds the coordinator reconstructs the current session from PostgreSQL and the append-only paper-broker journal. Before any action it reconciles the expected open position with the recovered broker position. A mismatch blocks trading.

New entries require all of the following:

- Paper mode, automation enabled, weekday session, and inactive kill switch;
- fresh validated Groww data within the configured staleness limit;
- at least 21 completed Nifty spot one-minute candles;
- at least 21 aligned nearest-expiry Nifty futures candles for relative-volume confirmation;
- a completed 09:15-09:30 opening range;
- a persisted previous-session close;
- positive validated futures volume for relative-volume confirmation;
- a permitted deterministic market regime;
- a qualifying Opening Range Breakout signal;
- a deterministically selected near-expiry ATM Nifty call for bullish bias or put for bearish bias;
- a live option quote with positive, non-inverted bid/offer, permitted spread and premium, and minimum volume/open interest;
- risk approval, including quantity, daily trade/loss, exposure, and reward-to-risk limits;
- a risk-approved quantity rounded down to complete option lots;
- entry before 14:30 Asia/Kolkata.

Nifty remains the signal and chart instrument; it is never submitted as the paper order instrument. At session startup the system imports up to seven days of official Groww one-minute spot and nearest-future history, then continues polling both live quotes. Spot OHLC drives strategy price levels while futures volume supplies relative-volume confirmation. The paper option entry fills at the validated offer. One long option position is allowed. Its immutable premium stop and target are monitored against the live bid and it closes at that liquidation reference when either is crossed, when the administrator activates the kill switch, or at/after 15:15. A missing bid never fabricates an exit.

## Recovery and audit

Underlying signals and option-premium risk decisions are persisted before submission. Entry reference `{signalId}-ENTRY` and exit reference `{signalId}-EXIT` make submission idempotent. On restart, filled entries without a filled exit recover the actual option instrument and protective prices and are reconciled with the recovered paper broker. Closures create an append-only audit record containing prices, quantity, reason, and realised P&L.

## Dashboard

The protected Angular dashboard shows automation state, trades today, realised/unrealised P&L, active direction/quantity, signal overlays, and an explicit readiness grid for spot candles, previous-session context, futures confirmation, freshness, entry window, and risk controls. The administrator-only kill switch uses antiforgery protection and records each change in the audit log.

## Manual verification before deployment

1. Keep `PaperTrading__Automation__Enabled=false`; verify the dashboard reports `Disabled`.
2. Enable it only in a non-live paper environment and supply a current Groww token.
3. During an NSE session verify fresh one-minute candles accumulate and readiness reasons change deterministically.
4. Verify no entry can occur before 09:30 or after 14:30, with stale data, missing volume/previous close, or an active kill switch.
5. With a controlled replay/fake feed, produce bullish and bearish breakouts and verify they map to CE and PE respectively; verify the Nifty chart overlay remains at the underlying signal level.
6. Verify a wide spread, excessive premium, insufficient volume/open interest, and any quantity smaller than one lot all reject the entry.
7. Verify one option entry fills at the offer and its open-position P&L is marked against the bid.
8. Restart the process and verify the same option position and premium SL/target are recovered without a duplicate entry.
9. Cross the option-premium stop and target separately in controlled runs; verify one idempotent exit and audited P&L.
10. Activate the kill switch with a valid bid and verify emergency exit; verify a missing bid blocks a fabricated exit.

## Known limitations

- Historical bootstrap depends on a current Groww token and a successfully synchronised Groww symbol for both Nifty spot and the nearest future. Failure remains visible and blocks entry.
- Exchange holidays and exceptional closures are not integrated yet.
- Simulated fills cross the visible option spread (offer entry/bid exit) and include a configurable turnover-cost estimate, but do not model an exact broker tariff, tax schedule, queue position, changing quote during submission, or latency. Results must not be treated as profitability evidence.
- PostgreSQL integration/restart tests are checked in but remain opt-in on this workstation because Docker is unavailable.
- This increment is local only until the user explicitly approves deployment.
