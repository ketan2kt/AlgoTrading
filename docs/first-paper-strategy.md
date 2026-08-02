# Phase 7 First Paper Strategy

## Strategy

The only implemented strategy is Opening Range Breakout version `1.0.0`. It evaluates a precomputed, validated context and generates an immutable proposal only when price clears the opening range plus a configurable buffer, relative volume is sufficient, regime direction confirms the break, data/regime permission is true, confidence passes, cooldown has elapsed, and its daily strategy limit is not reached.

The proposal contains entry, stop, target, reward-to-risk, confidence, supporting and invalidating reasons, source timestamp, and expiry. The strategy has no broker dependency and does not choose final quantity.

## Preliminary risk gate

The Phase 7 risk gate owns quantity and checks signal expiry, kill switch, broker/data health, daily trade/loss limits, open-position count, minimum reward-to-risk, per-trade risk, quantity cap, and capital exposure. Any rejection produces quantity zero and the paper broker is not called.

Defaults are deliberately conservative: one ORB trade per day, maximum three system trades per day, one open position, ₹500 maximum modeled risk, quantity cap 75, ₹200,000 exposure, and minimum 1.8 reward-to-risk.

## Paper lifecycle

`PaperTradeLifecycleService` evaluates strategy, obtains risk approval, submits an idempotent paper entry, handles deterministic partial fills, submits the opposing exit, confirms the simulated position is closed, and produces a realised-P&L report. This orchestration is Paper-mode-only.

## Gate limitation

Paper broker orders and positions survive process restarts through deterministic append-only journal replay. Signals and risk decisions are persisted before broker submission, and a completed paper report is appended to the audit log. Cross-store atomicity and recovery between lifecycle writes are not yet proven, and realistic fees and slippage are not modeled. Therefore the functional lifecycle and broker restart-recovery criteria are proven, but the full promotion gate remains open. Do not promote this strategy to shadow or live operation.

No profitability claim is made. Historical, out-of-sample, fee/slippage, and paper-soak evidence do not yet exist.
