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

## Interpretation boundaries

MFE and MAE use observed executable option-premium samples, not an assumed continuous price path. A rapid movement between samples may therefore be missed. Historical trades created before this release will show insufficient price-path evidence.

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
