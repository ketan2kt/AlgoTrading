# Multi-market paper workspaces

## Implemented markets

| Workspace | Signal market | Paper execution | Live data | Historical warm-up |
|---|---|---|---|---|
| Nifty | NSE Nifty cash index with Nifty futures confirmation | Nearest eligible Nifty CE/PE | Groww CASH/FNO | Groww CASH/FNO |
| Sensex | BSE Sensex cash index | Nearest eligible Sensex CE/PE | Groww CASH/FNO | Groww CASH |
| Natural Gas | Nearest active MCX Natural Gas Mini future | Same Mini future, long or short in the isolated simulator; manual execution alert only | Groww COMMODITY quote when exposed by the account/instrument master | Live accumulation only |

All three workspaces expose candles, strategy audit records, active/closed positions, current
price, entry, stop, target, quantity, expiry, timestamps and net paper P&L. Sensex and Natural
Gas use the market-scoped durable journal (`market_paper_positions` and
`market_strategy_audits`) so their positions survive restarts and cannot be mistaken for Nifty
broker-journal reconciliation mismatches.

The additional-market engine evaluates every completed candle. Sensex requires an EMA-aligned
five-candle structure break, stores a no-signal explanation at most every 15 minutes, blocks a
duplicate active contract, limits the proposed position to ₹5,000 initial paper risk, uses a 1:1
initial stop/target and trails the stop after 0.8R favourable movement. Paper P&L includes the
configured equity-option cost schedule for Sensex and a conservative estimated commodity
transaction cost for Natural Gas.

Natural Gas is intentionally different. It accepts only `NATURALGASMINI` futures with the
instrument-master lot size of 250 and always models four lots (1,000 units). There is no monetary
risk-sizing cap. Its stop is placed beyond the recent 12-candle structure with a 0.25 ATR buffer,
the initial objective is 2R, and the stop trails after favourable progress. Calls may carry
overnight, are limited to three per calendar month, and are never forced merely to meet a count.
No end-of-session exit applies; the position remains tracked until its structural protection,
objective or a later validated exit rule closes it.

## Groww capability boundary

The official Groww instrument master and live-data documentation list BSE F&O and MCX
COMMODITY instruments/quotes. Groww historical-candle documentation covers CASH and FNO,
not COMMODITY. Groww's Trading API overview also states that MCX commodity order trading is
currently unavailable. Therefore:

- Sensex is eligible for the complete current paper lifecycle and could be separately promoted
  through the normal live-order gates later.
- Natural Gas remains an alert-and-tracking paper call only, has no broker historical backfill,
  and is never routed to any Groww order endpoint. The administrator executes any real trade manually.
- A future Natural Gas live promotion requires an officially supported commodity broker API,
  a dedicated gateway implementation, contract tests and a new approval gate.

No workspace enables live trading and no real order endpoint is called.
