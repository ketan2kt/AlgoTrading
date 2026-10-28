# Pre-session source feasibility (28 October 2026)

Every briefing item must identify its source and observation date/time. A
publicly viewable price is not automatically an authorized automated feed.

| Input | No-cost source finding | Status |
| --- | --- | --- |
| Prior Nifty/Sensex and option OI | Existing authenticated Groww candles and chain. | Connected; in-progress changes add recent-session context and exclude expired OI. |
| Pre-open index | Groww's documented quote has a trade timestamp, but is not documented as an auction indicative price. | In-progress optional same-day 09:12 quote, labeled as a quote, not indicative. |
| USD/INR | ECB EUR/INR and EUR/USD daily reference rates; free reuse with attribution. | In-progress calculated, dated cross rate; not live FX. |
| US 10-year yield | US Treasury official daily par-yield CSV, no key. | In-progress dated US yield; not Indian bonds. |
| Overnight Fed release | Federal Reserve monetary-policy RSS feed. | In-progress recent official headline; not a full event calendar. |
| Crude | EIA API is free but needs a key and its WTI spot series has a publication lag. Groww's MCX LTP fallback has no source timestamp. | Not a reliable fresh pre-session crude quote. |
| GIFT Nifty, FII/DII and Indian pre-open auction | NSE exchange data policy requires an agreement for intended market-data use and prices commercial usage. No authorized no-cost automated feed verified. | Not connected; do not scrape exchange pages. |
| US/Asian stock indices | No official no-key overnight index feed with suitable automated commercial reuse verified. FRED has personal/third-party restrictions. | Not connected. |
| Full economic calendar | Official calendars exist, but no complete structured no-key feed covering relevant Indian and global events verified. | Not connected. |

Official references: [Groww quotes](https://groww.in/trade-api/docs/curl/live-data),
[Groww historical limits](https://groww.in/trade-api/docs/curl/backtesting),
[ECB reuse](https://www.ecb.europa.eu/stats/ecb_statistics/governance_and_quality_framework/html/usage_policy.en.html),
[ECB reference rates](https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml),
[Treasury data feed](https://home.treasury.gov/treasury-daily-interest-rate-xml-feed),
[Fed RSS directory](https://www.federalreserve.gov/feeds/feeds.htm),
[EIA API](https://www.eia.gov/opendata/),
[NSE data policy](https://www.nseindia.com/static/market-data/nse-data-policy),
[FRED terms](https://fred.stlouisfed.org/legal/terms/).

## Further research: Upstox candidate (28 October)

Upstox documents a free, one-year read-only Analytics Token that supports
market quote, historical, market-information and WebSocket APIs. Its global
instrument file includes GIFT NIFTY (documented 120-second latency), Dow,
S&P 500, Nikkei, Hang Seng, and Brent/WTI indicators (Brent example shows
20-second latency). Its market-information APIs include daily/monthly FII and
DII activity. Its V3 feed documents indicative equilibrium prices during the
pre-open for eligible instruments, but this does not prove an indicative
Nifty/Sensex *index* price is available. See [global instruments](https://upstox.com/developer/api-documentation/announcements/global-instruments/),
[token](https://upstox.com/developer/api-documentation/analytics-token/),
[FII/DII](https://upstox.com/developer/api-documentation/announcements/analytics-apis/),
and [pre-open fields](https://upstox.com/developer/api-documentation/v3/get-market-data-feed/).

This is a strong candidate for five of the six missing data groups, not a
connected feed. It requires an Upstox account/token. Live values, exact
per-instrument update times, FII/DII release times and permission for a future
multi-customer product have not been verified. For that last use Upstox directs
businesses to contact api@upstox.com. Moneycontrol [terms](https://www.moneycontrol.com/cdata/termsofuse.php?classic=true)
expressly prohibit automated extraction without written permission.

Implementation prepared: optional `Upstox__AnalyticsToken` Azure App Setting
enables the pre-session reader's read-only Upstox calls. The code resolves the
public official global instrument catalog at capture time, requests one batch
of quotes and separate cash FII/DII flows, validates timestamps, and records
only fresh dated evidence in both Nifty and Sensex brief snapshots. It never
changes the directional judgement or trading engine. With no token, no Upstox
request is made and unavailable placeholders remain. The public catalog was
read successfully (HTTP 200) on 29 October: GIFT NIFTY 120 seconds, Dow/S&P
20 seconds, Hang Seng/Nikkei 900 seconds, Brent/WTI 20 seconds. Authenticated
quote/flow values are not yet verified. The BLS calendar endpoint rejected an
automated read with Access Denied, so it is not connected as an event source.

The [Finance Calendar API](https://www.financecalendar.com/api/forex-calendar/)
documents free production use (including commercial use), 2,000 calls/day/IP,
a five-minute cache and required visible attribution. Its public endpoint
returned HTTP 200 with confirmed high-impact global events, verification
timestamps and a stale-data flag. The morning brief now requests a bounded
date range, filters stale/out-of-window events and displays IST times with a
visible attribution link. It is context only, not a complete Indian calendar
or a trade-decision input.

Production connectivity remains unverified until a deployed snapshot records
actual dated values. Upstox quotes and flows additionally require an Upstox
account and a read-only analytics token; code alone cannot activate them.
