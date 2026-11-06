# Current handover — 7 November 2026

## Approved release: remove disabled experiments; deploy the remaining batch

Active worktree: D:/AlgoTrading/.worktree-session-evidence, branch codex/instant-index-switch. Root D:/AlgoTrading contains unrelated edits; do not use it for this release. User explicitly authorized deploying remaining local work to main after removing the disabled refinement.

Removed never-enabled PullbackEntryTimingPolicy and DisableProfitLockTrailing experiments, production/replay/configuration wiring, experiment-only tests and EntryTimingReplayStudy tool. Existing production entry gates restored unchanged. Private research results remain local, outside release. Historical replay and stored market data are NOT removed.

### Retained changes
- Nifty, Sensex and Bank Nifty differentiated runner: only after a filled partial/reduced position and fresh completed-candle continuation confirmation. Remaining runner locks 50% of peak gain; prior stops never loosen. Ordinary protection resumes when continuation fails. Initial stops, reversal/manual/time exits and freshness remain. Shared production/replay policy; production default enabled, frozen legacy replay default false.
- Regular sizing uses selected whole lots and premium/stop geometry plus estimated fees, not fixed Rs5,000 loss cap. Portfolio capital/open-position limits remain; Nifty uncapped-paper shortcut removed. Explicit daily-loss switch preserved in replay. Hero Zero separate pair debit budget unchanged. Controls wording updated.
- Live partial preparation persists PartialProtectionResizing before reducing broker stop quantity. Failed resize/ack restores protection and skips unsent partial; restart/stop-fill races reconcile residual quantity. Ambiguous sells are not blindly resubmitted; unresolved state blocks new entries.
- Downloaded replay quote-cache budget: default 3 GiB with 1 GiB minimum free space; reader leases prevent active-file eviction. Only downloadable local quote/gap copies evicted, never original/cloud archives.

### Evidence and checks
Final assembled backend check after experiment removal passed: 1,046 tests (745 unit, 252 contract, 46 integration, 3 architecture); 3 Docker/PostgreSQL tests skipped locally and required in CI. Diff whitespace check passed. Entry-path validation passed for all three markets (Nifty 5/5, Sensex 8/8, Bank Nifty 6/6 retained). CI must pass exact main commit before deployment. Evidence files: lot-sizing-runner-bank-nifty.json, lot-sizing-runner-sensex.json, portfolio-limit-enforcement-nifty.json. These reuse explicitly labelled retained diagnostics; counts establish retained paths, not profitability.

Runner mechanics have deterministic tests; historical improvement NOT established. Retained 6 November partial replay had no eligible filled-partial runner, incomplete quote coverage and no completed Nifty sample. Do not call this a proven fix for all chop/trend losses. No local frontend browser testing requested; CI retains full frontend/backend/audit and PostgreSQL integration checks.

### Scope and pending
Do not stage .analysis-* files, archived experiment reports, tools/RunnerReplayStudy, or unrelated market-narrator.ts/spec.ts edits. Finish final checks, commit release files, push HEAD:main without force, wait for successful main push CI, dispatch scripts/tasks.ps1 deploy -Ref main once. Return progress link, completion unverified. No deployment/production polling after dispatch unless requested.
