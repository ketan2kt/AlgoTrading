# Current handover — 4 November 2026

## Chart/mobile release and November replay experiments

The mobile/chart release now also adds isolated controls in Historical Replay for all three normal markets: unchanged A/A, 20% smaller market-specific stops, 90% lock of current favorable move on confirmed reversal, and no target extension. No live strategy promotion. The deployed eligibility screen on 4 November returned 0 eligible / 6 excluded dates (28 Oct–4 Nov). See [experiment protocol](replay-trials-november.md). Entry-impact evidence from 30 October: five Nifty normal entries retained, none removed. CI is the release test path.

The mobile desk uses one scrollable bar: Nifty, Bank Nifty and Sensex first, then reports and controls. The shared chart shows active CE/PE fills by fill time and recorded reason, the latest recorded entry decision, a short index confirmation segment only where an explicit threshold was recorded, complete 09:15–09:30 IST range or unavailable, and futures-volume labeling. Open positions distinguish observed peak, marked P&L, strategy stop and last acknowledged broker protection. Older entries can lack evidence; the chart does not infer it from later candles. No trading rules changed. Local runtime tests skipped as requested; diff whitespace checked. CI and device review are pending; no deployment dispatched.

## Open positions on load — local only

The three desks show a compact summary above the chart for each filled, still-open trade. Selecting a contract focuses its existing full trade row. No open trade means no extra panel. This is a display-only change; no local runtime tests requested or deployment dispatched.

## Faster deployment

The main deployment can skip Bicep only when the previous successful main release's baseline matches the current infrastructure source, target, live Azure resource configuration and database connection setting. The first release seeds the baseline; a mismatch, missing baseline, read failure or forced full run reapplies Bicep. Both paths retain the existing tests, secret checks, package deployment, migrations and health/build identity verification. See the workflow summary for the path used. No production speedup has been measured yet.

## Trading desk visual feedback — authorized for main deployment after CI

Reports and its menu now specify text, surface and border together. All three market tabs and desk buttons have resting borders and keyboard focus. Controls groups per-market lots/live settings, broker connection, session diagnostics and the global emergency control; existing handlers and trading policies are unchanged. Source diff inspected; no local UI tests per Ketan. These edits are not part of the [previous deployment](https://github.com/ketan2kt/AlgoTrading/actions/runs/41852809649).

## Trading desk cleanup — authorized for deployment after CI

Nifty, Sensex and Bank Nifty now show the chart immediately followed by positions (or an empty state). Research moved to Reports → Research logs, Hero Zero to navigation, and the global paper kill switch to the chart toolbar with a fallback while the feed loads. No changes to trade decisions or kill-switch semantics. [PR 17](https://github.com/ketan2kt/AlgoTrading/pull/17) includes this layout and the admin search casing fix. Validate via CI, then deploy main once; Ketan will review the live layout.

Controls distinguish unknown/loading from confirmed broker state. Lot writes update only the three supported markets. Reports/navigation and replay maintenance reorganized; older chart history on demand. Admin searches query the entire directory/inbox; capture resource samples reuse existing archive checks. No trading-policy, broker-cadence or database schema changes. CI is the verification path, per Ketan; manual desktop/mobile review and actual capture measurements remain post-release checks. Replay remains subject to the 5 November recorded-session acceptance.

## Previous release: normal historical replay and market-data capture

Ketan authorized publishing the isolated replay/capture changes to main after CI. This supersedes the prior release note below. The website compares normal Nifty, Sensex and Bank Nifty policies using the same frozen data and clock. Pair replay is excluded. Capture is enabled by default and attempts a read-only Groww subscription from 09:00 IST; it retains timestamped quotes, quote depth, instrument universes and coverage gaps. Controlled deployment applies the research migrations.

Local checks included shared-policy contracts, a million-quote streaming test, a synthetic full-session frozen comparison and frontend build/tests. The remaining acceptance check is operational: after 15:45 IST on 5 November, check each market’s coverage and compare/freeze the complete eligible session. A connected feed, an eligible session and matching rerun results are not established by successful deployment alone. Incomplete sessions must be excluded.

## Approved entry-quality / evidence release

Ketan authorized deploying the completed entry-quality/evidence changes and Nifty
expansion-pair hold, then continuing replay. Isolated release:
codex/entry-evidence-quality, D:\AlgoTrading\.worktree-entry-evidence, based on
origin/main 4f6588e. Unfinished replay remains ONLY in the original worktree.

- Regular Nifty selects next eligible expiry, not same-day; Hero Zero unchanged.
- All three normal index engines require completed five-minute confirmation only
  on choppy paths; clean/developing trends receive no additional wait.
- Nifty/Sensex may replace ONLY the coarse range-middle veto with a completed
  direction-aligned continuation, multi-frame agreement and >=1.10 ATR room.
  Actual level failures, timing, OI, spread, maturity and risk gates stay enforced.
- Sensex alternative setups cannot bypass location checks. Bank Nifty uses the
  existing fresh-structure same-side re-entry rule. Prior deployed Sensex morning
  full-stop reset preserved. Stops, protection, lots and exits unchanged.
- Nifty expansion-pair NEW entries held by NiftyEntryEnabled=false. Existing pairs
  remain managed; Sensex pairs and Hero Zero unchanged.
- Every evaluated NoSignal candle is stored. Engine prerequisite/fault phase,
  available inputs and minute heartbeats spool first under HOME/data/analysis-evidence.
  Separate worker retries idempotent SystemEvents.DetailsJson inserts. Corrupt files
  stay quarantined without starving healthy later records or restart recovery.
- Daily diagnostics flag missing/stalled regular evidence independently of specialties.
  Protected paged endpoint /api/internal/diagnostics/engine-evidence accepts date,
  market, offset and limit. Existing diagnostics key/rate limit required.
  No schema change or additional broker polling.

Checks:
- Isolated backend Release build, 27 focused unit + 11 contract tests passed.
- Final corrupt-spool boundary correction: 6 contract tests passed (2 new).
- Required full push CI still applies before one deployment dispatch.
- Entry-path reviews: 62 Nifty, 76 Sensex, 25 Bank Nifty recent regular trades,
  zero strategy-label removal. Historical usage is NOT proof of future entry
  frequency or profitability; gates can still reject all candidates.
- Nov 4 Sensex 13:55/14:25 CE lost 4212.26/4055.15 net; five-minute confirmation
  did not break prior highs. 13:31 winner did. Pattern regressions, not net replay proof.
- Nov 3 exact Nifty pre-evaluation blocker was not saved; retained logs start after
  session. Exhausted sources cannot recover the exact cause. Do not invent it.
- Simultaneous disk/DB failure is not lossless. No production failure/storage
  validation or PostgreSQL end-to-end run locally.
- Dispatch once using scripts/tasks.ps1 deploy -Ref main after successful push CI.
  Return progress link and completion unverified; no subsequent production/deploy polling.

## Replay continuation

Historical Strategy Replay remains unfinished and undeployed, excluded from release.
Original worktree D:\AlgoTrading, feat/per-market-live-switches at 6a5c642 plus
dirty replay files. Preserve them. Resume shared production entry/exit policies:
Nifty, Sensex/Bank Nifty, specialties and website comparison. Full policy parity,
executable data coverage and current-versus-proposed website comparison remain open.
Exclude insufficient old dates rather than inventing observations. No replay deployment
authorization. Do not expand checklists or repeat passing checks without a new concern.
