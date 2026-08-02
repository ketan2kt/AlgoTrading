# Groww API Capability Matrix

Re-reviewed against the official Groww Trading API cURL and feed documentation on **2026-08-02** for Phase 4. Profile, quote, non-deprecated historical candles, and instrument CSV are implemented behind a read-only contract with sanitised fake-response tests. Every later endpoint must still be rechecked immediately before implementation.

Legend: **Documented** means an official page describes it; **Partial** means usable but required behaviour/fields remain uncertain; **Unverified** means no sufficient official contract was found during Phase 0; **Unsupported** means the desired capability is not established by the reviewed docs.

## Capabilities

| Area | Status | Officially documented capability | Design consequence / gap |
|---|---|---|---|
| Subscription | Documented | Active Trading API subscription is a prerequisite | Validate entitlement during secure setup |
| Manual access token | Implemented | Bearer access token generated in Groww; expires daily at 06:00 | Backend environment provider only; never persisted or sent to Angular |
| API key + secret | Partial | Token endpoint using SHA-256 of secret + epoch timestamp; docs say daily approval required | Secret stays backend-only; unattended daily approval behaviour must be confirmed |
| API key + TOTP | Partial | Token endpoint accepts API key and TOTP; docs say daily approval required | Never store normal username/password; whether storing a TOTP seed is permitted/necessary is unresolved |
| Request format | Documented | Bearer auth, `Accept: application/json`, `X-API-VERSION: 1.0`; JSON responses | Version header and error envelope become contract-tested |
| Segment scope | Partial | Current changelog says MCX commodity support was added, while the Getting Started page still says MCX is unavailable | Nifty uses CASH/FNO, but adapter capabilities must follow endpoint/account evidence rather than the conflicting global statement |
| Rate limits | Partial | Orders 10/s and 250/min; live REST 10/s and 300/min; non-trading 20/s and 500/min | Shared category budgets needed; response headers/reset and historical/option-chain limits unverified |
| Instrument master | Implemented, partial lifecycle | Public downloadable CSV with exchange token, symbols, expiry, strike, lot/tick metadata and segments | Size/schema validation and atomic CASH/FNO upsert implemented; checksum/version and safe deactivation remain unresolved |
| Instrument search/master API | Partial | Changelog claims search/master; CSV contract is clear | Prefer documented CSV until search endpoint contract is verified |
| Live quote REST | Implemented with Nifty polling workspace | Quote snapshot includes LTP, volume, bid/offer, OI, OI change and last-trade time mapping | The protected paper workspace polls NSE NIFTY every two seconds during the configured session, persists accepted observations and blocks on stale/disconnected data; per-segment nullability still needs empirical validation |
| LTP/OHLC REST | Documented | Dedicated lightweight endpoints are described on Live Data page | Use only when complete quote is unnecessary; category rate budget shared |
| Option chain | Documented | Exchange/underlying/expiry endpoint returns underlying LTP, strikes, CE/PE LTP, OI, volume and Greeks | PCR/max pain can be derived only after completeness, timestamp, and strike coverage validation |
| Feed | Partial, not integrated | Official Python feed docs describe subscriptions up to 1000 instruments, market streams, equity/F&O order updates, and derivatives position updates | No supported .NET protocol/client contract was found. The initial live workspace uses documented REST quotes; do not port SDK internals blindly |
| Historical candles (new) | Implemented | `/v1/historical/candles`; OHLC, volume, F&O OI; intervals; data from 2020 stated | Seven-field strict mapping implemented; quality/gap/corporate-action validation belongs to Phase 5 |
| Historical range (old) | Deprecated | `/v1/historical/candle/range` explicitly marked deprecated | Do not build new integration on it |
| Expiries/contracts | Documented | Backtesting APIs enumerate derivative expiries/contracts and Groww symbols | Useful for backtests; only CASH/FNO stated for backtesting |
| Create/modify/cancel order | Documented | Equity/F&O order management with client `order_reference_id` | Not implemented before Phase 9; unknown-outcome reconciliation mandatory |
| Order query/list/detail | Documented | Query by broker ID and client reference; day order list | Client reference supports dedupe/recovery; retention beyond current day unclear |
| Trades for order/trade list | Documented | Multiple trade fills per order are exposed | Partial fills must aggregate from trades and broker status |
| Order types/products | Documented | MARKET, LIMIT, SL, SL-M; CNC, MIS, NRML; DAY validity (per annexures) | Exact segment/product combinations and exchange restrictions require validation |
| Smart orders | Partial | Changelog/docs claim GTT and OCO | Atomicity, supported F&O use, trigger hosting, failure semantics, and modify/cancel contract require Phase 4 research |
| Positions | Documented | Position retrieval and P&L fields, including realised P&L per changelog | Reconciliation mapping needed; derivative feed updates appear supported, equity position feed coverage unclear |
| Holdings | Documented | Holdings retrieval | Not required for initial intraday scope except account safety/reconciliation |
| Margin | Documented | Available margin and order margin calculation are documented categories | Define whether values are authoritative/pre-trade and timestamp semantics |
| User profile | Implemented | `/v1/user/detail` returns account identifiers, exchange flags, DDPI, and active segments | Used for read-only account/entitlement validation; identifiers must not appear in normal logs |
| Broker sandbox | Unverified | No official sandbox/test environment was established in reviewed docs | Paper broker and fake HTTP fixtures are mandatory; ask Groww before controlled live tests |
| Idempotency semantics | Partial | Duplicate client order reference error (`GA007`) is documented | Confirm whether reference uniqueness is global/day/segment and whether lookup is always available |
| Webhooks | Unverified | No webhook contract established; feed offers updates | Polling plus feed reconciliation; never assume webhook delivery |
| India VIX | Partial | Index live data is supported generally; instrument master must confirm exact India VIX symbol/feed fields | Treat availability as runtime capability, not assumption |
| GIFT Nifty/global indices | Unsupported by evidence | Not established as Groww Trading API market data | External licensed provider required |
| FII/DII, economic calendar, news | Unsupported by evidence | Not part of reviewed Groww Trading API | Optional provider interfaces; previous-session FII/DII is not a live trigger |
| Exchange calendar/holidays | Unverified | No maintainable calendar endpoint established | Separate exchange-calendar provider plus manual overrides |

## Official sources

- [Getting started, authentication, errors, and rate limits](https://groww.in/trade-api/docs/curl)
- [Instrument master](https://groww.in/trade-api/docs/curl/instruments)
- [Live data and option chain](https://groww.in/trade-api/docs/curl/live-data)
- [Orders](https://groww.in/trade-api/docs/curl/orders)
- [Portfolio](https://groww.in/trade-api/docs/curl/portfolio)
- [Historical data](https://groww.in/trade-api/docs/curl/historical-data)
- [Backtesting candles, expiries, and contracts](https://groww.in/trade-api/docs/curl/backtesting)
- [Feed](https://groww.in/trade-api/docs/python-sdk/feed)
- [Enumerations](https://groww.in/trade-api/docs/curl/annexures)
- [API changelog](https://groww.in/trade-api/docs/curl/changelog)

## Questions requiring Groww confirmation or empirical read-only validation

1. Can API-key authentication complete unattended each trading day, or is interactive approval always required? At what exact timezone/event do tokens expire?
2. Is automated TOTP generation permitted by Groww terms, and must a TOTP seed ever be retained? The design preference is not to retain it.
3. Is there an official paper/sandbox environment and a separate test credential scope?
4. Is the feed protocol officially supported for non-Python clients? Is a .NET specification/client available?
5. What are heartbeat, reconnect, replay, ordering, duplicate, sequence-gap, and subscription-limit rules?
6. Which market/order/position messages include exchange time versus broker time? Are clock guarantees published?
7. Are equity position updates available on the feed, or only derivatives positions?
8. Which quote/option fields can be null or delayed for indices, futures, and options? Does option-chain data have a snapshot/source timestamp?
9. Are OI and OI-change exchange-real-time, delayed, or snapshot values? What is the refresh cadence?
10. Are PCR and max pain supplied anywhere (not found), or must they be derived? Is complete strike coverage guaranteed?
11. What are option-chain, historical, instrument, and feed rate/connection limits and rate-limit response headers?
12. How are instrument CSV changes/versioning/checksums announced? When is it published each day?
13. What corporate-action/back-adjustment policy applies to historical candles?
14. What retention and pagination apply to orders, trades, and positions after the trading day?
15. What is the uniqueness scope/lifetime of `order_reference_id`, and can every unknown create outcome be queried by it?
16. Are broker-hosted OCO/GTT orders atomic for supported Nifty F&O products? What happens if one leg rejects or partially fills?
17. What are valid order-type/product combinations, freeze quantities, lot validation, price bands, and intraday auto-square-off rules for Nifty derivatives?
18. Are disclosed quantity, IOC validity, bracket/cover orders, or native trailing stops supported? They were not established in reviewed contracts.
19. What user/profile identifier is safe to persist for account binding without retaining personal data?
20. Does API use require static IP allowlisting, specific TLS requirements, or additional algo-registration/compliance steps?
21. Is MCX currently supported? The official changelog and Getting Started prerequisite note conflict; this does not block the Nifty CASH/FNO scope but must not be guessed.

## Implementation rule

Before each adapter endpoint is coded: archive the official URL and review date in the implementation issue, define capability flags, capture request/response/error examples without secrets, write contract tests, implement strict mapping with unknown-enum handling, and keep write endpoints unreachable until their promotion gate.
