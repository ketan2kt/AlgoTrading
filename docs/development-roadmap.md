# Current product backlog

Updated 15 November 2026. This is the active backlog, not the original build plan.
The [historical roadmap](history/development-roadmap-before-2026-11-15.md) is retained only for context.
Implementation is not proof of profitable strategy behavior or completed disaster recovery.

## Implemented — not pending feature work

| Area | Current implementation / evidence |
| --- | --- |
| Foundation and website | .NET/Angular, PostgreSQL persistence, protected login, three index workspaces, charts, retained trade markers and aggregate P&L. |
| Trading lifecycle | Nifty, Sensex and Bank Nifty regular entries/management, whole-lot sizing, hard stops, partial exits, runners, protection and audit evidence. |
| Controlled live execution | Daily arming, paper-fill/lifecycle mirroring, Groww reconciliation, OCO coordination, restart and duplicate protection. See [routine operations](routine-work.md). This inventory does not activate live trading. |
| Research and analysis | Recorded decisions/rejections, daily analysis, giveback/MFE/MAE, regime/cohort and challenger evaluation already exist. No duplicate research framework requested. |
| Historical replay | Frozen policy/settings, durable jobs, comparisons/checkpoints, gap-aware windows, sized fills, timing diagnostics and separate modeled backup results. Full-day evidence is verified per recorded session. |
| Recording | Independent Groww recorder plus separate website Upstox collector, shared contract plans, option prices/depth, index snapshots, archive persistence, monitoring and after-hours candle repair. |
| Release operations | Checked main CI package reused for website and recorder, package identity checks, safe handover and rollback. Latest release: 7815f28 / [successful rollout](https://github.com/ketan2kt/AlgoTrading/actions/runs/43691319226). |

The old items “initialise Git”, “implement database/authentication/feed”, “no live gateway exists”,
“start Phase 1”, “five fixed lots” and “deploy the website” are superseded—not open tasks.

## Active evidence work

| Item | Concrete completion evidence | Status |
| --- | --- | --- |
| Dual-feed recording/replay | Per-market persisted Groww/Upstox prices and bid/ask quantities, gap audit and demonstrated backup use; full-day results separate from partial windows. | Scheduled 16 November, opening/hourly and after close. |
| Strategy-fix measurement | Same-input old/current comparisons after costs, including lost winners and replacement entries; no daily tuning. | Five-session job cancelled by user. [Manual protocol](strategy-observation-2026-11-16.md), not scheduled. |
| Backup/recovery | Backup inventory/configuration plus isolated database/key-ring and archive checks, explicitly distinct from a managed Azure restore. | [Recovery runbook](backup-restore.md). No paid resources authorized. |

No additional trading or UI implementation is authorized by this cleanup.
The latest application fixes are deployed; documentation/operations verification is separate.

## On hold — not silently enabled

- Expansion-pair research/redesign stays deferred and outside normal replay.
  User explicitly requested a global new-entry hold for **all three indexes**:
  `NewEntriesEnabled=false`, with `Enabled=true` preserving open-position management.
  Bank Nifty has no expansion-pair discovery service; the shared hold rejects it too.
  Nifty's additional hold remains false; live expansion execution remains blocked.
  Hero Zero is separate and unchanged. Deployment is authorized after checks pass;
  see handover for actual rollout status rather than inferring activation from this file.
- Additional indicators, ScalpLab/new entry paths and external context providers are
  optional hypotheses, not approved fixes. Inspect existing implementations first.

## Later, separately approved work

- Broader regime/out-of-sample and controlled-live evidence after this fixed evaluation window.
- Azure point-in-time restore drill when temporary private-server cost is approved.
- Commercial analytics/journaling, multi-tenancy, billing and broker-neutral distribution only after
  internal validation, data-licensing and security/legal review. No paid recommendations, client
  execution or capital expansion is authorized here.

## Working rules

Use dated build IDs, settings and after-cost results; separate observed and modeled outcomes.
Preserve hard stops and risk controls. No deployment during trading/capture.
A completed check is not a new feature; do not grow the backlog with redundant implementations.
Update this page and handover when an item is actually finished.
