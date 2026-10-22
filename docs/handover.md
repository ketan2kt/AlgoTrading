# Current handover

Updated: 2026-10-22 (Asia/Calcutta).

## Permanent production analysis access

Implemented a bounded, read-only production diagnostics surface so daily trade/no-trade
analysis no longer depends on browser UI access or direct PostgreSQL networking. The
endpoint `/api/internal/diagnostics/daily` accepts only `nifty`, `sensex` or `all` plus a
date; it returns provider health, candle/decision freshness, exact outcome counts,
bounded decision evidence, trades/P&L, available Nifty MFE/MAE samples, operational
events and explicit telemetry warnings. It cannot execute arbitrary SQL or mutate data.

Access is protected by a dedicated 256-bit App Service key, constant-time validation,
no-store responses and a 12-request/minute limiter. The deployment workflow creates the
key only when missing and never prints it. `./scripts/tasks.ps1 analysis -Date YYYY-MM-DD
-Market all|nifty|sensex` retrieves that key through the authenticated Azure CLI without
displaying it and emits the production JSON in one command. Usage is documented in
`docs/routine-work.md`.

Checks: the complete Release solution and focused API project build with zero warnings
or errors; PowerShell parser validation passed. The two focused authorization tests
compiled but local VSTest hit the existing 90-second testhost connection timeout, so
execution remains a CI responsibility. No deployment was requested or dispatched; the
endpoint and command will become usable only after an authorized commit/push/deployment.

## Hero Zero late-session and near-ATM correction

Hero Zero is now isolated to a 14:25–15:00 IST entry window for both Nifty and
Sensex. Time alone cannot open a pair: the existing five-minute range/body/structure
expansion, liquidity, OI, volume, spread, candidate-score and combined-debit gates still
apply. This removes the previous 13:30 eligibility that could buy both legs well before
the intended late expiry move.

Contract discovery no longer allows cheap-premium scoring to select distant strikes.
Nifty CE/PE candidates must be on their correct OTM side and within 100 underlying
points of spot; Sensex candidates must be on their correct OTM side and within 250
points. ATM remains eligible when it is the strongest liquid candidate, avoiding a new
blanket no-trade condition. Monitor/audit explanations identify the late window and
near-ATM cap when an entry cannot proceed. Configuration validation fails startup for
invalid time ordering or implausible distance limits.

Checks: the complete Release solution builds with zero warnings/errors. Focused tests
compile and cover the 14:25 boundary, per-market defaults, correct OTM side and maximum
distance; local VSTest again failed to connect to testhost after 90 seconds, so test
execution remains pending the mandatory CI gate. No deployment was requested or
dispatched for this change.

## 22 October no-trade production failure

Production logs for 22 October showed that the current index/chart feed was healthy, but
both Groww futures-confirmation feeds were repeatedly failing with `Groww cumulative volume
regressed; session reconciliation is required`. Since confirmation is mandatory, this
blocked both paper engines even though the charts continued to update. Nifty also aborted
some automation cycles on the unique strategy-evaluation index, and concurrent provider
health writes occasionally aborted Sensex polling.

The correction rebases a same-session Groww cumulative-volume reset at zero delta and then
continues from the new baseline; an older-session quote remains rejected. Strategy-evaluation
deduplication now uses the actual recorded strategy ID and treats a database-confirmed
concurrent duplicate as idempotent. Provider-health writes retry once after their existing
optimistic-concurrency or unique-insert boundary. The complete Release solution builds with
zero warnings/errors. The focused VSTest run compiled and started but hit the documented
local testhost connection timeout after 90 seconds; the deployment workflow's mandatory
test suite remains the execution gate. Commit `1f8b76d` deployed successfully in run
https://github.com/ketan2kt/AlgoTrading/actions/runs/39726816716.

Post-deployment evidence at 11:56-11:57 IST showed the cumulative-volume failure was gone,
but the one-retry provider-health correction was insufficient: concurrent Groww services
still collided on the shared `ProviderHealth` row and Nifty live polling failed on the
second optimistic-concurrency attempt. Groww also returned sustained HTTP 429 responses.
The durable follow-up replaces tracked provider-health writes with a PostgreSQL atomic
upsert, so health telemetry can no longer throw an optimistic-concurrency exception into a
market-feed cycle. The singleton Groww read gateway now serializes request starts at 150 ms
intervals and shares identical quotes for 750 ms, eliminating the observed same-second
request bursts without making chart data seconds old. Concurrent identical-quote coverage
was added. The complete Release solution builds with zero warnings/errors; local VSTest
again failed to connect to testhost, so the mandatory deployment suite remains the test gate.
Direct workstation DB access is intentionally unavailable because the Azure PostgreSQL
server has public networking disabled. Future database-first analysis needs a secured,
read-only in-app diagnostic query surface rather than UI inspection or public DB exposure.

## Configurable index lots and clean-slate reset

Implemented persisted administrator controls for independent Nifty and Sensex lot counts,
defaulting to 10. Normal, Hero Zero, expansion-pair/hedge and controlled-live discovery now
use the saved market count exactly; a trade is rejected with an audit reason rather than
silently reduced when existing capital/risk approval cannot support that size. The Controls
screen loads and saves both values through an antiforgery-protected administrator API.

Added a guarded, request-idempotent production reset that gzip-archives Nifty paper results
and all multi-market paper positions before removing those performance rows. Candles,
signals, strategy evaluations, market audits, price samples, exit follow-ups, system events
and audit logs remain in the database for analysis. It is disabled by default and must be
enabled with a unique production request ID; an application-setting marker prevents a repeat.

Checks: Release solution build passed with zero warnings/errors. Frontend production build
passed and 39/39 frontend tests passed. Local VSTest could not connect to testhost, but the
previous deployment failure was isolated to an incorrect directional-policy test fixture;
the fixture now exercises a non-extended EMA-aligned pullback. Commit `9023570` was pushed
to `main`; deployment run https://github.com/ketan2kt/AlgoTrading/actions/runs/39584813366
was dispatched once. Completion and the production reset remain unverified.

Run `39584813366` failed because `PaperTradeResetService` began a user transaction
directly under Npgsql's retrying execution strategy, which aborted application startup.
The cleanup switch was immediately disabled to let the deployed site start. The service
now executes the complete marker check, deletes and audit write through
`Database.CreateExecutionStrategy()` with an inner transaction, preserving atomicity and
retry compatibility. The complete Release solution builds with zero warnings/errors.
The workflow now leaves the reset disabled during ZipDeploy and enables the fixed,
idempotent reset together with post-package migration startup. Commits `0822106` and
`3849d8b` were pushed; one corrected deployment was dispatched:
https://github.com/ketan2kt/AlgoTrading/actions/runs/39587324765
Completion remains unverified under the no-poll deployment procedure.

## 21 October directional participation correction

Implemented the user-authorized Nifty/Sensex correction after the 21 October review.
Sensex no longer becomes permanently ineligible after its first two morning entries;
active-position, structural-reset, direction-flip, OI/location and portfolio-risk gates
remain. A strong persistent session thesis can now contribute to composite Sensex
confidence instead of being contradicted by an isolated early-pullback score.

Both engines now distinguish local pullback noise from session direction using sustained
VWAP-side control, EMA alignment and progress. They may enter either a completed pullback
resumption or a compact continuation-base break, with separate EMA-extension anti-chase
budgets. A mature session is not rejected solely because the total move is large when
VWAP-side control persists and a fresh compact base forms. Same-direction re-entry accepts
that continuation structure, while ordinary time-only re-entry remains rejected. Existing
directional partial-profit, runner, correlation and risk controls were retained.

Regression coverage now includes persistent continuation entry, composite Sensex
confidence, continuation re-entry, persistent-session regime override and a choppy-session
negative case. The reconstructed 21 October failure signature passed locally: bearish
directional participation was produced and alternating chop remained blocked. The complete
Release solution built with zero warnings/errors. Local VSTest again could not connect to
testhost after 90 seconds; the deployment workflow's mandatory backend suite remains the
execution gate. Commit `0374113` was pushed to `main` and dispatched once:
https://github.com/ketan2kt/AlgoTrading/actions/runs/39580130614
Dispatch was accepted; completion is unverified per the no-poll deployment procedure.

## Normal-session expansion pair

Implemented a separate Nifty/Sensex intraday CE+PE expansion-pair engine for the
user-authorized normal-session hedge experiment. It waits for a measured four-candle
compression followed by a genuine five-minute release, rejects a mature preceding
impulse, checks the official option-chain OI barrier in the release direction, and
selects same-expiry liquid CE/PE contracts with comparable premium sensitivity. The
combined debit, spread, OI/volume quality and estimated 28% basket-stop risk determine
eligibility and whole-lot size; the strategy is capped at two pairs per market/day with
a 60-minute cooldown and never averages a losing basket.

Management is isolated from the ordinary position manager: no-expansion baskets exit
after three completed candles, the defined-risk basket stops at 28%, a winner may fund
the original combined debit while retaining a whole-lot runner, and that runner uses a
25% peak-value trail. Remaining exposure closes by 15:10 IST. Every rejection, OI
assessment, entry, recovery and exit is persisted. Sensex live discovery explicitly
excludes the paired legs so they cannot be promoted as unrelated single-leg orders.

Checks: full Release backend suite passed (381 passed: 2 architecture, 68 contract,
16 integration and 295 unit; 3 opt-in PostgreSQL tests skipped). The focused new policy
and lifecycle suite passed 8/8 and the live-isolation contract passed 1/1. Deployment
was committed and pushed as `6db558b`, then dispatched once:
https://github.com/ketan2kt/AlgoTrading/actions/runs/39570776791
Dispatch was accepted; completion is unverified per the no-poll deployment procedure.

## Option-chain OI barrier entry gate

Production evidence on 21 October showed four completed Nifty/Sensex CE trades at the
time of review: two wins, two losses and combined net P&L of -₹6,329.45. The 09:40 Nifty
CE and approximately 09:40 Sensex CE both recorded zero favourable excursion and lost
about ₹9,758 combined. This proves poor entry location/room on those trades, but does
not by itself prove that OI resistance caused them because the old entry audit did not
link option-chain walls to each decision.

The missing decision layer is now implemented locally for both index engines:

- Reads the latest official Groww option-chain snapshot, no more than three minutes old,
  plus the preceding matching-expiry snapshot.
- Treats concentrated call OI above spot as resistance for CE/bullish entries and
  concentrated put OI below spot as support for PE/bearish entries.
- Detects both established walls (relative-to-median OI plus visible-chain
  concentration) and material OI buildup between snapshots.
- Rejects only a proven wall leaving less than 1.25R of underlying room. A wall with
  sufficient room remains eligible, preventing the feature from becoming another
  blanket no-trade rule.
- Missing or stale chain data is explicitly audited as unavailable and never replaced
  by invented OI; it does not silently stop paper research.
- Every accepted, rejected or unavailable assessment is persisted with snapshot age,
  wall strike, OI strength/concentration, OI change and room-to-risk evidence.
- The policy tests are tagged `DeploymentRegression`, so the existing pre-Azure quality
  gate must pass them before any future deployment begins.

Checks: the Release unit-test project and all referenced production projects build with
zero warnings/errors. Direct execution against the compiled policy passed nearby call
wall rejection, growing put-wall rejection, sufficient-room acceptance and missing-data
behaviour. Local VSTest remains subject to the previously documented testhost startup
failure. Commit `98394ad` was pushed to `main` and dispatched once:
https://github.com/ketan2kt/AlgoTrading/actions/runs/39546871549
Dispatch was accepted; completion is unverified per the no-poll deployment procedure.

## 19 October directional participation correction

The 19 October paper session exposed three deterministic contradictions: Nifty's
four-entry/two-loss lockout prevented later participation after valid fresh pullbacks,
persistent-directional positions could be stopped by option-premium noise in under a
minute before the underlying thesis could confirm or invalidate, and Sensex could flip
CE/PE direction inside the same opening whipsaw.

The local correction is deliberately limited to the explicitly paper-only persistent
directional source (which automatic live discovery already excludes):

- Nifty retains the standard four-entry allocation and 30-minute post-loss cooldown,
  but a fresh persistent-directional pullback may use at most two bounded recovery
  entries. The consecutive-loss pause no longer becomes an all-day lockout for that
  qualified source.
- Initial premium stops on persistent-directional paper positions are deferred until
  the underlying thesis confirms a change of character. A 2R catastrophic boundary,
  all profit-protection stops, forced exits and kill-switch exits remain immediate.
- Sensex blocks an opposite-side re-entry for 30 minutes after an exit, while same-side
  pullbacks remain available. Persistent Sensex paper positions use the same
  thesis-aware initial-stop confirmation.
- Hero Zero no longer treats a below-average candle as an expansion. A pair requires
  at least 1.20x recent range, a 0.60x body and a decisive close beyond the prior
  nine-candle structure before it may open.
- Every deferred stop and every direction-flip rejection is persisted for later review.
- The 19 October failure signature is now an embedded deployment-regression fixture.
  It covers the observed early premium-stop sequence, bounded Nifty recovery after the
  normal allocation, the Sensex opposite-side reset, Hero Zero expansion quality and
  the retained immediate safety boundaries. It is explicitly a reconstructed failure
  signature, not a tick-complete market replay.
- The Azure workflow now runs that protected-session regression plus the complete
  backend test suite before Azure login, settings changes or package deployment. A
  failure prevents deployment from starting.

Checks: the entire Release solution builds with zero warnings/errors using single-node
MSBuild; the deployment-regression test project builds with zero warnings/errors; direct
fixture-driven runtime checks passed for bounded recovery, stop deferral, reserve
exhaustion, direction-flip reset and weak/decisive Hero Zero expansion. Focused unit
tests compile, but VSTest on this machine again failed to connect to testhost after 180
seconds, so execution is delegated to the new mandatory GitHub quality gate. The
strategy correction is committed as `39a615c`; the regression gate is committed as
`fa736ee`. Both were pushed to `main` and dispatched once for deployment:
https://github.com/ketan2kt/AlgoTrading/actions/runs/39241134597
Dispatch was accepted; completion is unverified per the no-poll deployment procedure.

## Persistent directional paper-entry integration

Midday production review on 16 October showed healthy/fresh Nifty and Sensex feeds but
zero trades. Nifty repeatedly recognised the directional move only after legacy timing,
location and `MatureTrend` gates considered it extended. Sensex's persistent directional
challenger recorded multiple `WouldEnter=true` pullback windows but was hard-coded
`ShadowOnly` until 22 October.

The integration was deployed in commit `0827332`:
https://github.com/ketan2kt/AlgoTrading/actions/runs/38858953616
The run completed successfully and production showed the new `PaperEligible` thesis,
including `WouldEnter=true` windows. No trades were created because the later professional
plan required at least 1.15 after-cost reward/risk while the configured option geometry
produced about 0.88-0.93 after costs. This was a deterministic contradiction.

A local correction now keeps sub-1.15 after-cost reward/risk as audit telemetry rather
than a hard paper-entry veto. The persistent paper source also bounds an outlying
structural stop to its 0.35-1.50 ATR research band instead of rejecting the entry, and
does not require the same pullback proof again through the legacy structural-reentry
gate. Fresh data, option availability/quote validity, valid price geometry, portfolio
risk, capacity, daily caps and kill-switch controls remain. `PaperOnly|...` remains
excluded from automatic live discovery.

Checks: changed unit-test project builds in Release with zero warnings/errors, including
the new regression tests. Local VSTest execution is currently blocked by a machine-level
testhost connection timeout (testhost failed to connect after both 90 and 180 seconds),
so the tests have compiled but not executed in this correction. The correction was
committed and pushed as `c1ab746`, then dispatched once:
https://github.com/ketan2kt/AlgoTrading/actions/runs/38879942476
Dispatch was accepted; completion is unverified per the no-poll deployment procedure.

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
