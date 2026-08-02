# Phase 6 Market-Regime Engine

The engine consumes only a validated summary containing current/open/previous-close prices, opening range, VWAP, fast/slow EMA, ATR percentage, relative volume, data quality, and market-data permission. Low-quality or unhealthy input returns `Uncertain` with trading prohibited.

## Deterministic priority

1. Data-quality and market-data health gate
2. Gap-up continuation or rejection
3. Gap-down continuation or reversal
4. High-volatility expansion
5. Strong bullish or bearish trend
6. Weak bullish or bearish trend
7. Low-volatility compression
8. Range-bound

Every result contains regime, optional directional bias, bounded confidence, supporting factors, contradicting factors, data quality, trading permission, and UTC observation time. Every evaluation is persisted as an append-only `MarketRegimeSnapshot`; explanations are stored as JSON.

`GET /api/system/market-regime` exposes the latest in-process projection for monitoring. It does not generate a signal or execute a trade.

## Conservative defaults

- Minimum data quality: 0.90
- Minimum trading confidence: 0.65
- Gap threshold: 0.40%
- Strong/weak EMA spread: 0.25% / 0.08%
- High/low ATR: 0.80% / 0.25%
- Expansion relative volume: 1.50

These are testable starting assumptions, not profitable parameters. Promotion requires historical replay, out-of-sample evaluation, realistic costs, and later paper evidence.

## Known limitations

- Input-summary construction and scheduled evaluation are not hosted yet.
- The initial classifier does not use option-chain, VIX, breadth, or external context.
- Confidence is a deterministic rules score, not a calibrated probability.
- Threshold optimization and performance claims are prohibited in this phase.
