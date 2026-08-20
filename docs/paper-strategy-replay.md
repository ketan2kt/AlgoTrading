# Paper strategy replay

## Purpose

The authenticated paper report now runs a deterministic research comparison before further strategy
promotion. It never places an order and never changes live or paper parameters automatically.

## Current evidence boundary

The database contains realised option trades, charges, recorded entry context, and one-minute option
marks while most positions were active. It does not contain a complete option-price history for every
rejected or `NoSignal` evaluation. The system therefore refuses to assign hypothetical profit or loss
to those evaluations.

The current report provides:

- the actual executed-trade baseline;
- retrospective cohorts for the recorded structure verdict and directional regimes;
- a 10% premium stop with an equal 10% target;
- a deterministic profit-lock variant using the observed minute path;
- brokerage and statutory charges on replayed exits;
- chronological 70% training and 30% validation metrics;
- trade count, wins, net P&L, expectancy, profit factor and maximum drawdown;
- explicit option-path coverage and limitations.

Entry-cohort comparisons describe only trades that were actually executed. They can identify harmful
segments, but cannot prove that a rejected setup would have won. Exit variants require at least one
intermediate option-price observation in addition to entry and exit.

## Next evidence increment

Complete entry replay requires a versioned historical option-candle dataset for each candidate contract,
including bid/offer or an explicit spread model, instrument expiry metadata, fill timing, slippage and
charges. Once collected, the same chronological split can evaluate both accepted and rejected signals.
Until then, no missed-trade profitability claim is permitted.

## Promotion rule

A candidate must improve validation expectancy and drawdown after costs across multiple sessions. A
positive training result alone is insufficient. Paper observation remains required after replay, and
no result authorises Phase 9 or live order placement.
