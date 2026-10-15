# Current handover

Updated: 2026-10-15 (Asia/Calcutta).

## Professional trade framework implementation

The user ended the observation freeze and authorized one combined release covering the
ten-point framework plus the 8-15 October evidence. Implemented for Nifty and Sensex,
committed as `3238836`, pushed to main and dispatched once:
https://github.com/ketan2kt/AlgoTrading/actions/runs/38720820304
Dispatch was accepted; completion is UNVERIFIED per the no-poll deployment procedure.

Run `38720820304` later failed in ZipDeploy. The application build and tests were not
the cause: Azure reported that an App Service management operation restarted the SCM
container while OneDeploy was active. The workflow's 45-second delay did not prevent
the delayed restart. Commit `9bd68e9` changes the workflow to deploy the package
synchronously before enabling migration mode, so the settings-triggered restart can no
longer race ZipDeploy. One corrected retry was dispatched:
https://github.com/ketan2kt/AlgoTrading/actions/runs/38726518202
Dispatch was accepted; completion is UNVERIFIED.

- Added a shared auditable professional plan: regime-aware participation, after-cost
  reward/risk, liquidity, expiry, Greek/IV availability, calibrated confidence tiers,
  correlated Nifty/Sensex exposure reduction and repeat-loss structural reset.
- Added early directional pullback/resumption participation so a clean directional day
  is not missed merely because the mature breakout confirmation arrives late. Raw
  breakout chasing, noisy chop and moves beyond the maturity budget remain rejected.
- Directional option selection now prefers one-step ITM as a conservative delta proxy;
  actual spread, volume and OI are checked and missing Greeks/IV are explicitly logged,
  never invented.
- Entry size is tiered (starter/standard/high-conviction), whole-lot and still bounded by
  the existing risk engine. Same-direction correlated index exposure halves incremental
  risk rather than silently doubling it.
- Exits are regime-specific. Directional trades protect profit at 0.65R but use a wider
  1.25R trail, take a smaller partial at 1.25R and retain a runner while the underlying
  trend remains valid. Range trades realize earlier and do not extend targets.
- Implemented one paper-only winner add-on after at least 1R and fresh continuation;
  losing positions are never averaged down. Nifty add-ons use weighted entry/cost/P&L
  accounting. Sensex supports a separately tracked add-on plus partial realization and
  runner accounting through a schema migration.
- Added per-plan, add-on, partial, protection and cost telemetry for later expectancy and
  opportunity-capture analysis. No new daily-loss cap was added.

Checks: solution build passed with zero warnings/errors. Full Release backend suite passed:
351 tests (2 architecture, 66 contract, 16 integration, 267 unit); 3 external PostgreSQL
integration tests were skipped because their opt-in database flag was off. `git diff --check`
passed for the changed files. No production verification has occurred yet.

## Production paper-trade history cleanup

User authorized removal of all paper trades entered before 8 October 2026 while
preserving 8 October onward. Completed directly against production on 13 October;
no code, strategy, live-order setting, commit, push or deployment was changed.

- Cutoff: 2026-10-08 00:00 IST (2026-10-07 18:30 UTC), based on trade entry time.
- Deleted 107 Nifty paper results and 255 multi-market paper positions, including
  two stale old paper positions still marked Active.
- Deleted linked research-only history: 194 exit follow-ups, 1,164 price samples,
  3,477 position/post-exit audit samples and 36 derived daily self-improvement
  summaries. Candles, signals and strategy decisions were preserved.
- Preserved and independently re-counted all later records: 5 Nifty paper results
  and 10 multi-market paper positions.
- Recoverable gzip JSON backup:
  `/home/site/data/trade-cleanup-backups/pre-2026-10-08-paper-trades-20261013-111851Z.json.gz`
  (290,159 bytes; SHA-256
  `0ccd83513b2a45ae35faa3bc06da7aaa236909525ecf8e5edb5d9f7f753f2ba9`).
- Final read-only verification returned zero pre-cutoff results, positions, linked
  samples or follow-ups. SCM basic publishing authentication was restored to its
  original disabled state after every maintenance call.

## Current deployment

User authorized removal of the live blockers found in the 13 October investigation.
Committed and pushed `ff96caf` to main. It permits configured `Research|` Nifty and
Sensex paper sources to enter live discovery and reconciles only instruments managed
by this automation, so unrelated/manual Groww positions no longer block entries.
Quantity, daily arming, source freshness, duplicate protection, approved risk,
instrument validation, managed-position reconciliation, durable intent and OCO
protection remain enforced. The workflow sets and verifies
`LiveExecution__AllowResearchSources=true`.

Backend verification: 341 tests passed; 3 PostgreSQL integration tests skipped
because their external database flag was off. Focused live-discovery tests: 12/12.

The first deployment run failed during ZipDeploy because an immediately preceding
App Service setting change restarted the SCM container:
https://github.com/ketan2kt/AlgoTrading/actions/runs/38394132977

Committed and pushed `d90e9ec`, which adds a 45-second SCM-settle interval between
the migration-setting restart and ZipDeploy. `git diff --check` passed. Dispatched
one replacement deployment:
https://github.com/ketan2kt/AlgoTrading/actions/runs/38396646786
Dispatch accepted; completion is UNVERIFIED. Do not poll or inspect production unless
the user requests it.

## Latest live-order investigation and fix

On 14 October a Sensex paper entry at 12:02:01 IST correctly reached automatic live
execution. Production UI and downloaded App Service logs were inspected; no order
was replayed and no application setting, token or code was changed.

- Candidate: `SENSEX26O1573100CE`, 100 units. The live attempt was recorded as
  `Rejected` at 12:02:09 IST with `Groww execution API returned 401`.
- The stack trace proves the 401 occurred inside `GrowwBrokerGateway.SubmitAsync`,
  i.e. the actual order-create submission, not candidate discovery, risk approval,
  option routing, reconciliation or an order-status lookup. No Groww order ID/fill
  was returned.
- At the same time authenticated direct quote calls returned HTTP 200, the feed was
  connected, the token vault was ready through 15 October 06:00 IST, and live
  execution remained armed. The failure is therefore specific to order authorization.
- Azure routes execution through `https://relay.174-138-120-226.nip.io/`, but the
  relay was still using its primary gateway `165.232.176.1` and public egress
  `165.232.188.171`. Groww requires order submission from the registered static IP.
- Fixed the Droplet on 14 October using DigitalOcean's documented reserved-IP
  outbound configuration. Cloud-init network regeneration is disabled, netplan now
  uses anchor gateway `10.47.0.1`, and the applied default route is
  `default via 10.47.0.1 dev eth0 proto static`.
- Post-change verification returned public egress `174.138.120.226`, Caddy active,
  the safety rollback timer inactive, and the relay TLS endpoint reachable (404 at
  `/` is expected because the relay only allows specific protected Groww routes).
  Backup: `/root/sarthi-network-backup-20261014T064701Z` on the Droplet.
- This was an infrastructure routing correction; no repository commit, push or Azure
  deployment was required. The rejected intent was not replayed and no real order
  was placed. The next eligible live signal is the safe end-to-end confirmation.

## Historical 401 timeline correction

User clarified the 401 happened about a month ago, BEFORE DigitalOcean, static-IP
setup, and TOTP. Quotes/positions worked then. Current infrastructure cannot be
blamed for that old event. User console screenshot on 12 October verified current
relay outbound IPv4 165.232.188.171, default gateway 165.232.176.1; Reserved IP
174.138.120.226 differs. Groww allowlist remains unverified. No routing changed.

## Existing local work and checks

Branch main at d90e9ec (pushed). Previous local diagnostics changes in GrowwBrokerGateway.cs,
GrowwExecutionContractTests.cs, infra/groww-relay/README.md and check-egress.sh remain
uncommitted/undeployed. Seven execution contract tests and Bash syntax check passed
on 12 October using bundled SDK with escalation. No application code changed today.
Preserve unrelated README.md, angular.json, proxy.local.json, docs, scripts and tmp.
No commit/push/deployment performed. Use docs/routine-work.md and scripts/tasks.ps1.

## Next steps

The combined framework is committed and pushed. Initial deployment run 38720820304
failed because an App Service settings mutation restarted Kudu during ZipDeploy. The
workflow fix in commit 9bd68e9 moved that mutation after synchronous package deployment;
retry 38726518202 completed successfully. User-requested production verification then
found that neither Nifty nor Sensex renders its chart/workspace data. Public readiness is
HTTP 200, but App Service runtime logs show PostgreSQL 42703 failures because
`trading.market_paper_positions.CostsPaid` does not exist. Migration
20261015163000_AddProfessionalPositionManagement is in the deployed package but did not
run. The workflow's migration wait can falsely accept the already-running new build before
the `ApplyMigrations=true` settings restart occurs, then immediately set it false again.
The readiness health check now reports unhealthy while EF migrations are pending, so the
existing deployment workflow cannot complete its migration wait until the new schema is
actually applied. The focused change compiles locally; the user authorized immediate
deployment without further local PostgreSQL setup. Expose/read the
exact live eligibility and skip reason if deeper confirmation is needed. Do not replay a
closed trade as part of diagnosis. The earlier observation freeze is cancelled. The 14
October choppy-session evidence and 15 October directional-session evidence are recorded
in docs/observation-review.md. On 15 October
Nifty captured about 17% of observed peak gross profit and the Sensex expiry strangle
lost to theta/strike placement; the session was INR 83.25 gross positive but INR
169.06 net negative after charges. The user confirmed that sustained directional
sessions must be evaluated on Core participation and meaningful option-premium
capture, with research benchmarks of roughly 50 Nifty points and 100–150 Sensex
points when the available move supports them; these are validation benchmarks, not
guaranteed exits. The same review records deferred confidence-based
lot tiers and winner pyramiding for the post-window implementation discussion; no
daily-loss cap is authorized. All ten profit-maximization items agreed on 14 October,
including regime-specific entries/exits, calibrated sizing, pyramiding, runner
management, option selection and portfolio-aware exposure, are mandatory for the
next post-22-October evaluation irrespective of the review outcome. Behavioural
changes must start in shadow/paper and require evidence before production activation;
an item may be rejected or deferred with its measured reason recorded. Removed daily analysis
automation must not be recreated.

## Links

- https://sarthico.com/sensex
- https://github.com/ketan2kt/AlgoTrading/actions/workflows/azure-deploy.yml
