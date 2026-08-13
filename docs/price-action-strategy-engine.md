# Price-action strategy engine

## Scope

The paper engine evaluates completed five-minute Nifty candles and routes an approved directional
signal to a Nifty call or put. It never submits a live broker order. A maximum of three entries per
session, one open position, shared cooldowns, data freshness, reconciliation and the paper kill
switch remain authoritative.

## Deterministic setup portfolio

The portfolio contains opening-range breakout, opening-range breakout/retest, VWAP trend pullback,
EMA 9/21 pullback continuation, intraday range breakout/retest, VWAP rejection/reversal and momentum
range expansion. Every module produces an immutable signal or explicit failed conditions. Volume is
a confidence input rather than a universal veto for price-action setups.

Market structure is calculated from recent candles using higher-high/higher-low,
lower-high/lower-low and directional-close evidence. If multiple modules qualify on the same candle,
the engine keeps the highest-confidence signal per direction and then selects the strongest overall
candidate. Shared cooldown, one-open-position enforcement and a two-entry-per-strategy daily
allocation suppress correlated duplicate entries.

## Option routing and expiry

Bullish Nifty signals buy calls; bearish signals buy puts. The nearest non-expired weekly contract is
used. Normal sessions target ATM. On expiry day the selector targets one strike ITM, reduces the
configured five-lot ceiling to two lots and tightens a configured 10% premium stop to 8%. These are
conservative deterministic defaults and are configurable. Quote validation, spread, volume and open
interest controls remain available; permissive paper mode may use a valid LTP when depth is absent.

## Position management

The existing premium lifecycle performs partial profit at 1R, moves protection to breakeven, trails
after further favourable movement, and exits at stop, target, kill switch or the intraday cutoff.
Restart recovery reconstructs the position from the durable paper journal.

While an option position is open, the engine also evaluates completed Nifty candles for invalidation
of the original directional thesis. It waits for at least two completed candles after entry and then
requires three of four independent reversal facts: two closes beyond EMA 9, an EMA 9/21 cross,
opposite higher-high/lower-low structure with at least 55% strength, and a close through the recent
swing. A confirmed invalidation closes the option using the current executable paper price and stores
the evidence in the audit log. One adverse candle, stale Nifty data or incomplete history cannot
trigger this exit. Premium stop, target, emergency and time exits retain priority.

## Research

All evaluated signals and periodic no-trade explanations are persisted. Closed paper trades include
cost-adjusted P&L. The administrator performance report groups outcomes by strategy, market regime,
IST time bucket and days to expiry. These statistics are observational; the engine never changes its
own parameters or claims profitability.

## Known limitations

- Structure uses candle-derived pivots, not order-flow or full depth.
- Contract selection ranks expiry and strike before requesting the selected contract quote; it does
  not fan out quote calls across the entire option chain.
- Paper fills are simulations and cannot establish achievable live slippage.
- Holiday/calendar handling and out-of-sample replay remain mandatory promotion gates.
