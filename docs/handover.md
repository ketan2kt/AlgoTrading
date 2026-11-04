# Current handover — 5 November 2026

## Session evidence — merged to main; deployment dispatched

Local follow-up: Protection → Mark reviewed dismisses only the exact prior-session alert after administrator confirmation. The existing audit log stores the review; changed evidence reopens the reminder. No trading/order writes or migration. IST-boundary and reappearance fixture added; diff checked, runtime tests pending. Not deployed.

[PR #23](https://github.com/ketan2kt/AlgoTrading/pull/23) merged to main at f08c21f. The protection badge fits the rail and shows “After hours” outside regular weekday 09:15–15:30 IST hours; manual refresh still exposes unresolved records. Broker handling is unchanged. [Main CI](https://github.com/ketan2kt/AlgoTrading/actions/runs/41899596445) passed. [Deployment](https://github.com/ketan2kt/AlgoTrading/actions/runs/41899963969) dispatched once; completion and visual validation unverified.

[PR #22](https://github.com/ketan2kt/AlgoTrading/pull/22) merged as `1456005`. [Main CI](https://github.com/ketan2kt/AlgoTrading/actions/runs/41896279171) passed all checks. [Deployment](https://github.com/ketan2kt/AlgoTrading/actions/runs/41896686655) dispatched once; completion and real-session behavior remain unverified. The per-feature notes below describe the pre-release checks and evidence limitations.

- **5 Nov · Live protection monitor, local:** Admin desk badge and read-only dialog for Nifty, Sensex and Bank Nifty. Reads a consistent DB snapshot of unsettled system live intents, paper stop/units and exact source/intent/order-linked broker receipts. Exceptions first; missing, uncertain, prior-session and superseded confirmations cannot produce a match. Shows confirmation time, not current exchange state; account-only trades are outside scope. Visible tabs refresh every 20s; no broker API calls or order/control mutations. Static checks only; runtime, live-session validation and deployment pending.

- **5 Nov · Timeline quote-gap fix (local):** Expanding the after-close timeline streams the existing shared Groww REST/feed receipt archive once, matching exact F&O exchange, symbol and held interval for Nifty, Sensex and Bank Nifty. Gross open-position marks use LTP as of receipt, with both receipt and source time ≤30s old. Unrecorded, stale, conflicting/undated and incomplete-fill intervals remain explicit; paper fills mark their instant only. Old dates without matching archive receipts are not reconstructed from minute audit snapshots. No new polling, strategy changes or historical writes. Regression fixtures added; runtime and real-session validation pending, no deployment.

- **5 Nov · Session Timeline, local only:** Daily Analysis shows gross booked vs marked paper P&L for completed three-market sessions after 15:30 IST, with interval attribution, recorded actions and same-date Replay navigation. Reuses persisted samples/fills; 30-second freshness, no future prices or summed extrema. Unbalanced/unpriced legs leave gaps until final recorded gross. Peak/drop are sampled; drawdowns do not span unknown intervals. Missing completed trades suppress the curve. Focused frontend fixtures added; static checks only, no runtime or real-session validation or deploy.

- **5 Nov · Replay vs recorded paper, local only:** Historical Replay → “Verify replay against paper” uses one completed date, three normal markets and an uninterrupted feed manifest with a pre-open fingerprint of the policy build and replay settings. Reject missing/late provenance, changed lot settings or incomplete sessions; never substitute today's configuration. Replay finishes before paper journal/position reads. The report freezes the outcome with its saved run and displays the first observable difference and per-action times, premiums, quantities and reasons (including partials/add-ons). Same-contract entries within ±60s must pair uniquely; this window does not excuse timing differences. Missing engine coverage or ambiguous matches are inconclusive. No pairs, live calls or strategy changes. Focused test fixtures added; C# syntax and diff checked, runtime tests not run locally. The new policy receipt must be captured prospectively; no real-session result or deployment yet.

- Daily Analysis → Realised paper vs live (local): compute gross from fully priced, balanced, closed system orders; split the matched gross difference into buys and sells, including add-ons, partial exits and residual sales. Deduplicate repeated order snapshots, require scaling totals to agree with the persisted intent, and exclude missing/conflicting evidence. Model exchange-specific charges separately: actual fees and actual account net are not available. No broker calls added. Session subtotals show confirmed/comparable counts separately from paper-only, rejected, open and incomplete cases. Old records without priced-quantity evidence are not certified. Added regression cases for matched units, scaling, deduplication, incomplete evidence and empty subtotals; whitespace checked, runtime tests and deployment not run.

- Paper → live admission evidence (local, not deployed): read recorded per-market startup state and switch changes as of the actual paper fill, not today's switch. New switch writes and audit share one EF save. Discovery persists deduplicated source-specific blockers (risk/contract/lot/pair, reconciliation, pre-submit disarm), reuses existing fast-close/policy records, and identifies expired unseen entries without attributing a past cause to expiry. Source observations take precedence over switch inference. Successful submission is distinct from a confirmed fill. Daily Analysis and the single-trade diagnostic read these facts; missing older evidence stays unknown. No new broker requests. Added focused switch-history/miss precedence tests, not executed locally; static diff checks only.
- Trailing-stop comparison implemented locally: Daily Analysis → Paper vs live and the single-trade diagnostic compare paper stop revisions with Groww-verified trigger, remaining units, order ID and readback time for Nifty, Sensex and Bank Nifty. Initial/trailing, partial/add-on sizing and restart/residual protection feed the timeline. Shows first exact match latency (including before-call vs call-and-verification where captured), wrong size/price, superseded or unconfirmed revisions, and same-order stop fills against the last verified trigger. Uncertain state invalidates prior fill attribution. Receipt time is NOT exchange trigger time; absent or legacy evidence cannot prove a missed stop. No stop policy changes or extra broker calls. Regression cases added; diff checked, no local runtime tests, CI or deployment. First recorded live sample remains to be validated.

- Controls → Session diagnostics → Replay capture: on-demand, admin-only pre-open master timestamp, running worker subscription and per-market last receipts. This probes no broker or history archive; worker evidence resets on restart. Only the after-close cohort check certifies a session.
- Daily Analysis separates paper results from the first recorded live fill/quantity/reconciliation difference and shows paper-to-intent and intent-to-recorded-submission intervals. Shows recorded policy exclusions; absent evidence stays unknown. New partial/add-on outcomes record paper vs actual fills after protection and intent persistence without changing order behavior. Position samples and live lifecycle events no longer count as entry rejections. This does not reconcile every intermediate action.
- Replay gives a fill-by-fill identical-build/config A/A finding; matching zero trades does not verify fills. A verified saved control offers a fixed-target comparison on its frozen inputs; the finding distinguishes evidence gaps from a held-out result. No live rules or broker calls changed.
- Focused regression fixtures added; diff checked, no local runtime tests per owner. No push or deployment for this task. First eligible real three-market session and measured strategy benefit still pending. Previous combined deployment [41874748415](https://github.com/ketan2kt/AlgoTrading/actions/runs/41874748415) was dispatched; completion unverified.

## Combined release: mobile/chart + paper/live lifecycle

The mobile-only deployment [41871894417](https://github.com/ketan2kt/AlgoTrading/actions/runs/41871894417) was canceled at its test gate; use one combined deployment after CI. Normal Nifty paper and replay now size partials in whole lots and persist a tick-aligned trail that cannot loosen. Live stop creation/modification verifies the actual Groww SL price, trigger, size and unfilled status. Scaling and final exits persist intent phases before submission, recover by stable references, and block fresh entries on uncertainty. On a proven terminal unfilled/part-filled final sell, check broker net exposure before re-protecting only the residual units. Missing or conflicting evidence, an unaccepted recovery stop, or a triggered-but-unfilled stop still require reconciliation; no software guarantees a fill. Trade diagnostics now query the JSONB source ID structurally and include partial, add-on and recovery-order evidence. No real-order testing is implied by CI.

**OCO investigation:** Groww documents F&O OCO, including changes to target/SL triggers ([official API](https://groww.in/trade-api/docs/curl/smart-orders)). There is already a broker-side DAY stop; offline, the most recently accepted stop remains, but application-calculated trails/partials do not advance. OCO adds a broker-side target, not an offline version of our whole strategy. Do not add a full-size OCO alongside the current stop: duplicate sell coverage or an independently triggered target could close the runner. A candidate design is a fixed-target whole-lot tranche plus a separately protected runner. Verify sibling cancellation, simultaneous partial/trigger, restart, DAY expiry, broker limits and identical paper/replay tranche behavior before any promotion. OCO is not enabled in this release.

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
