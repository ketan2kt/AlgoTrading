# November 13 research decision: normal options only

**Starting evidence, 4 November:** the deployed Replay eligibility check returned 0 included / 6 excluded sessions for 28 October–4 November. Each lacked three session archives and the dated pre-open instrument master. No candidate has a measured profitability result. Do not rebuild yesterday's chain from today's instruments or count an excluded day as zero trades.

**Runbook:** Reports → Historical Replay → Check full-session eligibility → Control (no change). Start with the same approved build on both sides. Require identical per-day trades, costs and P&L. Select that saved run as the frozen dataset for each trial; use identical contracts, chronological quotes, capital, lots, charges and slippage. Exclude incomplete dates, Hero Zero and pairs.

| Trial | Single proposal change (all three markets) | What could disprove it |
|---|---|---|
| Smaller initial risk | Multiply each market's own percentage stop distance by 0.80. Preserve other policy settings; let dependent sizing/targets and later entries respond normally. | More eventual winners stopped out, or worse net expectancy despite smaller individual losses. |
| Confirmed reversal protection | On an already-profitable confirmed reversal only, request a stop retaining 90% of the **then-current favorable premium move**, not 90% of the historical peak or of a guaranteed net fill. | Too many runners clipped or no improvement after costs. If the gate never activates, no evidence. |
| Runner comparison | Disable target extension while keeping partials, hard stops and other controls unchanged. This tests whether keeping our existing runners earns its giveback. | If the baseline keeps substantially more upside without worse downside, retain extension. No inference on days without eligible extensions. |

**5–9 November:** verify the pre-open master and session capture on actual trading days. After each close check all three markets' coverage. On eligible discovery dates save the A/A control and the three paired comparisons. Retain every result, including negative ones. Freeze the policies, assumptions and selected build before examining the later block.

**10–13 November:** use later eligible sessions as untouched validation. Read net expectancy per trade, net daily P&L, modeled costs, worst session drawdown, largest loss and largest winner, trade count and sampled giveback. Inspect each market and trend vs choppy periods as diagnostics; no per-market tuning to the holdout. Where detailed bid/ask depth is missing, do not treat simulated execution as proof of a live fill. Check whether one best day explains the entire improvement. Higher win rate alone does not qualify.

**Decision:** retain baseline if validation net expectancy fails to improve, worst session drawdown worsens, or a candidate obtains its advantage by dropping essential market coverage. Too few dates, triggers or distinct regimes → *insufficient evidence*, continue capture past November 13, no live promotion. These few days are an engineering acceptance window, not a statistical proof of edge.

**Next safety tool if capture fails:** surface the existing capture manifest (market, pre-open master time, first/last quote, gaps, reason) beside eligibility. This can distinguish missing input from an actual no-trade session without changing the engine.

Research basis: [Bailey et al., Probability of Backtest Overfitting](https://scholarworks.wmich.edu/math_pubs/42/) on multiple trials and selection bias; [NSE on impact cost](https://www.nseindia.com/static/products-services/indices-impact-cost) on executable prices and order size.
