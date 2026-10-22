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
or after the lockfile changes. The helper uses the pinned Node runtime in `.tools`
when present, otherwise Node/npm must be on PATH. .NET follows `global.json`.
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

## Deployment

The deployment workflow currently sets `LiveExecution__RequirePaperPromotion=false`
at the user's request (13 October 2026). This temporarily bypasses only the historical
paper-performance gate for Nifty and Sensex. Restore it to `true` to re-enable that
gate; the code defaults to requiring promotion when the setting is absent.
Daily arming, reconciliation, fresh eligible non-research signals, duplicate-order
prevention and broker protection still apply. Existing closed paper trades are not
replayed. Broker acceptance is not guaranteed by this switch.

After the desired commit has been pushed and deployment authorized:

```powershell
./scripts/tasks.ps1 deploy -Ref main -DryRun
./scripts/tasks.ps1 deploy -Ref main
```

Requires an authenticated GitHub CLI with workflow dispatch access. This invokes
the existing `azure-deploy.yml` with its required `deploy=DEPLOY-LIVE` input.
It deploys the selected remote ref, not uncommitted or unpushed local work.
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
