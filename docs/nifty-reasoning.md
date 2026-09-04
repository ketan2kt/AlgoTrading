# Nifty reasoning v1

Observation only; no deployment or changes to trade selection.

Existing StrategyEvaluation and signal records already retain strategy/version,
regime, indicators, support and invalidation reasons, rejected checks and shadow
structure. Existing price samples and exit/follow-up records remain unchanged.

A supplementary append-only `NiftyTradeReasoning` audit event links by SignalId
and correlation ID to these records rather than creating a parallel trade table.
It captures the actual Nifty location and entry-quality assessments, strategy
reasons, market context, signal expiry, raw quote, simulation mode, filled entry,
initial premium SL/target/risk, planned target reward after estimated charges,
portfolio exposure and adaptive-risk assessment. Missing setup age remains null.
No Sensex evaluator, thresholds or scoring is reused. Nifty and Sensex can be
analysed under common evidence categories but must be grouped by market/version.

Historical evidence is not invented or backfilled. Subsequent samples, exits and
net results are joined by SignalId; entry reasons are not rewritten after outcomes.
Collection begins only after an approved deployment. No dedicated UI is added.
