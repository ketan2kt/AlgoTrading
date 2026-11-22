# Nifty causal entry correction — 22 November 2026

## Decision and scope

User subsequently authorized publishing this targeted correction and website-only deployment after successful CI. Deployment completion is not established by this research report. This fixes an existing Nifty entry-quality check, not exits, hard stops, sizing, profit protection, Bank Nifty, Sensex or recording. Friday's already-deployed frozen-boundary exit fix remains intact.

The seven-session chronological comparison improves modeled after-cost net by **₹10,796.41**, with all its baseline winning positions retained. A separate check at actual ledger entry times rejects both Friday morning losers, but also rejects one 19 November winner. These are different experiments; their benefits must not be added together.

## Actual defect and correction

`SessionMoveQualityPolicy` previously measured older candle lows against the latest session high (inverse for puts). A low that occurred before that high could therefore count as a completed pullback even during an uninterrupted rise. The maturity gate was not establishing the required event order.

For the existing Nifty profile only, a mature move near its extreme now requires either:

- The **first closed-candle reclaim** of a genuine retracement candle's high/low. The retracement must be a directional close-to-close move of the existing 0.20 ATR size; any intervening close already beyond its pivot disqualifies a later entry from reusing that confirmation.
- Existing shared **two-close breakout acceptance**, allowing orderly trends that do not pull back.

The existing direction-aligned resumption check remains mandatory. The six-bar search horizon reuses the existing retest horizon, and the confirmation requires contiguous same-session candles. Existing maturity threshold 3.5 ATR and proximity threshold 0.35 ATR are unchanged. No time cutoff, rupee loss cap, stop widening or new strategy toggle was added. Both production Nifty and historical Nifty enter through the same pre-quote gate; the policy version is recorded in configuration receipts.

## Chronological replay: after modeled costs and slippage

| November session | Baseline net | Corrected net | Difference |
|---|---:|---:|---:|
| 11 | ₹4,003.67 | ₹4,003.67 | ⚪ ₹0.00 |
| 12 | ₹7,323.63 | ₹7,323.63 | ⚪ ₹0.00 |
| 13 | −₹10,277.86 | −₹10,277.86 | ⚪ ₹0.00 |
| 17 | ₹8,797.77 | ₹8,797.77 | ⚪ ₹0.00 |
| 18, available window | −₹3,790.42 | −₹3,790.42 | ⚪ ₹0.00 |
| 19 | ₹22,611.77 | ₹22,611.77 | ⚪ ₹0.00 |
| 20, through 15:15 | ₹6,973.01 | ₹17,769.42 | 🟢 +₹10,796.41 |
| **Total** | **₹35,641.58** | **₹46,437.99** | **🟢 +₹10,796.41** |

Friday's positions, including replacement-entry effects:

| Entry IST | Contract | Baseline net | Corrected net |
|---|---|---:|---:|
| 09:47 | 25450CE | −₹6,786.78 | −₹6,786.78 |
| 10:51 | 25500CE | −₹10,796.41 | No entry |
| 11:55 | 25550CE | ₹9,489.73 | ₹9,489.73 |
| 12:16 | 25600CE | ₹13,443.13 | ₹13,443.13 |
| 12:35 | 25650CE | ₹12,109.22 | ₹12,109.22 |
| 14:15 | 25700CE | −₹10,485.88 | −₹10,485.88 |

Friday's modeled marked-to-market maximum drawdown falls from ₹20,413.58 to ₹15,562.26 (₹4,851.32 improvement). Costs fall from ₹1,836.23 to ₹1,568.57. The remaining early and late losses are not claimed fixed.

## Separate actual-entry check: 24 recorded Nifty entries

Only three recorded decisions are rejected by the corrected maturity gate:

| Actual entry IST | Contract | Actual recorded net | Same-entry modeled exit net | Result |
|---|---|---:|---:|---|
| 19 Nov 14:10:07 | 25400CE | ₹4,263.90 | ₹2,844.64 | 🔴 Winner missed |
| 20 Nov 09:53:15 | 25500CE | −₹10,925.82 | −₹10,737.04 | 🟢 Losing entry rejected |
| 20 Nov 10:51:03 | 25500CE | −₹10,763.56 | −₹10,678.94 | 🟢 Losing entry rejected |

The other 21 recorded decisions remain permitted by this gate. Arithmetic removal of these trades would improve recorded net by ₹17,425.48, or same-entry modeled net by ₹18,571.34. **Neither number is a chronological strategy return**: removing trades changes subsequent eligibility and contract selection. The chronological table is the portfolio comparison. Its 19 November result is unchanged because it does not reproduce the actual 14:10 entry; this does not erase the missed-winner evidence.

For the Friday 09:53 actual entry, a prior meaningful pullback's pivot had already been reclaimed at 09:50. For the 10:51 entry, the earlier pivot was already reclaimed at 10:48. Neither was a fresh reclaim. By contrast the 12:35 modeled winner followed the first reclaim of the 12:30 pullback; it remains eligible.

## Evidence boundaries and reproducibility

- Existing `EntryRunnerReplayStudy --normal-session` runs the shared entry/admission/risk/position policies chronologically. Entries are not seeded from the ledger. Both arms use the same explicit research configuration, source candles, recorded volumes, observed option books, slippage and costs. These are modeled comparisons, **not original-settings production parity** or verified actual daily P&L. Specialty trades are outside this Nifty study.
- All seven windows completed with zero missing-entry, missing-exit, missing-add-on or unsupported-partial execution counters. This verifies the simulated paths, not every archived contract at every second.
- 11/12/13/17/19 end at 15:30; 18/20 available spot windows end at 15:15. 18 has only 312 recorded volume bars in that window, versus 360 on 20. Therefore 18 is partial-input evidence, not a full-day completeness claim. No missing observations were invented.
- The local chart bundle lacked 19/20 futures volume. Read-only authenticated `/api/trading-workspace/nifty?candleCount=6000` supplied genuine closed futures-volume observations. Source provenance is retained in `nifty-volume-supplement.json`; the helper rejects conflicting duplicates and absent volume instead of presenting zero-trade runs as successful validation. Volume-only adapter invariance checks confirm synthetic placeholder OHLC never affects entry selection; those placeholders are not execution prices.
- Original historical settings and portfolio parity are not established by this research harness. The actual-entry and chronological differences above are deliberately reported separately.
- Evidence folder: `D:/AlgoTrading/.tools/nifty-entry-review-20261122`. Input bundle: `D:/AlgoTrading/.tools/all-session-boundary-20261120`. Files `baseline-{day}.json`, `refined-{day}.json`, `refined-gates-{day}.json`; unchanged same-entry exit evidence is `{date}-nifty-after.json` in the input bundle.
- Baseline application SHA256: `7564AF78EE8AB841FC52A496F6E34EFB61ECECF11AF5A4DFEAA59F1A6C6033D8`. Refined application SHA256 used for comparisons: `22B4622CA2CB71F805E043B30D8159FA8F0EECF4B83FA5F108E1F94082E43E3D`. Subsequent source edits only format the policy and correct its explanatory message; decision logic is unchanged. Preserved baseline artifacts were not overwritten.
- Initial three-bar-only candidate was rejected: it cut Friday's ₹12,109.22 winner and made that day ₹128.69 worse. Its production logic was replaced, not left disabled behind a flag. Its findings remain research evidence only.

## Verification and remaining boundary

195 relevant checks passed: 171 unit tests and 24 contract tests. Four recorded as-of candle prefixes cover the two Friday rejections and preserved 17/20 November reclaims; mirrored put cases and shifted dates exercise direction/time independence. Synthetic tests cover absent retracements, accepted breakouts, discontinuous candles and immature moves. No PostgreSQL-dependent integration suite or deployment was run for this local change.

Release preflight: the read-only 19–20 November entry-path receipt confirms 10 regular Nifty entries on the retained `NiftyAutomation` path, with no whole strategy path removed. This path-usage check does not assert that all 10 entries would pass the new gate. Required CI remains mandatory before dispatch.

**Bottom line:** a measured Nifty-only entry improvement, retaining the chronological winners and accepting one explicitly quantified actual-entry missed winner. Not a claim that every bad entry is eliminated. Website-only release authorized; broker settings, orders and recorder are outside scope. See handover for release status.
