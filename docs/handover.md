# Current handover — 9 November 2026

## Current task: Bank Nifty option capture repair

Worktree D:/AlgoTrading/.worktree-session-evidence; branch codex/instant-index-switch. Root D:/AlgoTrading has unrelated edits. User authorized fixing and deploying recorder failures, explicitly approved emergency market-hours recorder restarts, and requested routine repair decisions without repeated questions. Trading strategy/risk/order changes are not part of this task.

### Live outcome already verified
- Previous deployment42581662643 (commit52efc8a64abbacf916d7a8a4b549e455c8b700ac) succeeded on website and recorder. UTC pre-open query fix and canonical no-slash socket URL are deployed. Live diagnosis proved old socket URL HTTP307 -> .NET strips bearer -> HTTP401; canonical route HTTP200 with same token. No credential refresh needed.
- That release subscribed995instruments but requested1987price/depthsubjects. Saved plan had330BankNiftyoptions; every Bank option subject was beyond first1000. Actual saved quotes showed Bankindex-only,0options. JWT did NOT explicitly expose a subscription limit; do not claim it did.
- Applied recorder-only preserving app-setting update MaximumInstruments=500 (approved emergency restart). Existing balanced near-ATM planner produced497instruments/991subjects. Website capture remains OFF. This narrows the declared recorded strike bands, not a claim to record every listed strike. Trading controls and30secondfreshness rule unchanged.
- New plan subscribed14:09:03IST. Direct bounded read of actual saved completed14:08/14:09IST minute shards at14:10:12: NIFTY879samples/828options/111symbols; SENSEX720/682options/130symbols; BANKNIFTY398/361options/77symbols. All3 option price AND depth source timestamps14:09:58IST; persisted receipt14:09:58.380. Bank0options BEFORE budget repair ->361 AFTER. Actual capture for all3 verified, not inferred from heartbeat/index ticks. Today's earlier gaps remain; full-day replay is NOT recovered.

### Permanent changes awaiting release
1. Capture default/validated ceiling500; recorder deployment/infra pins500. Transport checks actual subject cost (index1, derivative2) <=1000 BEFORE auth/partial subscription. No additional broker polling/connections.
2. Durable archive health now exposes per-market saved option receipt and conservative MIN(price-source,depth-source) timestamps for positive bid/offer, only after successful shard flush. Index/REST writes cannot substitute.
3. Activation gate and website capture-health require fresh saved option prices AND depth for Nifty, Sensex and Bank Nifty. Missing/stale Bank premiums now produce an explicit warning/failing activation. Genuine prior gaps remain visible but do not prevent current recovery.

### Checks and release status
- Focused backend checks passed54 (50contract+4plannerunit), including balanced497/991plan, oversized subject rejection, actual durable per-market option stamps, stale/missing option source and index-only failure. First test-only decimal/long fixture and CA1861 errors corrected; production code compiled successfully.
- Two mocked PowerShell cutover regressions passed; deployment setting500 asserted. Diff whitespace check passed. Full CI still required before release.
- Finish committing only13task files, push authorized main without force, wait CI, dispatch ONCE: scripts/tasks.ps1 deploy -Ref main -IncludeRecorder -EmergencyRecorderRestart . Return progress link/completion unverified; no production/deployment polling after dispatch unless user requests it. Runtime budget fix is already active/verified, but new permanent guards are not yet deployed.

### Preserve / cleanup
Unrelated market-narrator.ts/spec.ts edits and .analysis-* artifacts remain local; do NOT stage them. Private diagnostics: .analysis-socket-probe, .analysis-bank-plan-proof-20261109.py, .analysis-quote-proof-20261109.py. All corresponding one-use production helpers (SocketProbe, SocketLimitProbe, BankPlanProof, QuoteProof) have been removed after use; recordings untouched. Credentials stayed inside existing runtime and were not exposed in chat/files/logs. Older detailed handover preserved privately before replacing this summary.

Earlier deployed features remain: differentiated post-partial runner all3, lot-based regular risk sizing (no fixedRs5000regularcap), live partial protection recovery, bounded disposable replay cache. Disabled entry timing/profit-lock-removal experiments were deleted; HeroZero/pairs unchanged. Do not present runner profitability as historically proven.
