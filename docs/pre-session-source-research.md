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

Production connectivity remains unverified until a deployed snapshot records
actual dated values. The 29 October briefing cannot honestly be called a full
global analysis with these no-cost sources alone.
