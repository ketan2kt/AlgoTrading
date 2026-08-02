# Phase 5 Market-Data Engine

## Pipeline

`GrowwQuoteNormalizer` converts documented Groww quote fields into a source-stamped UTC `MarketObservation`. The first cumulative-volume sample establishes a baseline; later samples become volume deltas. A volume regression fails closed and requires session reconciliation.

`MarketDataValidator` rejects invalid, stale, future-dated, duplicate, out-of-order, and sequence-gapped observations. `MarketDataHealthMonitor` latches sequence gaps and out-of-order incidents so later valid data cannot silently restore `TradingPermitted`; an explicit reconciliation reset is required.

Accepted observations enter `CandleAggregator`. Buckets are aligned using Unix UTC boundaries and produce deterministic OHLC, incremental volume, and latest open interest. Completed candles and provider health are persisted through `IMarketDataPersistence`. Database uniqueness prevents duplicate candle identity.

Initial pure indicators are SMA, EMA, session VWAP from typical price and volume, and ATR. They make no trade or regime decision.

## Configuration

```json
"MarketData": {
  "MaximumAgeSeconds": 5,
  "CandleIntervalSeconds": 60
}
```

Invalid bounds prevent startup. UTC is used internally; market-session timezone conversion remains a scheduler responsibility.

## Health endpoint

`GET /api/system/market-data-health` returns provider availability, trading-permitted state, last observation/rejection, and accepted/rejected counts. It exposes no credentials or market account identifiers.

## Retention boundary

Only completed candles are persisted by this pipeline; raw quote/tick observations are not stored indefinitely. PostgreSQL partitioning, compression, and deletion jobs will be selected after measured paper-soak volume. Until then, deployments must monitor candle-table growth and retain backups according to the documented database policy.

## Known limitations

- No BackgroundService polls Groww yet.
- Groww streaming remains unsupported without an official .NET/wire contract.
- Sequence numbers are supported by the validator but REST quotes do not provide them.
- Data-gap backfill and automatic reconciliation are not implemented.
- Indicator persistence and multi-timeframe warm-up are later work.
