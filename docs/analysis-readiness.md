# Paper-trading analysis readiness

The research dataset must support evidence-based review without changing live strategy behaviour.

## Captured for every market

- Immutable entry decision: market, instrument, strategy, direction, confidence, reasons, entry,
  initial stop, objective, quantity and decision timestamp.
- Exact exit record: exit price, reason, gross P&L, estimated costs, net P&L and timestamp.
- One-minute active-position price samples from which maximum favourable excursion (MFE), maximum
  adverse excursion (MAE), time in profit and time in drawdown can be derived.
- Every trailing-stop adjustment with previous stop, new stop, current price and timestamp.
- Post-exit prices and hypothetical P&L at 5, 15, 30 and 60 minutes when the market and quote
  remain available.
- Rejected/no-trade evaluations remain stored separately from position telemetry.

## Existing market-specific context

- Nifty stores strategy evaluations, regime, relative futures volume, option pricing, charges,
  price samples and post-exit observations.
- Sensex stores its market decision audit and option execution lifecycle.
- Natural Gas Mini stores directional futures decisions, structural stop/objective, fixed quantity,
  costs and the same position telemetry as Sensex.

## Known limitations

- Post-exit checkpoints cannot be captured after the market closes or when Groww quotes are
  unavailable; absence must be treated as missing data, never as a zero return.
- One-minute sampling approximates intraminute MFE/MAE. Exact tick-level excursion is deliberately
  not retained because of storage and retention cost.
- External news, weather and macro context are not yet part of the deterministic dataset.

## Review gate

Before changing a strategy, require a meaningful sample and compare net results after charges,
MFE/MAE, entry delay, exit efficiency, regime, time bucket and days to expiry. No profitability
claim may be based only on win rate or a small number of sessions.
