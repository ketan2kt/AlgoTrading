# Shared market context — local validation, 19 November 2026

## Decision

**Latest scope: BANK NIFTY ONLY.** At the user's request, the local Nifty and Sensex context/strategy-selection wiring, version changes, extra context field and associated test changes were removed, not disabled. Their production and replay strategy paths now match HEAD exactly. Bank Nifty's tested context gate and winner-retaining retest remain unchanged. Offline diagnostics now explicitly report this Bank-only scope.

Scope-change checks: 34 focused tests passed; backend/tool builds and whitespace checks passed. Historical proposal hashes/results below identify the evaluated all-three artifact, not the rebuilt Bank-only package. Bank's policy code is unchanged by this scope reduction, so its existing comparisons are retained without redundant replay runs.

Keep the candidate local. The Bank Nifty refinement retains the missed 12 November winner without re-admitting any of the three targeted losing entries. The earlier all-three comparison below is retained as research evidence: Nifty and Sensex were neutral, so their proposed strategy changes are no longer in the release candidate. No deployment, push, production access, recorder changes or risk-setting changes.

## Evaluated behaviour (Nifty/Sensex wiring subsequently removed)

- One causal, per-index market context distinguishes directional structure, range/chop/transition and independent volatility. It reuses completed-five-minute aggregation and the existing structure analyzer. Two completed closes through the protected boundary invalidate a thesis; a lagging window cannot immediately reinstate it below the lost boundary.
- Gaps and new sessions reset the context. Stale decision timestamps are rejected. Reconstruction is restart-safe and uses no future candles.
- Nifty retains its original 30-bar strategy lookback, direction-conflict, consensus, anti-chase and downstream admission/risk gates. Full-session context is supplied separately; existing confirmed continuation/retest strategies receive priority.
- Sensex shares this context and can prefer an existing same-direction range-edge rejection over an eligible non-persistent impulse. It cannot resurrect a candidate rejected by the existing entry gates.
- Bank Nifty retains its momentum strategy and all subsequent entry/risk checks. The refinement accepts an otherwise-qualified candidate after a completed five-minute breakout, a completed opposite-colour retest holding that breakout level within the existing quarter-ATR tolerance, and two closed one-minute resumption candles. The latest must clear the previous candle. This exception is Bank-only, symmetric for CE/PE, and has no date, time-of-day, contract or profit-outcome condition. It does not apply to five-minute source feeds.
- Hard stops, sizing, failed-setup/reversal exits, partial booking, target extension, profit protection and quote freshness remain unchanged. The earlier overly strict context-veto candidate was rejected and is not retained.

## Nifty and Sensex: chronological baseline versus proposal

These runs start flat, discover entries using the real shared entry/admission/risk policies, simulate replacement entries/cooldowns, and use the unchanged position policies and actual recorded books. They do NOT seed the day's recorded trades.

All numbers are modeled net after costs/slippage. Both arms use identical frozen study configuration. Every completed fill, not merely the aggregate net, matches.

| Session | Market | Baseline net | Proposal net | Difference | Completed positions / scope |
|---|---|---:|---:|---:|---|
| 12 Nov | Nifty | ₹7,323.63 | ₹7,323.63 | ₹0.00 | 1; session-clock run completed |
| 13 Nov | Nifty | ₹4,096.39 | ₹4,096.39 | ₹0.00 | 1; bounded flat start 11:15–15:30 IST |
| 17 Nov | Nifty | ₹8,797.77 | ₹8,797.77 | ₹0.00 | 4; session-clock run completed |
| 18 Nov | Nifty | −₹3,790.42 | −₹3,790.42 | ₹0.00 | 2; available history through 14:27 IST |
| 12 Nov | Sensex | −₹5,303.05 | −₹5,303.05 | ₹0.00 | 1; supported closed-position prefix only |
| 13 Nov | Sensex | ₹2,522.10 | ₹2,522.10 | ₹0.00 | 1; supported prefix from 11:15 flat start |
| 17 Nov | Sensex | ₹7,673.65 | ₹7,673.65 | ₹0.00 | 6; session-clock run completed |
| 18 Nov | Sensex | ₹2,822.73 | ₹2,822.73 | ₹0.00 | 7; available history through 14:27 IST |

**23 supported completed positions match: 8 Nifty + 15 Sensex. Losses avoided ₹0; winners missed ₹0.** The change does not improve their losing trades in this sample. It preserves the tested directional winners.

Sensex coverage boundaries: 12 November next admission at 11:19 lacks a qualifying fresh quote (expired 11:20:01). A separate 12:00 restart also encounters an unsupported admission and supplies no additional validated P&L. On 13 November the next entry at 14:08 is unsupported (expired 14:10); no fabricated zero-loss fill is counted. Opening archive observations on 13 November begin at 11:00, so the explicit 11:15 flat restart is not a full-day account reconstruction.

Artifacts: `final-{baseline,candidate}-{nifty,sensex}-{12,13,17,18}.json`.

## Bank Nifty: retained winner and avoided losses

### Fixed-entry exclusion audit — not replacement-entry account P&L

At the same 65 recorded-entry decision points (51 with supported previously modeled exits), Nifty and Sensex retain every recorded entry. Bank Nifty now excludes only:

| Original entry (IST) | Modeled net after costs | Current added-context effect |
|---|---:|---|
| 13 Nov 11:16 PE | −₹3,593.10 | Still excluded |
| 13 Nov 11:49 PE | −₹2,296.69 | Still excluded |
| 17 Nov 10:41 PE | −₹5,786.04 | Still excluded |
| 12 Nov 12:59 CE | +₹7,450.45 | Now retained |

This screen avoids **₹11,675.83** of modeled losses and misses **₹0** of supported winners, versus the earlier local candidate's +₹4,225.38 net exclusion benefit after sacrificing the winner. These are fixed-entry exclusions, not a forecast or whole-session portfolio profit.

The earlier report incorrectly labelled the two 13 November entries CE; their actual symbols are 61000PE. The sign/direction calculations used PE; only the prose label was wrong and is corrected here.

### Chronological entry, fill and replacement comparison

| Common supported window (IST) | Baseline net | Refined proposal net | Difference |
|---|---:|---:|---:|
| 12 Nov, flat start 12:30 through closed trades at 13:12 | ₹9,738.66 | ₹9,738.66 | ₹0.00 — winner preserved |
| 17 Nov, session open through common flat point 13:58 | ₹5,765.68 | ₹15,500.15 | **+₹9,734.47** |
| 18 Nov, available history through 14:27 | ₹3,955.98 | ₹3,955.98 | ₹0.00 |

The 12 November 12:59 61300CE is independently rediscovered and filled at 12:59:23, exiting at 13:12:07 for **₹7,750.69 net** in BOTH baseline and refined proposal. The earlier 12:45 CE also matches at +₹1,987.98. The ₹7,750.69 differs from the fixed-entry audit because this chronological run models its own entry fill rather than using the original ledger fill.

On 17 November, the proposal removes the 10:41 PE loss of ₹7,066.49 and takes the 10:56 CE for +₹2,667.99; the other five closed positions match. Closed-trade equity drawdown falls from ₹10,331.77 to ₹3,265.28 (this is not intratrade MTM drawdown).

Do not add the fixed-entry ₹11,675.83 to the chronological ₹9,734.47: they are different comparisons.

Boundaries: 12 November full-open run hits an unsupported first exit; the separate afternoon run encounters a new unsupported entry at 13:37 after the two supported winners. On 17 November the next position has an unsupported exit from 14:18. A 13 November 11:15 restart avoids the baseline 11:49 loss of ₹2,418.59 but opens another trade at 11:56 whose exit becomes unsupported at 12:12:36. Therefore **13 November's replacement-entry net remains unverified**, not a ₹0 candidate-profit result. This unresolved trade already belongs to the prior shared-context candidate; the retest refinement adds no candidate on that date.

Artifacts: `shared-context-bank-{baseline,retest}.json`, `window-{baseline,candidate}-bank-{12,13}.json`; fixed-entry receipts `shared-context-retest-{12,13,17,18}.json`.

## Wider selection check

Across all four dates, the retest refinement adds exactly ONE eligible Bank candidate relative to the previous local candidate: 12 November at 12:59. It adds none on 13, 17 or 18 November and none to Nifty/Sensex. Bank candidate-minutes move from 103 to 104 versus 120 without shared context; these are opportunities, not executed trade counts.

Nifty remains 395 baseline versus 364 proposal candidate-minutes, with 17 strategy reselections. Sensex remains 293 versus 269. Later admission gates mean these opportunity changes do not change the supported Nifty/Sensex completed trades above.

Artifacts: `selection-baseline-*.json`, `selection-candidate-*.json` (before retest), `selection-retest-*.json`.

## Reproducibility, safeguards and limits

- Baseline source commit `66dffb9f332f9dca5b1134a06a9e74a5095dc941`; preserved Application SHA256 `09C3D70C5F656C60521D2D5194445E12371B4ECBA9110A95E33172136691F9CB`. Final proposal Application SHA256 `78CA75766A16A4E7671BA28C67689EFB9184F0B860294ECB7615174193C036D0`.
- Evidence root: `D:/AlgoTrading/.tools/setup-boundary-20261118`. Baseline executable under `D:/AlgoTrading/.tools/shared-context-entry-baseline-20261119`; proposal executable under this worktree's `tools/EntryRunnerReplayStudy/bin/Release/net10.0`.
- Durable offline modes: `--normal-session ROOT DATE MARKET LABEL OUTPUT [START_IST]`, `--bank-window ROOT DATE START_IST END_IST OUTPUT`, existing `--bank-session`, `--recorded-entries`, `--market-context`, `--entry-selection`.
- N/S features require genuine timestamped futures VOLUME, not futures price OHLC. The diagnostic projects recorded volume only inside the existing entry interface; no invented futures prices enter engine/exit history. Each entry decision is evaluated with two different unused OHLC carriers and must match exactly, otherwise the study fails. Deferred admission consumes actual observed option fields. The archive loader now retains available volume/OI as well as prices/depth; final paired N/S runs include those fields.
- Independent per-market comparisons, explicit study settings/contract metadata, neutral Sensex prior thesis, no original-policy-receipt or shared three-market capital/portfolio parity claim. This is not an exchange-complete coverage certificate. Uncertain fills are excluded and stop the affected chronological run.
- The new retest was designed from these cases; these four dates are development evidence, not out-of-sample proof. A broad ranging/volatile-session advantage remains unestablished.
- Focused backend checks passed **47 tests** for context, retest, entry, replay and retained safety paths. Regression fixtures cover the winner and all three excluded losses, mirrored direction/date, broken levels, missing minutes, incomplete resumption and unsupported intervals. Earlier task checks remain valid for unchanged risk/exit code; do not sum overlapping test counts.
- Tool builds pass with no warnings/errors; whitespace check passes. Final N/S engine times approximately 3–23 seconds each, quote loading approximately 5–32 seconds, no timeouts. No deployment or production mutation.

Bottom line: the targeted Bank winner-retention refinement works in the tested cases while retaining the measured 17 November benefit. Nifty/Sensex have a neutral comparison, not demonstrated extra profitability. Keep local as requested.
