# Current handover — 4 November 2026

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
