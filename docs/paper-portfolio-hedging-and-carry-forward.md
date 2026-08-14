# Paper portfolio hedging and carry-forward

Status: implementation in paper mode; live execution remains disabled.

## Verified capabilities and constraints

- Groww's official annexure defines `MIS` as intraday and `NRML` as the F&O product that allows
  overnight positions. A future live implementation must create an overnight order as `NRML`; it
  must not assume that an existing MIS position can be converted.
- Groww order creation requires an explicit product, segment, transaction type, validity and unique
  order reference. No live request model is added in this phase.
- NSE's current contract specification lists four Nifty 50 weekly expiries and Tuesday as the expiry
  day, or the prior trading day when Tuesday is a holiday. The instrument master remains authoritative;
  the application does not calculate an expiry from a hard-coded weekday.

Official references:

- https://groww.in/trade-api/docs/python-sdk/annexures
- https://groww.in/trade-api/docs/curl/orders
- https://groww.in/trade-api/docs/curl/portfolio
- https://www.nseindia.com/static/products-services/equity-derivatives-contract-specifications

## Implemented safety boundary

The paper engine can reconstruct, reconcile and manage several open option positions concurrently.
Expected broker positions are aggregated by instrument and direction during reconciliation. New
signals can continue to be evaluated after existing positions have been repriced and protected.

Selective hedging uses a long option in the opposite direction. It requires all of:

1. existing opposite directional exposure;
2. no existing portfolio hedge;
3. ATR expansion at or above the configured threshold;
4. adequate relative Nifty futures volume;
5. an opposite setup above the hedge confidence threshold; and
6. a regime other than range-bound or low-volatility compression.

This avoids naked option shorts and avoids paying option decay for a permanent hedge.

Weekly carry-forward is a distinct, disabled-by-default gate. When enabled in paper mode, only a
fully-paid long option may bypass the intraday exit, and only while it is profitable, has sufficient
signal confidence, remains trend-valid, has time remaining before expiry, and the kill switch is clear.
Recovery looks back across prior sessions so a restart does not orphan that position.

## Not promoted

- Live Groww order placement is unchanged and disabled.
- Weekly carry-forward is not enabled until an overnight/restart paper soak test is reviewed.
- STBT is not represented as a naked short option. Its eventual paper implementation must be an
  atomic, defined-risk vertical spread: protective long wing first, short leg second, group-level
  reconciliation, combined maximum-loss calculation and all-or-exit recovery if either leg fails.
- Exchange holiday and early-close data must be consulted before any overnight promotion.

## Paper acceptance checks

1. Open two distinct long-option positions and confirm both are repriced and independently exited.
2. Restart with both open and confirm reconciliation matches the aggregated paper journal.
3. Generate an opposite signal in low volatility and confirm `HedgeRejected` is audited.
4. Generate an opposite signal in qualified expansion and confirm one opposite option hedge opens.
5. Confirm a second hedge is rejected while the first remains open.
6. Enable weekly carry only in a controlled paper environment; confirm an eligible position survives
   15:15, is recovered next session, and exits before expiry or on invalidation.
