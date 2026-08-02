# Live Nifty workspace

## Scope

The first chart workspace is a protected, paper-only view of the NSE Nifty cash index. It consumes the official Groww live quote REST endpoint, validates each quote, persists every accepted observation, aggregates one-minute candles, persists completed candles, and publishes workspace updates to authenticated Angular clients through SignalR.

The chart never labels historical or replay data as live. Its normal workspace query is restricted to the current Asia/Kolkata trading date; today's already-captured live candles are reloaded after restart for continuity, but prior-day candles are excluded. When live ingestion is disabled, the token is absent, the market is closed, the instrument is unavailable, or data exceeds the freshness threshold, the workspace displays that state and trading remains blocked. Stored candles are recovery/audit data; the `IsLive` and `IsFresh` fields come only from the current server feed state.

## Configuration

Live ingestion is disabled by default.

- `MarketData__LiveNifty__Enabled=true`
- A current Groww bearer token entered through the administrator dashboard, or `GROWW_ACCESS_TOKEN=<current Groww bearer token>` as a backend-only fallback

The dashboard sends the token only over HTTPS to an administrator-only, antiforgery-protected backend endpoint. It is encrypted with ASP.NET Core Data Protection before PostgreSQL persistence, is never returned to Angular, and is excluded from audit details and normal logs. Azure keeps the Data Protection key ring at the configured persistent backend path. Never commit the token. The configured fallback environment-variable name remains controlled by `Groww:AccessTokenEnvironmentVariable`.

Groww documents that manually generated access tokens expire daily at 06:00. API-key/secret and API-key/TOTP flows still require daily approval according to the current official documentation. Therefore unattended token renewal is not claimed or implemented. The UI asks for a replacement when the token is missing or expired. After it is saved, the background feed retries within five seconds without restarting the application. Trading remains fail-closed until valid live data is received.

## Live pipeline

1. During the weekday 09:15–15:30 Asia/Kolkata window, the background worker requests the official Nifty quote every two seconds.
2. The existing strict Groww response mapper requires a positive LTP and exchange trade timestamp.
3. Validation rejects stale, future, duplicate, out-of-order, or sequence-invalid observations.
4. Accepted observations are appended to `trading.market_observations` before candle aggregation.
5. Completed one-minute candles are appended to `trading.candles`.
6. The protected workspace API reconstructs the open candle from the current minute's persisted observations, so a process restart does not fabricate or erase its observed OHLC path.
7. SignalR broadcasts the server-authoritative snapshot to authenticated subscribers.

## UI behavior

The Angular workspace includes:

- application login;
- prominent Paper/live-order-prohibited banner;
- feed connection, freshness, source timestamp and timeframe;
- TradingView Lightweight Charts candlesticks;
- strategy markers plus entry, stop-loss and target price lines;
- red risk and green reward regions;
- persisted strategy/risk rows;
- explicit empty/disconnected states instead of synthetic candles.

## Current limitations

- A real live candle cannot be verified outside an NSE session.
- Market holidays are not yet integrated; a holiday causes failed/no-data polling and a fail-closed disconnected state.
- The official streaming feed is documented through Groww's Python SDK, but no supported .NET protocol/client contract was established. The system uses the documented REST quote endpoint and stays within its published category limit.
- Polling snapshots are not equivalent to an exchange tick-by-tick feed. They are live observations sampled every two seconds.
- Durable signals and risk decisions are displayed when present, and completed paper reports are audited. Cross-store atomicity and unattended scheduled strategy evaluation remain incomplete.
- Live Groww order placement remains absent.
