# Option-chain research capture

## Decision

The system uses only Groww's authenticated, officially documented option-chain API. It does not
scrape NSE, BSE, Groww, or any other website. Exchange redistribution rights are not assumed.

Snapshots are stored in PostgreSQL `option_chain_snapshots` as immutable JSONB. App Service local
files are deliberately not used because they are unsuitable as the durable research system of
record and are harder to query, back up, deduplicate, and correlate with trades.

## Capture policy

- Underlyings: Nifty (NSE) and Sensex (BSE).
- Expiry: nearest non-expired option expiry found in the synchronized Groww instrument master.
- Window: 09:15 through 15:30 Asia/Kolkata on weekdays.
- Frequency: once per minute per underlying (two Groww live-data requests per minute).
- Stored band: the nearest 25 strikes (ATM plus 12 strikes on each side), including both CE and PE.
- Fields: underlying LTP, strike, trading symbol, option LTP, OI, volume, delta, gamma, theta, vega,
  rho and implied volatility.
- Idempotency: one row per underlying, expiry, source, and UTC minute. Restarts cannot duplicate it.
- Provenance: source is `GrowwOptionChainApiV1`; received time is stored separately. Groww's option
  chain response currently has no provider snapshot timestamp, and the payload records that fact.

Minute-to-minute price, OI, volume, IV and Greek changes are derived from adjacent immutable
snapshots during analysis rather than overwriting the original observation.

## Storage impact and retention

The bounded strike band produces roughly 750 JSONB snapshots per full trading day (375 minutes x
two underlyings), not millions of per-contract rows. Actual database growth must be measured after
the first month. No automatic deletion is enabled. A later, separately approved retention job may
archive older snapshots to durable object storage before deleting database rows.

## Configuration

`MarketData:OptionResearchCapture` controls enablement, interval, strike band, and session window.
The production default is enabled at 60 seconds with 12 strikes on each side. Missing tokens,
instruments, expiry data, or API failures result in a logged capture gap; synthetic data is never
inserted.

## Current limitations

- Weekends are excluded, but the exchange holiday calendar is not yet consulted by this collector.
- The API response has no source timestamp; `SourceTimestampUtc` is therefore the normalized request
  minute and `ReceivedAtUtc` is the actual receipt time.
- PostgreSQL retention/partitioning and a research-report UI are later work.
- Stored data is for the account owner's research and is not exposed or redistributed.
