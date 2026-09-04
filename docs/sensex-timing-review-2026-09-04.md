# Sensex timing baseline — 31 August–4 September 2026

Source: authenticated production P&L and trade-history report read on 4 September.
Paper results after recorded estimated costs, not live account returns.

| Session | Trades | Net INR |
| --- | ---: | ---: |
| 31 Aug | 12 | 11480.61 |
| 1 Sep | 13 | 26088.80 |
| 2 Sep | 14 | -20206.10 |
| 3 Sep | 11 | -3087.03 |
| 4 Sep | 8 | 420.89 |

Total: 58 trades, 30 wins, 28 losses, net 14697.17, estimated charges 6561.83.
This does not establish that late entries caused the losing sessions.
Strategy versions changed during the week; do not pool versions to infer causation.

## Confirmed evidence

- 4 Sep entry 15:24:03, time exit 15:24:09, net -437.08:
  entry/forced-exit window mismatch. Separately fixed locally; not deployed.
- Four profitable Sensex StopLossHit exits on 4 Sep demonstrate that the label
  alone cannot classify an entry as bad; trailing stops can lock profit.
- The report passed empty arrays to Sensex exit analysis despite existing
  PositionSample and PostExit audit writers. Local reader now consumes them.
  Actual historical sample coverage must be checked after release; no backfill
  or reconstructed ticks have been fabricated.

## Measurement-only implementation

Sensex candidate audits and successful entry audits record versioned directional
extension relative to the preceding eight-bar extreme, ATR, nearest opposing
level in the available candle window, candle close age, and advisory hypotheses.
Entry audits link these observations to positionId and include evaluation-start
and paper-record timestamps. Their difference is paper pipeline elapsed time,
NOT broker execution latency. No extra broker requests or database migration.

Thresholds (extension >1 ATR, room <0.5 ATR, candle age >2 intervals) are research
hypotheses, not validated settings and not trading gates. Missing levels/ATR
produce null values, not assumed safety. Setup age remains null: first detection
of a persistent setup is not yet tracked. Candidate observations are taken only
when the existing detector emits a direction; earlier invisible opportunities
and rejected option outcomes cannot be inferred from them.

Nifty logic is untouched by this research. NG is excluded from it.

## Next analysis, before promoting any gate

Join entry positionId with realised net outcomes and actual sample coverage.
Compare winners/losses by extension, room and observed candle age, separating
strategy version, session and quantity/initial risk. Count profitable trades
each hypothesis would remove as well as losses it would avoid. Validate on later
sessions without tuning thresholds to those sessions. Assess overlapping positions
before claiming an independent filtered portfolio return. Add persistent setup
identity/first-detection tracking before evaluating setup expiry or earlier entry.

No evidence yet supports enabling these filters. No deployment authorised.
