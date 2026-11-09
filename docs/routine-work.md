# Routine commands

Run from the repository root in PowerShell. Commands fail on a nonzero tool exit code.

```powershell
./scripts/tasks.ps1 status
./scripts/tasks.ps1 check -Scope backend
./scripts/tasks.ps1 check -Scope backend -Filter 'FullyQualifiedName~NameOfChangedArea'
./scripts/tasks.ps1 check -Scope frontend
./scripts/tasks.ps1 check -Scope all
```

Backend checks build and test in Release. Frontend checks run the production build
and non-watching tests. Add `-Install` to install frontend dependencies on first use
or after the lockfile changes. The helper uses the pinned Node and .NET runtimes
in `.tools` when present; otherwise they must be on PATH. .NET follows `global.json`.
PostgreSQL integration tests require Docker and `RUN_POSTGRES_TESTS=true`.
CI retains its full checks and dependency audits; the helper does not replace CI.

## Daily production analysis

After the diagnostics-capable build has been deployed, query production directly
without opening the UI or exposing PostgreSQL:

```powershell
./scripts/tasks.ps1 analysis -Date 2026-10-22 -Market all
./scripts/tasks.ps1 analysis -Date 2026-10-22 -Market nifty
./scripts/tasks.ps1 analysis -Date 2026-10-22 -Market sensex
./scripts/tasks.ps1 ops -Date 2026-10-22 -Market all
./scripts/tasks.ps1 replay -Days 30
```

The command obtains the dedicated diagnostics key from the Azure App Service settings
without printing it, calls the bounded read-only endpoint, and emits JSON. It requires
authenticated `gh` and `az` sessions with access to the existing application. The API
does not accept SQL and returns 404 when the key has not been configured. Do not copy
the key into source control, command history, logs, or chat.

`ops` returns the persisted 09:05 readiness check, 10:30/12:00/14:00 no-trade
explanations, 16:00 daily result and 16:05 telemetry audit. `replay` returns the
existing bounded strategy replay without browser navigation. Operational automation
is evidence-only: it cannot change strategy settings or place orders.

For a compact new-chat handover without reading the full history:

```powershell
./scripts/tasks.ps1 context
```

This combines the stable `docs/project-state.json` manifest with the current branch,
commit and working-tree state. It never reads credentials.

## Replay contract coverage

Normal Nifty, Sensex and Bank Nifty contract discovery uses
`RegularOptionContractSelector` in trading, recording and replay. Same-day
expiry is excluded for these regular paths; Hero Zero/pair selectors are not
changed and pairs remain outside the normal replay feature.

The independent recorder saves a pre-09:15 IST contract preflight with the
website policy fingerprint, selector version, per-market expiry/strike bounds,
guarded underlying-price intervals and actual price/depth subscription cost.
This is a bounded recording plan, not unlimited coverage of future strategies.
Capture health exposes the preflight and flags missing verification or a
session moving beyond contract coverage. Existing freshness and genuine gap
checks still apply independently. A successful preflight is not proof of
complete quote recording or a completed session.

Before comparison, both frozen configurations must have every possible selected
contract across the observed session price range in the saved manifest, with
matching dated expiry, strike, lot and tick metadata. Unsupported dates are
excluded for both versions with contract-specific reasons shown in Historical
Replay. Unknown/legacy selector protocols or missing manifests are not certified.
A compiled strategy emitting an out-of-manifest candidate rejects the replay
with HTTP409; it cannot silently omit that candidate or invent an execution.
Coverage checks never restrict live entries to the recorded band, and never
backfill missing historical depth or change trading risk controls.

## Deployment

Routine main releases automatically choose app-only or full provisioning. App-only requires a verified baseline from the previous successful deployment, identical infrastructure inputs, and matching Azure resources. A missing, expired, changed or unreadable baseline takes the full path. Set `force_infrastructure` in the workflow dispatch form for a deliberate reprovision. The workflow summary records the path. The first release containing this optimization performs a full deployment.

Before removing or replacing a regular entry path, run the database-backed
entry-path impact check over recent completed sessions. Supply the strategy
labels that the proposed change would remove, using PowerShell `-like` patterns:

```powershell
./scripts/check-entry-path-impact.ps1 -Market sensex -From 2026-10-26 -To 2026-10-30 -RemoveStrategy 'Research|*','PaperOnly|persistent-directional-thesis-v1|*'
```

For an entry-code PR, add `-ReviewId <unique-lowercase-name>` to save a
secret-free JSON review under `docs/entry-path-reviews/` and commit that file
with the code change. Use a nonmatching removal pattern (for example,
`'__no_removed_path__'`) when modifying or adding a setup without removing an
existing one. CI checks the changed entry-decision modules and fails if a new
review is missing, inconsistent, or shows zero historically retained regular
entries. The deployment preflight requires successful push CI, so this also
blocks deployment of a known failing review. CI validates the saved evidence;
it does not have production diagnostics access to independently re-query it.

It excludes Hero Zero, Expansion Pair, and pyramid add-on legs so these cannot
mask loss of regular entries. A nonzero result means stop and review the
proposed change before publishing: either no baseline regular trades were
observed or every observed regular entry path would be removed. A passing
result only proves historical usage of a remaining path; it does not prove
that current code will open a trade or that the change improves net P&L.
Record the path counts and limitations in the task handover. This check is
read-only and requires the existing Azure diagnostics access.

The live execution worker uses persisted paper fills and position lifecycle events
for quantities and exits. It does not apply a separate research-promotion gate or
five-lot size. The deployment workflow sets and verifies
`LiveExecution__PaperParityMode=PaperFillAndLifecycle`; server startup rejects a
different mode. Live entry still requires daily arming, and broker reconciliation
and duplicate-order protections remain. Closed paper trades are not replayed.
Expansion-pair legs remain explicitly blocked from live execution because their
₹0.05 floor stop cannot be protected by the current Groww stop-limit order. Do
not present local tests or broker acceptance as guaranteed live parity.

After the desired commit has been pushed and deployment authorized:

```powershell
./scripts/tasks.ps1 deploy -Ref main -DryRun
./scripts/tasks.ps1 deploy -Ref main
```

`deploy` now resolves the remote ref to its exact commit and requires a completed,
successful push CI run for that commit before either the dry run or dispatch can
proceed. If CI is pending, missing, or failed, it stops without sending a deployment.
Fix the reported CI issue, push a new commit, and retry only after that commit's
full backend, frontend, and dependency-audit checks pass. This preflight prevents
known build/test failures from consuming a deployment run. Main CI runs the protected-session
and remaining tests as disjoint sets, then packages the checked Angular and .NET outputs.
Deployment accepts only an unexpired package from that successful main push run. Before
Azure sign-in it verifies the workflow, repository, commit, attempt and package checksum;
it never rebuilds or reruns tests. All Azure safety and post-deployment checks still run.
If the package has expired (30-day retention), rerun the entire CI workflow on that commit.

Requires an authenticated GitHub CLI with workflow dispatch access. This invokes
the existing `azure-deploy.yml` with its required `deploy=DEPLOY-LIVE` input.
It deploys only the checked remote main commit, not uncommitted or unpushed local work.
It neither commits nor pushes changes. The current workflow enables the controlled-live
build while setting trading mode to Paper; older paper-only deployment descriptions
do not describe all current capabilities. Consult the workflow before changing it.

Dispatch returns a workflow progress page and stops. It does not poll for a run ID
or wait for completion. The workflow itself checks health and deployed commit identity.
Report dispatch acceptance separately from deployment success.

Only when status or troubleshooting is requested:

```powershell
./scripts/tasks.ps1 runs
./scripts/tasks.ps1 failed-logs -RunId 123456789
```

`runs` takes one snapshot. Failed logs print to the terminal; review for sensitive
information before sharing. Neither command schedules monitoring.

## Chat handover

Keep `docs/handover.md` short and current. Update it when a major task finishes or
before moving chats: outcome, relevant files, checks actually run, outstanding work,
and links. A new chat can start with: "Read AGENTS.md and docs/handover.md, then
work on <specific task>." Record facts, not assumptions about production state.
