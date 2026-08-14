# Risk Controls

## 1. Authority and philosophy

The risk engine has veto authority over every strategy and execution request. Risk checks are deterministic, versioned, persisted with their exact input snapshot, and repeated immediately before submission when market/account state can change.

Defaults are fail-closed. Numeric defaults that depend on account capital, instrument lot size, or broker rules are deliberately not invented in Phase 0.

## 2. Layered controls

### Environment and activation

- Live gateway absent before Phase 9 and live disabled by default thereafter.
- Server-authoritative mode, account binding, activation lease, allowed host, and readiness checks.
- Emergency kill switch blocks entries immediately and starts the configured controlled-exit policy.

### Session

- Exchange calendar plus manual override.
- Entry window, no-trade periods, new-entry cutoff, forced-exit time, delayed-start policy.
- UTC storage with explicit `Asia/Kolkata` session calculations.
- Unexpected closure or missing calendar certainty suspends entries.

### Data quality

- Required source availability, validation, freshness threshold, source timestamp, receive timestamp, sequence/order, duplicate detection, gap detection, and clock skew.
- Cross-check index/derivative observations where configured.
- Required snapshot is frozen for reproducibility.
- Optional provider failure reduces confidence or blocks only when policy says it is required.

### Broker/system health

- Authentication and entitlement, connectivity, rate-budget headroom, database readiness, worker lease, clock health, unresolved order count, and reconciliation status.
- Local/broker mismatch, unknown submission outcome, or stale account state blocks new entries.

### Signal and strategy

- Immutable ID/fingerprint, version, expiry, trading window, permitted instrument/regime, confidence, cooldown, daily strategy count, and re-entry policy.
- Duplicate, expired, unsupported, or contradicted signals are rejected with reasons.

### Trade and portfolio

- Maximum risk per trade, quantity, capital exposure, open positions, spread, expected slippage, minimum reward-to-risk, and instrument/strategy limits.
- Daily realised plus defined unrealised-loss treatment, consecutive losses, attempt/trade count, cooldown after loss/rejection, and correlated exposure.
- Final quantity is calculated by risk using conservative executable prices, lot size, tick size, fees/slippage buffer, and stop distance.

## 3. Proposed initial policy

| Control | Initial policy |
|---|---|
| Mode | Backtest/paper only; live capability absent |
| Max entries/attempts per day | 3 aggregate; never a target |
| Max open positions | 1 |
| Concurrent signals | Serialize approvals against locked daily-risk state |
| Minimum reward:risk | Configurable; value requires strategy evidence and approval |
| Risk per trade | Disabled until approved capital base and loss budget are configured |
| Max daily loss | Mandatory before paper/live; value requires user approval |
| Quantity | Must respect lot/freeze limits and min of all exposure/risk caps |
| Entry cutoff / forced exit | Mandatory before paper/live; exact times require broker/exchange validation |
| Staleness | Per data type/timeframe; no universal fabricated value |
| Unknown broker outcome | Suspend instrument/account submissions and reconcile |
| Position mismatch | Suspend all new entries; critical alert |
| Kill switch | Persisted, server-side, role-protected; entries blocked fail-closed |

An unset mandatory monetary/time control means `TradingPermitted = false`, not “unlimited.”

## 4. Risk decision contract

Each immutable decision includes:

- Decision ID, signal ID/fingerprint, mode, session, account binding
- Approved/rejected and machine-readable/human-readable reasons
- Requested and approved quantity
- Final entry assumptions, stop, target, spread and slippage allowance
- Capital/exposure/daily-risk before and projected after
- Open positions, daily counts, consecutive losses and cooldowns
- Provider/broker/reconciliation/kill-switch health
- Data timestamps, freshness and quality
- Configuration/strategy/risk-policy versions
- UTC decision time and correlation ID

## 5. Position sizing outline

For directional positions, the risk budget is the minimum of remaining per-trade, daily, strategy, instrument, and capital limits. Conservative loss per unit includes stop distance, adverse slippage, fees/taxes, and gap/market-order buffer where applicable. Quantity is floored to valid lots and capped by exposure, broker/exchange freeze, available margin, liquidity, and configured maximum.

If stop distance is zero/invalid, fees cannot be estimated, market spread exceeds policy, available margin is stale, or calculated quantity is below one valid lot, reject.

Options require premium/exposure rules specific to long versus short positions. Naked short options are outside the initial scope unless separately risk-approved with margin and tail-risk controls.

## 6. Kill switch semantics

States: `Clear`, `EntriesBlocked`, `ExitRequested`, `ExitInProgress`, `ReconciliationRequired`, `Safe`. Activation is idempotent and immediately blocks new intents. Exit requests use current reconciled positions and do not assume a cancelled/failed response means flat. Clearing requires Administrator authority, recent authentication, reason, health checks, and reconciliation.

Machine/process shutdown is not the kill switch because it can leave broker positions unmanaged.

## 7. Breach handling

- Pre-trade breach: reject and audit.
- Intraday soft threshold: block entries, alert, continue managing positions.
- Hard loss/health threshold: block entries and invoke approved exit policy.
- Reconciliation/unknown-order breach: freeze new activity, query broker, escalate.
- Data breach: stop affected strategy/instrument; global stop if the data is portfolio-critical.
- Worker/database failure: no new submission; recovery begins suspended.

## 8. Required tests

Boundary and property tests for sizing/rounding; daily loss and trade-count concurrency; cooldowns; stale/out-of-sequence data; clock/session boundaries; spread/slippage; missing config; mode and activation; kill switch; partial fills; unknown outcomes; restart; mismatches; forced exit; and audit completeness.

No automated test may resolve or call a live execution gateway.

## 9. Paper trend-reversal protection

Trend-reversal exits are session-behaviour aware. The engine classifies the completed session as
trending, range-bound, or zigzag before acting on recent reversal evidence. Every market exit requires
three persistent closes on the adverse side of EMA 9 and a confirmed swing break. Range-bound and
zigzag sessions additionally require all four evidence categories, including opposite market
structure; correlated EMA noise alone is insufficient.

When a confirmed reversal occurs while the option position is profitable, the paper engine protects
80% of the current premium gain with a tightened stop rather than immediately submitting a market
exit. A losing position may still exit on the fully confirmed reversal. Each reversal exit is followed
up at 15, 30, and 60 minutes when valid quotes are available; observed premium and counterfactual P&L
are persisted for research. Follow-up collection is optional and cannot block trading.

## 10. Open risk decisions

The owner must approve before paper trading: capital base definition, per-trade and daily loss budgets, whether unrealised loss counts toward the daily stop, eligible instruments, long/short option scope, max premium/margin exposure, minimum reward:risk, liquidity/spread/slippage limits, cutoff/exit times, fee model, cooldowns, and emergency liquidation style.
