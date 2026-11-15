# Five-session strategy-fix observation

Protocol prepared 15 November 2026. **The scheduled five-session job was cancelled by Ketan
later the same day and deleted.** This is retained as a manual/on-request comparison protocol,
not an active commitment or permission to recreate the job. No sessions have been measured.
This is observation and existing replay, not permission to retune or deploy strategies.
Expansion-pair redesign remains deferred; Hero Zero and expansion results are separate.

## Fixed comparison

- Pre-change strategy anchor: `d6639a9e392ad7e327e050e9aa90a427eae53d52`.
- Changed strategy batch: `d59b24de056a1fa0d53c611f81dab04c4f04ca18` (13 November).
- Current deployed application at study creation: `7815f283ff5428dcd6774e14612cee0c345ed980`.
- Sensex: mature-pullback room-to-opposing-level admission and two-close failed-setup confirmation.
- Bank Nifty: fresh structure for same-side losing retries, avoidance of fresh accelerating-impulse extremes,
  and two-close failed-setup confirmation. Hard stops are not relaxed.
- Nifty: unchanged strategy control for this batch; measure its actual results, do not credit it with a new rule.

Resolve both strategy anchors to exact archived module IDs and fingerprints before running A/B;
save those IDs with the first result. Never substitute current policy for both sides if the old
artifact cannot be loaded. Use the existing frozen-build and durable comparison tooling, not
temporary candidate patches. Both arms must share the same dated settings, quotes, costs,
slippage, sizes, capital assumptions and supported windows. Old/new builds may have different
runtime wrappers; comparison uses the current execution engine for both frozen decision modules.
Record any protocol incompatibility explicitly, without weakening coverage or execution checks.

No user-selected lot/risk changes are made. If the user changes settings or strategy during the
window, tag a separate cohort and record the date/build; do not silently pool it with the original.

## Manual procedure (after capture ends, only when requested)

1. Read current handover and saved study results. Reuse the day's recording/replay audit and existing
   durable jobs, particularly the 16 November 16:20 audit; do not run duplicate expensive scans.
2. Read actual regular-trade ledgers and decision/rejection evidence for each index, including
   positions with zero/negative outcomes. Use `scripts/tasks.ps1 analysis -Date YYYY-MM-DD -Market all`
   and existing archive diagnostics. Confirm all three are present in the result.
3. Record actual after-cost net P&L, trade/winner/loser counts, average win/loss, profit factor,
   drawdown, peak-profit giveback, profitable-to-loss reversals, FailedSetupExit count and timing.
   Use stored equity paths for drawdown; distinguish closed-trade drawdown from intratrade drawdown.
4. Run the fixed A/B with existing website replay, separately per index and identical inputs.
   Require counterfactual/replacement entries, not just removal of observed losing trades.
   Keep strict evidence-backed, gap-aware independent-window and Upstox-assisted modeled results separate.
   Do not call a partial-window sum a continuous full-day equity result.
5. List changed trades: old entry/exit/net, new entry/exit/net, after-cost delta, rejected/replaced
   entries, losses avoided, winners missed and winners cut short. Match by policy decision/setup,
   contract, side and time, not by P&L resemblance. Unmatched or unsupported fills remain explicit.
6. Append the day's evidence here (or link its existing report). Update the cumulative table only
   using comparable scope. Never average win rates without their denominators.
7. Report briefly using 🟢 improved, 🔴 worsened, ⚪ unchanged/unverified, with rupee differences.
   After five sessions, recommend retain/change/reject for each market with concrete trade evidence.
   Five sessions are an initial decision sample; record missing regime coverage rather than claiming
   that an unobserved trending/choppy regime was tested. Do not change rules automatically.

## Results

No session has been measured yet. Do not prefill results or count a holiday as a session.

| Date | Index | Actual net / trades | Baseline modeled net | Current modeled net | After-cost delta | Avoided losses / missed winners | Coverage / report ID |
| --- | --- | --- | --- | --- | --- | --- | --- |

## Completion

If this study is resumed: five dated session reports, frozen build/settings identities, per-trade
differences and a cumulative per-market summary. There is no scheduled study. No new entries, orders, arming changes,
production outages or paid infrastructure are authorized by this observation task.
