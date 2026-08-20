# Phase 8.5 — Strategy intelligence and validation

## Status

Engineering implementation completed on 17 August 2026. Deployment and paper-observation evidence remain promotion gates.

This phase adds an advisory research layer. It does not change strategy parameters, place live orders, or claim profitability.

## Implemented capability

- One option-premium sample is persisted per active paper position per UTC minute.
- Each closed trade is analysed for maximum favourable excursion (MFE), maximum adverse excursion (MAE), profit giveback, captured-profit ratio and price-path sample count.
- Existing 15/30/60-minute follow-ups identify trades whose option premium materially improved after an exit.
- Exit assessments distinguish insufficient evidence, potential early exits, material profit giveback, stops supported by follow-up, and exits within the observed range.
- Expectancy and profit factor are reported by strategy, regime, IST time bucket and expiry distance.
- The decision funnel aggregates signals, rejections, confidence, futures volume and leading failed conditions.
- Recommendations are deterministic and advisory. A minimum of 20 closed trades is required before a recommendation can become eligible for a controlled replay experiment.
- The authenticated paper report exposes the new evidence without changing the chart-first dashboard layout.
- A fast-reacting market-structure quality analyzer runs in shadow mode. It measures trend efficiency, close-direction flips, VWAP recrossing, swing consistency, move maturity in ATR and known room-to-risk before nearby structure.
- The shadow layer classifies `CleanTrend`, `DevelopingTrend`, `MatureTrend`, `StructuredRange`, `NoisyChop` and `VolatilityTransition`. Its permit/reject verdict is persisted beside the actual decision but cannot block a paper entry.
- Paper reports compare realised results for trades the shadow layer would have permitted versus rejected. This creates counterfactual evidence before any filter is promoted.

## Shadow market-structure policy

The shadow layer vetoes a candidate when observed evidence indicates noisy low-efficiency chop, a continuation move that has already travelled at least three ATR, conflict with a high-quality directional path, or less than 1.10R of known room before nearby structure. Unknown room is reported as unknown rather than invented.

These thresholds are hypotheses, not profitable rules. Their realised results must be reviewed by
net expectancy and drawdown after charges across varied regimes; enforcement is limited to paper
mode and requires explicit review before any later live-mode use.

## Entry-quality enforcement update (2026-08-20)

Following repeated late CE/PE entries in choppy sessions, the previously recorded structure-quality
verdict now blocks new paper entries when the path is noisy, the directional move is mature, the
candidate conflicts with short-term structure, or the state is an ambiguous range/volatility
transition. A clean or developing aligned trend is required. VWAP reversal additionally requires
confirmed directional structure and matching regime bias. Opening-range breakout now enforces its
configured 0.75 relative-futures-volume minimum instead of treating it as informational.

Option open interest remains a liquidity validation input only. A single contract's OI does not
identify whether positions were opened by buyers or sellers; directional OI confirmation requires
time-aligned option-chain snapshots and change-in-OI analysis and is not currently used as an entry
signal.

## Production evidence correction (2026-08-20)

The authenticated paper report contained 57 closed trades with net expectancy of -₹638.60 and a
profit factor of 0.47 after costs. The dominant failure was repeated EMA-pullback entries during
`LowVolatilityCompression`: 10 trades lost ₹20,392.19 on 19 August and 15 trades were approximately
flat on 18 August despite substantial churn. Across all trades, average MAE (₹1,212.65) materially
exceeded average MFE (₹656.21), and average profit giveback was ₹1,246.75. This is evidence of weak
entry selection and overtrading, not evidence that targets alone are wrong.

The paper engine therefore now applies these deterministic safeguards:

- `LowVolatilityCompression` and `RangeBound` cannot authorize a directional entry.
- Weak trends require price to agree with VWAP; volatility expansion requires a directional bias.
- Every price-action strategy respects the regime permission flag.
- EMA continuation, range breakout and momentum expansion require matching regime bias.
- At least two independent price-action strategies must agree on direction; conflict blocks entry.
- Paper research is limited to four entries per session, pauses after two consecutive net losses,
  and observes a 30-minute cooldown after a net loss.
- All vetoes are persisted as `EntryQualityRejected` or `AdaptiveRiskRejected` for reporting.

These controls are paper-mode research safeguards. They do not demonstrate profitability and must
be validated on new out-of-sample sessions before Phase 9.
- Every executed paper option order, including a partial exit, is charged using the versioned `GROWW-NSE-OPTIONS-2026-04-01` schedule. Net P&L deducts brokerage, STT, NSE transaction charges, NSE IPFT, SEBI turnover fees, GST and buy-side stamp duty.

## Paper cost schedule

The schedule is based on Groww's published equity-option pricing and the NSE STT schedule effective 1 April 2026:

- Groww brokerage: ₹20 per executed F&O order.
- STT: 0.15% of sell-side option premium turnover.
- NSE option transaction charge: 0.03503% of premium turnover on both sides.
- NSE IPFT: 0.0005% of premium turnover on both sides.
- SEBI turnover fee: 0.0001% on both sides.
- GST: 18% of brokerage, exchange, IPFT and SEBI charges.
- Stamp duty: 0.003% of buy-side premium turnover.

Sources: [Groww F&O pricing](https://groww.in/pricing/futures-and-options), [NSE STT](https://www.nseindia.com/static/products-services/equity-derivatives-securities-transaction-tax), and [NSE transaction-charge circular](https://nsearchives.nseindia.com/content/circulars/FA73061.pdf).

The model rounds displayed components and the total to paise. Actual broker contract notes remain authoritative and can differ because of broker/exchange rounding, regulatory changes, exercise STT, penalties or physical-settlement charges. The schedule must be reviewed whenever published rates change.

## Interpretation boundaries

MFE and MAE use observed executable option-premium samples, not an assumed continuous price path. A rapid movement between samples may therefore be missed. Historical trades created before this release will show insufficient price-path evidence.

The shadow analyzer uses completed five-minute candles and the current session VWAP. It is faster and richer than the legacy two-half structure label, but it cannot see order-book behaviour between candles and must not be treated as a forecast.

Post-exit analysis currently exists for trend-invalidation exits because those follow-up quotes were already captured. Extending it to every exit reason requires a separate bounded quote-retention decision.

The decision funnel explains why trades were not taken but does not label a rejected setup as a missed profitable trade. That conclusion requires replay using historical option prices, spread, slippage and costs.

## Promotion criteria

Before any Phase 9 order integration:

1. Collect at least 20 closed trades and preferably 50 or more across trend, range and volatile sessions.
2. Verify price-sample coverage for active trades and investigate gaps.
3. Run candidate changes through replay and an out-of-sample period.
4. Include brokerage, taxes, spread and slippage.
5. Compare expectancy, profit factor, maximum drawdown and stability against the unchanged baseline.
6. Promote one rule change at a time through explicit approval.

## Security and risk review

- Research endpoints remain administrator-only.
- No broker secret or raw access token is stored in research records.
- Research records are append-only and uniquely keyed per signal/minute.
- No recommendation can mutate configuration or enable live mode.
- Live order capability remains absent.
