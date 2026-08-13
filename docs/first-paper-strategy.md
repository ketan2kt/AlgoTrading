# Phase 7 First Paper Strategy

## Strategy

The paper strategy portfolio contains Opening Range Breakout, Opening Range Retest, and VWAP Trend Pullback, each versioned `1.0.0`. All strategies evaluate the same validated, completed-candle context and generate immutable proposals without broker access. The portfolio deterministically selects the highest-confidence qualifying signal for a candle.

Opening Range Breakout requires a buffered range break with matching regime direction. Opening Range Retest requires a prior break, a hold at the broken boundary, and a directional confirmation candle aligned with VWAP and EMA structure. VWAP Trend Pullback requires an established EMA trend, a pullback into the VWAP tolerance area, and a continuation candle. Futures relative volume contributes confirmation and confidence but is not a universal veto for the two price-action setups.

The proposal contains entry, stop, target, reward-to-risk, confidence, supporting and invalidating reasons, source timestamp, and expiry. The strategy has no broker dependency and does not choose final quantity.

## Preliminary risk gate

The Phase 7 risk gate owns quantity and checks signal expiry, kill switch, broker/data health, daily trade/loss limits, open-position count, minimum reward-to-risk, per-trade risk, quantity cap, and capital exposure. Any rejection produces quantity zero and the paper broker is not called.

Defaults retain a maximum of three system trades per day, one open position, a shared 20-minute price-action cooldown, minimum 55% price-action score, ₹5,000 maximum paper risk per trade, five option lots, ₹200,000 exposure, and minimum 1.8 reward-to-risk. The portfolio never forces a minimum trade count.

## Paper lifecycle

`PaperTradeLifecycleService` evaluates strategy, obtains risk approval, submits an idempotent paper entry, handles deterministic partial fills, submits the opposing exit, confirms the simulated position is closed, and produces a realised-P&L report. This orchestration is Paper-mode-only.

## Gate limitation

Paper broker orders and positions survive process restarts through deterministic append-only journal replay. Signals and risk decisions are persisted before broker submission, and a completed paper report is appended to the audit log. Cross-store atomicity and recovery between lifecycle writes are not yet proven, and realistic fees and slippage are not modeled. Therefore the functional lifecycle and broker restart-recovery criteria are proven, but the full promotion gate remains open. Do not promote this strategy to shadow or live operation.

No profitability claim is made. Historical, out-of-sample, fee/slippage, and paper-soak evidence do not yet exist.
