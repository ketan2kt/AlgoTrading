# Multi-market workspaces

## Implemented shell

After administrator login, the application opens a market selector with three large cards:

- Nifty (`/nifty`)
- Sensex (`/sensex`)
- Natural Gas Futures (`/natural-gas`)

Nifty retains the existing live chart, option paper execution, risk controls and research report.
Sensex and Natural Gas have independent prepared screens for chart, paper trades, strategy research,
risk and data readiness. They deliberately display no prices or trading state yet.

## Safety boundary

The existing backend is Nifty-specific. Sensex must validate BSE index/option symbols, expiries,
lot sizes, session rules and Groww data availability before ingestion or paper execution is enabled.
Natural Gas requires a separate MCX futures instrument lifecycle, commodity session calendar,
expiry/roll handling, price limits, contract sizing and dedicated research. Nifty records and
configuration must never be silently reused for either market.

The prepared screens are therefore fail-closed. They do not call a broker endpoint, generate a
signal, copy Nifty statistics or claim that data is live. Each market will require separate database
partitioning/keys, provider health, replay evidence, configuration and promotion approval.

## Next implementation order

1. Validate official Groww capabilities and instrument identifiers for BSE Sensex and MCX Natural Gas.
2. Generalise the market workspace contract without weakening Nifty isolation or recovery.
3. Add read-only feed and historical ingestion per market.
4. Add separate replay datasets and deterministic regime calibration.
5. Promote one market at a time to paper execution after tests and manual verification.

Live order placement remains absent for every market.
