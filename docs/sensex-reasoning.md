# Sensex trade reasoning v1

Observation-only, no deployment. Does not change Nifty or NG, or gate Sensex entries.

Each new standard Sensex paper entry stores a versioned `reasoning` snapshot inside
the append-only PaperPositionOpened audit, linked by positionId to samples, exits,
post-exit observations and realised P&L. Good and bad outcomes retain the same entry
evidence; it is not rewritten after exit. Existing trades are not backfilled.

Evidence includes local candle history, direction, detector reasons, option quote,
initial premium stop/target/risk, planned net target reward and estimated charges,
local directional efficiency, extension, opposing-level room, same-side exposure,
and recent same-side losses. Each factor has an explanation and Supports/Concern/
Unknown status. No weighted score or win probability is claimed.

Verdicts are advisory hypotheses, not validated trade selection. Candle efficiency
is not full-day regime classification; local extremes are not major support and
resistance. Missing opposing levels and quote fields remain unknown. Quote timestamp
measures last trade age, not broker latency. Setup age remains unavailable. Estimated
target reward does not forecast actual profit or unknown slippage. Recent losses
are not evidence of an identical failed setup. Correlated factors are not counted as
independent confirmations. Existing market-location gate remains separate.

Use positionId and version for later offline cohorts: compare net winners/losers,
charges, price-path coverage and subsequent-session results before enabling a gate.
No automatic learning, parameter updates, data deletion or new provider requests.
No dedicated UI was added; snapshots are persisted in the existing audit store.
This instrumentation requires a later approved deployment before collection starts.
