# 27 November 2026 — confirmation data and entry/re-entry results

## Decision

**Latest outcome: retain an additional, month-driven Sensex compact-base confirmation correction alongside the three earlier Nifty/Bank entry corrections.** The new Sensex correction improves four older sessions (Nov11/18/19/26), not Nov27, and preserves every baseline modeled winner. Its chronological comparison includes replacement trades. Earlier fixes remain: Nifty requires a completed five-minute escape for its short-range breakout/retest in rotating price action; Bank cannot reuse invalidated opposing confirmation or retry an unresolved failed session-extreme auction. Sensex confirmation-data corrections and current-history fallback validation also remain local. No deployment, production settings/order changes, or recorder interruption occurred.

Hard stops, position sizing, risk limits, exit rules, partial booking and profit protection are unchanged. Existing unrelated research/tool work was preserved.

## Whole-month follow-up: recurring problems and what to protect

### Scope and decision

Reviewed 365 regular actual trade rows across 22 traded dates, Oct26–Nov27, plus two zero-trade dates. Excludes Hero Zero, expansion pairs, pyramids and BANKEX: those are different strategies, not evidence for these entry changes. Actual exports span historical code versions, quantities and settings; their aggregate is descriptive, not a reconstruction under today's policy.

All 12 available quote-archive dates were attempted with the existing production-shared replay. Eight Sensex modeled windows complete; Nov12/13/23 stop at an execution-evidence gap and are reported as closed segments only; Nov9 has no supported opening contract universe. Earlier dates have actual ledgers and causal candle/entry screens, not verified executable counterfactual P&L. No missing quantities or prices were fabricated. Both arms use the same recorded quotes, assisted-book provenance, costs, slippage, settings and time window. Nov23 starts09:30 in BOTH arms; other dates09:27. A preliminary09:27Nov23 run had no opening universe and was corrected to the existing matched09:30 window, not counted as a cut winner.

**Retain the new Sensex fix. Protect Nifty's confirmed EMA-pullback route from broad tightening. Do not freeze all Bank or Sensex logic as proven profitable.** Sealing means leave an identified route unchanged while measuring it, not declaring it infallible or increasing risk.

### Recurring defect fixed — Sensex extended compact-base entry

The directional-participation route already allows an ordinary pullback entry up to1.25ATR from EMA9. Its compact-base exception permits up to1.75ATR. However, the exception needed only a close outside a small base, even when the confirming candle had almost no directional body. That grants extra chase distance without breakout-quality evidence.

The Sensex selector now rejects that selected route when extension exceeds the existing1.25ATR pullback budget AND the directional confirmation body is below the existing0.45ATR momentum minimum. These thresholds are reused, not optimized against the ledger. Near-EMA pullbacks and strong compact-base breaks retain their existing rules. No same-candle fallback to primary momentum after rejection. The next completed candle is evaluated normally; no new clock cutoff/cooldown or persistent state.

Source: `ProfessionalTradeFramework.cs` extracts the unchanged1.25 constant; `SensexMaturePullbackConfirmationPolicy.cs` adds the narrow validation/version; `SensexEntrySelectionPolicy.cs` invokes it only for selected Sensex directional participation. Nifty/Bank do not call it. Ten mirrored regression tests cover buy/sell, exact boundaries, ordinary pullbacks, strong breaks, and fallback prevention.

### New Sensex correction: every archived date, after costs

Baseline here is the already-accepted local package before this NEW correction; not actual booked P&L.

| Date | Before ₹ | After ₹ | Difference ₹ | Coverage |
|---|---:|---:|---:|---|
| Nov09 | — | — | Unverified | Unsupported opening universe |
| Nov11 | 2,538.18 | 14,045.63 | +11,507.45 | Completed modeled window |
| Nov12 | -5,303.05 | -5,303.05 | 0.00 | Closed segment only |
| Nov13 | -3,916.55 | -3,916.55 | 0.00 | Closed segment only |
| Nov17 | 8,957.61 | 8,957.61 | 0.00 | Completed modeled window |
| Nov18 | 5,190.58 | 9,832.99 | +4,642.42 | Completed modeled window |
| Nov19 | -4,322.91 | 2,800.38 | +7,123.28 | Completed modeled window |
| Nov20 | 6,845.96 | 6,845.96 | 0.00 | Completed modeled window |
| Nov23 | 3,560.92 | 3,560.92 | 0.00 | Closed segment only,09:30 start |
| Nov25 | -9,269.70 | -9,269.70 | 0.00 | Completed modeled window |
| Nov26 | -5,731.16 | 1,174.33 | +6,905.49 | Completed modeled window |
| Nov27 | -14,121.82 | -14,121.82 | 0.00 | Completed modeled window |

Eight completed windows total: **-₹9,913.25 → +₹20,265.39; +₹30,178.64 after costs.** Closed-trade equity drawdown across those chronological windows decreases₹33,864.10→₹25,813.79; this is not intratrade mark-to-market drawdown. Profit factor0.89→1.29. No claim that today's remaining Sensex losses are fixed.

Trade-level changes (IST decision time):

| Date | Avoided modeled entry/loss | Replacement entries, net after costs | Net improvement |
|---|---|---|---:|
| Nov11 |09:42 directional participation, -₹5,985.55|09:50 range rejection +₹4,335.77;10:12 early pullback +₹1,186.14|₹11,507.45|
| Nov18 |12:44 directional participation, -₹4,642.42|None|₹4,642.42|
| Nov19 |13:49 directional participation, -₹4,027.41|14:07 persistent thesis +₹3,095.87|₹7,123.28|
| Nov26 |10:56 directional participation, -₹6,905.49|None|₹6,905.49|

Total losing paths removed₹21,560.865, new modeled net profit₹8,617.7785. All49 retained positions have unchanged entry, exit, quantity and after-cost result. **All34 baseline winners retained, zero winners cut, no retained loser enlarged or extended.** Thirty-one of these winners belong to completed windows; three belong to the unchanged partial segments. No new losing replacement appears. Fifty-three baseline closed positions become52 (four removals, three additions).

Whole-month actual-entry direction screen:92/158 regular Sensex entries are reproduced by the current flat-inventory selector; all50 matching winners remain eligible. Newly excluded actual losing entries:Nov06 09:54(-₹4,954.59),Nov12 12:15(-₹3,391.33),Nov20 14:19(-₹2,760.52). These excluded actual losses are NOT extra replay savings: entry screens do not include portfolio/replacement effects. The other66 actual entries were already unmatched by the baseline and are not claimed verified. Independent causal feature screening also found the weak extended-base pattern on Nov13/18 losses, but their actual candidates were not baseline-matched. No broader profitability claim is taken from those rows.

### All retained entry fixes: per-date incremental modeled result

This adds the earlier accepted Nifty/Bank corrections to the new Sensex result; all original baseline windows/settings are matched. Deltas are after costs. A zero on a partial date means only the supported segment is unchanged.

| Date | Nifty Δ₹ | Bank Nifty Δ₹ | Sensex Δ₹ | Total Δ₹ |
|---|---:|---:|---:|---:|
| Nov09 | Unverified | Unverified | Unverified | Unverified |
| Nov11 |0|0|11,507.45|11,507.45|
| Nov12 |0|0 partial|0 partial|0 supported segment|
| Nov13 |0|0 partial|0 partial|0 supported segment|
| Nov17 |0|0 partial|0|0 supported segment|
| Nov18 |0|0|4,642.42|4,642.42|
| Nov19 |0|0|7,123.28|7,123.28|
| Nov20 |0|6,838.07|0|6,838.07|
| Nov23 |0 partial start|0 partial|0 partial|0 supported segment|
| Nov25 |0|0 partial|0|0 supported segment|
| Nov26 |0|0|6,905.49|6,905.49|
| Nov27 |13,085.29|25,249.63|0|38,334.92|

Cumulative after-cost improvement over the original entry baseline **₹75,351.62**, concentrated across six dates. Sum uses unrounded results; displayed rounded rows can differ by a paisa. Previous Nifty16/Bank30 modeled winners remain unchanged; new Sensex retains34, for80 baseline modeled winners retained across supported windows/segments. This is not₹75,351.62 of actual booked or guaranteed profit.

### Actual month: every ledger date and execution-comparison coverage

Regular trades only; zero means no selected ledger row, not a general market holiday determination.

| Date | Nifty net₹ | Bank Nifty net₹ | Sensex net₹ | Comparison available |
|---|---:|---:|---:|---|
| Oct26 |-740.12|0|26,174.21|Causal entry screen only|
| Oct27 |14,878.63|0|3,445.76|Causal entry screen only|
| Oct28 |14,363.26|0|8,905.88|Causal entry screen only|
| Oct29 |17,450.20|0|0|Causal entry screen only|
| Oct30 |-3,465.81|1,459.67|-15,342.83|Causal entry screen only|
| Nov02 |9,149.34|3,829.66|21,352.31|Causal entry screen only|
| Nov03 |0|-5,031.06|-3,407.20|Causal entry screen only|
| Nov04 |0|-7,174.50|-20,753.11|Causal entry screen only|
| Nov05 |4,310.87|1,854.26|-6,109.61|Screen; legacy LTP not executable depth|
| Nov06 |0|-4,447.95|-3,619.41|Screen; legacy LTP not executable depth|
| Nov09 |-6,408.79|5,342.37|2,022.76|Sparse archive; unsupported opening universe|
| Nov10 |0|0|0|No regular trades in export|
| Nov11 |5,010.44|-6,156.78|10,466.16|Modeled comparison|
| Nov12 |3,379.76|2,884.05|-22,331.38|Bank/Sensex partial|
| Nov13 |-1,201.31|-24,572.54|-9,736.89|Bank/Sensex partial|
| Nov16 |0|0|0|No regular trades in export|
| Nov17 |26,830.60|10,402.40|3,435.93|Bank partial|
| Nov18 |-33,170.64|5,760.19|1,357.42|Modeled comparison|
| Nov19 |25,922.44|10,415.03|16,409.69|Modeled comparison|
| Nov20 |-10,557.25|28,683.47|-13,237.68|Modeled comparison|
| Nov23 |6,386.67|48,426.69|6,445.27|Partial start;Bank/Sensex incomplete|
| Nov25 |-4,764.26|-34,076.78|-3,732.82|Bank partial|
| Nov26 |-8,470.97|5,512.93|-4,738.79|Modeled comparison|
| Nov27 |-6,490.27|-3,446.81|-1,048.49|Modeled comparison|

Actual totals: Nifty94trades/54wins/+₹52,412.79; Bank113/56/+₹39,664.30; Sensex158/87/-₹4,042.82. These actual values must not be substituted for modeled baselines or added to hypothetical improvements.

### What succeeds, what fails, and what can be sealed

Entry-time diagnostic, no future candles: compute efficiency and sign flips from21 completed one-minute closes, and four-bar pivot clearance from the four bars before the confirmation. Use the existing .32efficiency/.45flip rotation definition for this descriptive grouping, not a new production filter.362of365 actual trades have the required history; three early Bank winners lack15bars and are excluded from this classification, not from actual P&L totals.

- **Nifty's problem is not all momentum.** Non-rotating entry contexts:55trades/37wins, +₹126,429.13, profit factor2.63; removing the best trade still leaves+₹111,272.59. Rotating contexts:39/17, -₹74,016.34. Within rotation, entries already clearing the four-bar pivot lose₹69,526.68 across22trades, but include8winners. That is evidence of short-range false continuation, NOT permission to veto every rotating entry. The earlier local range-escape correction targets one vulnerable setup and preserves the other routes.
- **Sensex's win rate hides payoff imbalance.** Actual current directional participation wins29/48 yet nets-₹17,438.46; persistent thesis wins23/41 yet nets-₹20,294.36. The extended weak-base defect is now corrected. Broadly requiring more breakout confirmation would remove profitable near-EMA entries, while blindly allowing small breakout closes creates poor late entries. These are different situations.
- **Bank's failure is not simply choppiness.** Of110 entries with sufficient history,28rotating entries net-₹10,122.71, while82other entries net+₹17,241.10. Removing that second group's best winner makes it negative. A blanket chop rule would block14actual winners, and its favorable aggregate is not robust enough to seal the entire breakout strategy. Retain the targeted failed-auction/held-confirmation corrections and the unchanged winners/protection.

Protect/leave unchanged list:

| Route | Evidence after costs | Decision |
|---|---|---|
| Nifty confirmed EMA-pullback continuation, normal and existing exploration labels combined |14modeled trades,9wins,+₹30,339.66 across10dates; excluding best trade still+₹15,712.40; excluding best day+₹13,542.69|**Best route to seal operationally: leave its entry/runner/exit rules unchanged.** Preserve its regression winners when touching shared code. Partial replay window remains identified, not called a full-month proof.|
| Nifty range breakout/retest with accepted local range-escape correction |7modeled trades,5wins,+₹24,262.95|Keep the targeted correction; do not apply it indiscriminately to EMA pullbacks.|
| Nifty opening-range route |2modeled winners,+₹12,529.15|Keep unchanged, but sample too small to call independently proven.|
| Sensex directional participation AFTER this correction |15modeled trades,12wins,+₹28,477.83 over8dates; excluding best trade+₹20,208.86, best day+₹15,611.53|Promising corrected route to retain and measure. It was selected/tested on this history, so do not seal it as independently proven yet.|
| Sensex early pullback |Actual22trades/12wins,+₹9,910.79; removing best day makes it-₹594.59|Keep existing winners, not a robust strategy-wide seal.|
| Bank complete breakout strategy / Sensex persistent thesis and range fades |Material remaining losses or small/concentrated samples|Not sealed. No additional speculative rule retained.|

Counterchecks rejected in this follow-up without adding source: applying Bank's session-extreme rule indiscriminately to Nifty/Sensex would reject existing modeled winners (NiftyNov17+₹12,367.62; SensexNov17+₹8,162.09, among others). Generalizing it to every local eight-bar failed break would cut13Bank winners, includingNov20+₹12,262.36 andNov26+₹9,843.92. These were causal entry screens, not portfolio P&L claims; rejection at the winner-preservation gate avoided unnecessary implementation/replays.

### Verification and evidence

- Replay baseline frozen `bank-auction-final`, new frozen `sensex-base-impulse`; final ApplicationSHA256 `BCA8C2BB078DED7FCA40E6124C00FC294782C1195D86ECADEFF35883B40B817C` matches tested source. All12Sensex before/after runs made this follow-up using the existing replay tool and cached recorded inputs.
- Files: `.tools/entry-fixes-20261127/month/<date>/sensex-month-baseline.json` and `sensex-base-impulse.json`; causal screens `selection-<date>-auction-final.json` versus `selection-<date>-sensex-base-impulse.json`. Nifty/Bank selection arrays are byte-for-byte identical across all24date screens for this new Sensex change.
- Ten new mirrored tests; focused37passed. Repository backend helper:1055unit+479contract+3architecture=1537passed. Database integration excluded for this pure-entry follow-up; earlier data-fallback PostgreSQL evidence retained separately. No skipped/failing test counted as pass; CI has not run because nothing was published.
- Full-day model still differs from actual policy/fills/starting thesis. Actual screens and executed models are separately labeled. The positive correction is local only. No push, deployment, live orders, risk/settings changes, or recorder interruption.

## Latest Bank Nifty follow-up — 11:48 and 14:10 corrected locally

### Cause and retained fix

Both entries passed the original EMA/momentum filters in the remembered bearish direction. The missing distinction was a **new session-low breakout that had already been fully rejected**:

- At 11:30 price closed at a new low of 63,164.15. The next candle closed at 63,212.35, above the failed candle's high of 63,211.70. At the 11:48 entry decision, the latest completed close was 63,223.70: it had not even reclaimed that near boundary of the failed bearish candle.
- The 13:30 session-low break reached 63,146.00 and was fully rejected through its 63,162.50 high on the next completed candle. At the 14:10 decision, the latest completed close was still 63,198.20, on the wrong side of that failed-break boundary.
- The earlier profitable 11:16 PE remains eligible: its completed close 63,379.85 had reclaimed the earlier failed candle's near boundary 63,396.75. The fix does **not** force price to chase a new session low before every valid pullback entry.

`BankNiftyFailedAuctionPolicy` reconstructs these events from completed, contiguous same-session candles. A same-direction retry must reclaim the failed candle's near boundary; a close through the original extreme retires the earlier failure. An opposite-side replacement needs two completed closes beyond the post-failure range, and that accepted level must remain intact. Wick-only tests do not invalidate acceptance. A missing interval or new session resets the reconstructed history rather than inventing an event across missing candles.

The opposite-side check matters: simply suppressing 11:48 exposed a new 12:00 CE losing ₹12,304.03 in the model. An earlier accepted upward break had itself subsequently lost its level. The final correction no longer reuses that failed acceptance and removes this replacement loss. It uses price structure, not a time cutoff, fixed loss cap, direction ban or tighter exit.

Production and historical Bank entry discovery both call the same `IndexMomentumBreakoutPolicy.EvaluateBankNifty`, now carrying `bank-failed-auction-v1`. Nifty and Sensex selection, stops, targets, sizing, risk controls, partial exits and profit protection are unchanged by this follow-up.

### Incremental after-cost result versus the already accepted local fixes

| Date / position | Before | After | Improvement |
|---|---:|---:|---:|
| Nov27 11:48 PE | −₹6,010.33 | No entry | 🟢 ₹6,010.33 |
| Nov27 14:10 PE | −₹8,004.72 | No entry | 🟢 ₹8,004.72 |
| Nov27 whole Bank day | +₹5,209.82 | +₹19,224.86 | 🟢 ₹14,015.05 |
| Nov20 12:51 CE | −₹6,838.07 | No entry | 🟢 ₹6,838.07 |
| Nov20 whole Bank day | −₹15,968.22 | −₹9,130.15 | 🟢 ₹6,838.07 |
| All supported comparison paths | | | **🟢 ₹20,853.11** |

Calculations use unrounded fills: Nov27 incremental difference is 14,015.04500; Nov20 is 6,838.06625; total 20,853.11125. Display rounding can differ by a paisa when subtracting rounded totals. These are modeled results, not actual booked profits. Actual losses on today's requested trades were ₹6,776.86 and ₹8,104.16; those are not substituted into replay savings.

All **30 Bank replay winners** retained with identical fills/P&L, including today's 10:31 +₹9,756.02 and 11:16 +₹9,468.84. No retained loser enlarged, no winning partial/final exit reduced, no replacement/delayed position added. Baseline had 53 completed positions across supported paths; candidate retains 50 unchanged and removes three losing positions.

### Every available Bank archive date — final build

| Date | Accepted-local baseline net | New net | Coverage / change |
|---|---:|---:|---|
| Nov09 | 0 | 0 | Opening universe unsupported; not full-session evidence |
| Nov11 | 17,644.35 | 17,644.35 | Completed; identical fills |
| Nov12 | 0 | 0 | Incomplete before first closed position |
| Nov13 | 8,737.13 | 8,737.13 | Partial closed segment; identical |
| Nov17 | 15,500.15 | 15,500.15 | Partial closed segment; identical |
| Nov18 | 3,955.98 | 3,955.98 | Completed; identical |
| Nov19 | 11,132.56 | 11,132.56 | Completed; identical |
| Nov20 | −15,968.22 | −9,130.15 | Completed; one loser avoided |
| Nov23 | 0 | 0 | Incomplete opening execution; archive begins09:30 |
| Nov25 | −18,367.36 | −18,367.36 | Partial closed segment; identical |
| Nov26 | −43,453.84 | −43,453.84 | Completed; identical |
| Nov27 | 5,209.82 | 19,224.86 | Completed; two losers avoided |

The earlier full-month candidate screen was also rerun with the final compiled code for all24available calendar dates. There are114actual Bank ledger entries;97matched the preceding local baseline's candidate direction, including50winners. All50matchedwinners remain eligible. Six newly excluded actual entries are losses: Nov02 13:27 (−2570.64), Nov12 14:35 (−6503.71), Nov12 14:52 (−4339.48), Nov20 12:51 (−7632.23), Nov27 11:48 (−6776.86), Nov27 14:10 (−8104.16). The first three are **screen findings, not additional replay savings**. The17baseline-unmatched entries are not counted as verified retention. Nifty/Sensex candidate direction, strategy and confidence arrays are identical on all24dates.

### Verification and cleanup

- Final build: **1,045unit + 479contract + 3architecture = 1,527 passing tests**; focused47also passed. Twelve new mirrored CE/PE tests cover near-boundary reclaim, ordinary pullbacks, wick-only extremes/retests, fresh extreme resolution, opposite two-close acceptance, lost acceptance/reconfirmation, gap/session reset and deterministic reconstruction. Database-dependent integration was not part of this filtered suite; no persistence schema or order-management change was made.
- Final source/frozen replay Application SHA256: `6507D75279C262DE9EEE0D8D7B9B8CB509C063B73FE2A09DD93B810D5A527E6B`. All12Bankarchive comparisons rerun after extracting/optimizing the production policy; fill arrays match the pre-refactor candidate exactly. Final same-session replay computation ranged about0.1–6.4s, excluding archive loading; no new archive download or production subscription.
- Removed broad compression memory (cut older winners), compression/pivot relaxation (created a larger replacement loss), and the incomplete failed-extreme candidate that accepted an old, subsequently invalidated opposite break. No rejected branch, flag or temporary test remains. The final source additions are the production policy, its tests, Bank-only call/version, and internal reuse of the existing contiguous-session helper.
- Final evidence: `D:/AlgoTrading/.tools/entry-fixes-20261127/month/<date>/bank-auction-final.json`, compared with `bank-final.json`; `selection-<date>-auction-final.json` versus `selection-<date>-held.json`; frozen binary `bank-auction-final`. Prior original-policy results remain separately available.

**Latest bottom line:** both requested Bank losses now have a demonstrated local correction. Incremental modeled improvement is **₹20,853.11** over the previous accepted local fixes, with **zero tested winners missed**. Including the earlier12:34fix, today's Bank modeled result is −₹6,024.76 originally → +₹19,224.86 now. Nothing deployed. Partial historical coverage remains explicitly partial.

## Earlier retained entry fixes and evidence — prior continuation

### What changed

- **Nifty:** reuse the existing 20-bar range-like classifier (efficiency below .38 and direction flips at least .40) only inside `intraday-range-breakout-retest`. A small consolidation break inside that rotating structure must also clear the previous two completed five-minute bars with a direction-aligned completed close. The previous-one-bar pullback shortcut cannot substitute for that escape. Original eight-bar setup, opening-range strategies, EMA/VWAP pullbacks and normal directional-market entries are retained. No new time cutoff or universal chop veto.
- **Bank Nifty:** preserve the original five-minute confirmation level and close time. For a trade opposing retained market direction, subsequent completed minute closes must not have lost that level. Wicks alone do not invalidate it. A fresh accepted range break or existing completed breakout/retest can still qualify. This removes stale confirmation reuse rather than adding a fixed re-entry delay or requiring two same-colour five-minute candles.
- **Specific defect at 12:34:** the 12:25–12:29 bullish bar closed at 63,368.10 above its confirmed 63,357.25 level. The completed 12:31 minute then closed at 63,351.30, invalidating that break. The old code still reused the earlier confirmation for the 12:34 CE. The new code checks the intervening completed closes. This test is symmetric for CE and PE.
- Version receipts: Nifty adapter v19 / `nifty-range-escape-v1`; Bank shared context `shared-market-context-v2-confirmation-held`, incorporated into its existing momentum version. Confirmation metadata does not change Sensex's entry thresholds.

### 27 November — after-cost chronological model

These are modeled executable-book replay results, **not a rewrite of actual realised P&L**. Both arms use the same recorded quotes, costs, slippage, settings and full entry/exit chronology.

| Market | Baseline net | New net | Improvement | Losing entry avoided | Winners missed |
|---|---:|---:|---:|---|---:|
| Nifty | −₹3,643.04 | +₹9,442.25 | 🟢 +₹13,085.29 | 14:11 25700PE | 0 |
| Bank Nifty | −₹6,024.76 | +₹5,209.82 | 🟢 +₹11,234.58 | 12:34 63400CE | 0 |
| Combined | −₹9,667.80 | +₹14,652.07 | 🟢 +₹24,319.87 | Two entries | 0 |

Every other modeled position and individual fill remains identical, including partial exits. There are **no replacement/delayed positions**, no enlarged retained losses and no shortened winners in the compared paths. Today's Nifty 10:59 winner remains +₹9,442.25; Bank 10:31 and 11:16 winners remain +₹9,756.02 and +₹9,468.84. Bank's 11:48 and 14:10 losses remain −₹6,010.33 and −₹8,004.72: these two fixes do not solve those entries.

The actual ledger losses on the two avoided entries were Nifty −₹12,618.44 and Bank −₹10,473.08. Do not add those actual amounts to the modeled improvement. Replay entry times/contracts match today's two Nifty and five Bank entries, but modeled fills do not reproduce actual execution prices exactly.

### Every available archive date — separate after-cost results

All amounts are rupees. `=` means every individual fill is identical, not merely an equal aggregate. Partial rows show only the supported closed segment and must not be treated as completed whole-day evidence.

| Date | Nifty baseline → new | Bank baseline → new | Coverage |
|---|---:|---:|---|
| 09 Nov | 0 → 0 = | 0 → 0 = | Sparse 14:07+ archive; Bank opening universe unsupported; no validated full-day comparison |
| 11 Nov | 4,003.67 → 4,003.67 = | 17,644.35 → 17,644.35 = | Both modeled windows completed |
| 12 Nov | 7,323.63 → 7,323.63 = | 0 → 0 = | Nifty completed; Bank incomplete before first closed position |
| 13 Nov | 4,096.39 → 4,096.39 = | 8,737.13 → 8,737.13 = | Nifty completed; Bank partial |
| 17 Nov | 16,796.96 → 16,796.96 = | 15,500.15 → 15,500.15 = | Nifty completed; Bank partial |
| 18 Nov | −6,062.42 → −6,062.42 = | 3,955.98 → 3,955.98 = | Both completed |
| 19 Nov | 22,611.77 → 22,611.77 = | 11,132.56 → 11,132.56 = | Both completed |
| 20 Nov | 11,414.56 → 11,414.56 = | −15,968.22 → −15,968.22 = | Both completed |
| 23 Nov | 10,099.17 → 10,099.17 = | 0 → 0 = | Archive starts 09:30; Nifty window completed, Bank incomplete |
| 25 Nov | −11,979.13 → −11,979.13 = | −18,367.36 → −18,367.36 = | Nifty completed; Bank partial |
| 26 Nov | −9,227.13 → −9,227.13 = | −43,453.84 → −43,453.84 = | Both completed |
| 27 Nov | −3,643.04 → 9,442.25 | −6,024.76 → 5,209.82 | Both completed; combined +24,319.87 |

Across these supported execution paths: **79 baseline closed positions, 77 retained unchanged, two losing positions removed; all 46 winning positions retained unchanged** (Nifty 16, Bank 30). No added positions. The partial/unsupported segments are not evidence that future entries in those missing intervals would be unchanged.

### Whole-month actual-entry screen, 26 October–27 November

Compiled chronological candidate scans covered 24 calendar session dates, including the two no-trade dates (10/16 November). Matched against **208 Nifty/Bank actual ledger entries on 22 traded dates**. Current-baseline candidate direction matched 159 entries, including 83 winners; 49 older entries were already not reproduced by current-baseline selection and are not claimed as validated fills. Among the baseline-matched entries, the final candidate newly excludes only today's Bank 12:34 loser and retains all 83 winners.

Per-date screened entries / baseline-direction matches: Oct26 19/11; Oct27 15/8; Oct28 8/2; Oct29 13/6; Oct30 12/7; Nov02 9/7; Nov03 6/1; Nov04 6/5; Nov05 4/2; Nov06 5/4; Nov09 5/5; Nov11 12/11; Nov12 11/11; Nov13 7/5; Nov17 12/10; Nov18 9/9; Nov19 11/11; Nov20 17/17; Nov23 7/7; Nov25 5/5; Nov26 8/8; Nov27 7/7. Only Nov27 has a new exclusion in this screen.

This flat-inventory screen is not a P&L replay: it excludes subsequent admission/risk gates, cooldowns and fill execution. In particular, Nifty can still produce a different candidate at 14:11 in the flat scan; the full chronological engine rejects that alternative under unchanged later gates. The loss-avoidance claim comes from the full engine, not from treating a screened exclusion as money saved. October/early-November evidence remains candle/ledger screening; legacy Nov5/6 LTP records cannot establish executable bid/depth fills.

### Refinement and verification

- Removed broad Nifty five-minute filtering and distinct nine-bar retest experiments: the broader approaches threatened older winners; the nine-bar variant removed Nov20's +₹9,489.73 winner. Final code retains the original pattern and scopes the escape requirement to the short-range setup only.
- Removed Bank accepted-break-only/rotation/two-five-minute-colour experiments. One cut Nov26's +₹9,843.92 winner; the colour variant failed the Nov12 actual-winner screen (+₹5,695.68). Final confirmation-validity logic retains both. No rejected candidate branches, flags or tests remain in production source.
- Final backend checks: **1,033 unit + 479 contract + 3 architecture tests passed**. New mirrored CE/PE tests cover Nifty internal-range rejection versus true escape, unchanged non-Nifty behavior, Bank wick tolerance, completed-close invalidation, same-direction preservation and fresh confirmation recovery. Prior Sensex all-three fallback PostgreSQL integration remains separately verified; no database-dependent test was silently counted in this filtered run.
- Final Application SHA256: `6AA946E431636BF7DE2671644C403C5527EC27C6CF8AFC0529EF8DD98E4DC048`. Frozen final binary and tested build match. Final Nifty Nov27 fill-array parity with the month-batch candidate is exact. Bank final binary reran all 12 dates. Source diff whitespace check passed.
- Evidence: `D:/AlgoTrading/.tools/entry-fixes-20261127/month/<date>/nifty-targeted.json`, `bank-final.json`, corresponding `*-baseline.json`; Nov27 `nifty-final.json`; month screen `selection-<date>-held.json` and baseline files. Held screen binary precedes only version/diagnostic wording updates; final production behavior is the same. Frozen rejected binaries/results are research evidence outside release source, not disabled production logic.
- Standard replay window 09:27–15:29, with the two partial-start exceptions above. Native Groww index/futures candles and observed option books (including assisted provider records), identical cost model and 0.05% per-side slippage in both arms; explicit settings/prior-state assumptions, not original historical-policy parity. Final Nifty Nov27 load 22.2s / replay 8.1s. Reused valid cached inputs rather than repeating archive downloads.

**Prior-stage bottom line:** +₹24,319.87 in today's Nifty/Bank after-cost model before the additional Bank correction above. This section is retained to distinguish the earlier baseline from the latest incremental results. Keep local until deployment is explicitly authorized.

## Earlier investigation below — superseded entry verdict, retained evidence

## Actual session, not simulated

Final production ledger: 10 closed paper positions, no open positions, five wins/five losses; gross **−₹7,115.50**, costs **₹3,870.07**, net **−₹10,985.57**. No Hero Zero rows or confirmed live fills in the report.

| Index | Positions | Actual net after costs |
|---|---:|---:|
| Nifty | 2 | −₹6,490.27 |
| Bank Nifty | 5 | −₹3,446.81 |
| Sensex | 3 | −₹1,048.49 |

The Bank Nifty 10:31 and 11:16 PE winners have persisted 70% net-trail receipts. Later entries at 11:48 PE, 12:34 CE and 14:10 PE lost ₹6,776.86, ₹10,473.08 and ₹8,104.16 respectively. The Nifty 14:11 PE lost ₹12,618.44 at the unchanged hard stop. These are entry-quality questions, not proof that removing stop/failed-setup exits would improve the session.

## Sensex: confirmed defects and retained changes

1. **A missing confirmation could consume the decision candle.** The production loop marked the candle evaluated before checking futures confirmation. A subsequent valid import could not retry that candle. It now checks confirmation first, retries while the completed index candle is still current, and consumes it only when evaluation proceeds. Unclosed/expired index candles cannot be revived.
2. **Provider selection could change between database reads.** Spot and futures now use one captured provider identity within an evaluation. They are not combined across providers.
3. **Unavailable confirmation could advance directional memory.** Production now leaves the thesis untouched until confirmation is ready; historical thesis reconstruction follows the same rule.
4. **HTTP success could conceal stale native history.** For current, in-session minute-history requests, missing/stale completed candles now trigger the existing broker fallback. Both stale providers fail closed. Historical and after-hours requests are unaffected. Source tags remain intact.
5. **Zero participation and missing data had the same explanation.** Audit reasons now distinguish fresh zero volume, stale/missing candles, unavailable baseline/alignment and an expired index candle. This does not turn zero volume into an entry signal.

Evidence: end-session native histories contain 374 index bars per provider for all three. Sensex December futures have 248 Groww bars and 374 Upstox bars, including 127 Upstox zero-volume bars. In the 24 latest afternoon `DataIncomplete` decisions, 17 corresponding final Upstox bars have zero volume and seven positive volume. A positive bar present after close does **not** establish that it was available at the original decision. Five provider-volume disagreements are retained as evidence; no archived volume was overwritten or invented.

The data correction does not relax participation requirements or promise entries on illiquid futures. Its historical comparison preserves all tested Sensex trade/fill arrays; **no extra profit from late-arrival retries has been established**. Final candle history is not a receipt-time reconstruction of when each import became available.

## Entry/re-entry candidates — after costs

The candidates reused existing entry policies. All were removed from production source after testing; no disabled experimental switches remain.

### A. Causal EMA pullback reset — rejected

Compared each post-exit candle with its contemporaneous EMA instead of comparing old candles with the latest EMA. Nifty results were unchanged. Bank Nifty:

| Date | Baseline net | Candidate net | Difference |
|---|---:|---:|---:|
| Nov 11 | ₹17,644.35 | ₹5,470.99 | 🔴 −₹12,173.37 |
| Nov 17, incomplete segment | ₹15,500.15 | ₹13,610.48 | 🔴 −₹1,889.67 |
| Nov 20 | −₹15,968.22 | −₹6,151.60 | 🟢 +₹9,816.62 |
| Nov 27 | −₹6,024.76 | −₹6,024.76 | ⚪ ₹0 |

Other tested dates unchanged. Across completed compared windows, difference **−₹2,356.75**; the incomplete Nov 17 segment is excluded from that total.

Trade attribution:

- Missed Nov 11 10:38 CE winner: **₹12,173.37**.
- Nov 20 avoided 12:41 CE loss: **₹12,794.36**, but also removed 12:03 and 12:35 CE winners of **₹2,821.41** and **₹156.33**. Net benefit that day: ₹9,816.62.
- Incomplete Nov 17 segment also missed a 10:09 CE winner of ₹1,889.67; not a complete-session result.

### B. Opposing five-minute impulse confirmation — rejected

Deferred a local break inside a recent opposing impulse until the impulse origin was reclaimed. Nifty results were unchanged.

| Date | Bank baseline net | Candidate net | Difference |
|---|---:|---:|---:|
| Nov 20 | −₹15,968.22 | −₹9,696.70 | 🟢 +₹6,271.52 |
| Nov 27 | −₹6,024.76 | −₹17,575.95 | 🔴 −₹11,551.19 |

Other tested dates unchanged. Completed-window total difference **−₹5,279.68** (computed from unrounded results).

Nov 27 shows why counting only excluded losses is misleading:

| Changed trade | Baseline net removed | Replacement net |
|---|---:|---:|
| 11:16 PE winner missed | +₹9,468.84 | — |
| 11:48 PE loss removed | −₹6,010.33 | — |
| 12:34 CE loss removed | −₹11,234.58 | — |
| New 11:31 PE | — | −₹7,023.23 |
| New 12:00 CE | — | −₹12,304.03 |

The removed losing trades total ₹17,244.91, but a missed winner plus ₹19,327.26 of replacement losses makes the day worse. Existing 10:31 and 14:10 positions remain unchanged. Nov 20's improvement removes a 10:09 CE loss of ₹6,271.52 without a replacement.

### C. Fresh post-exit five-minute reset alternative — removed, no improvement

Tested a completed pause and range break formed entirely after the previous exit as an alternative confirmation. All **24 Nifty/Bank Nifty market-date comparisons across 12 archive dates** had identical trade arrays and P&L. Zero-trade or incomplete windows are not counted as successful trading validation. No demonstrable benefit, so no code retained.

## Per-session replay coverage and baseline values

These are **current-policy modeled results**, not reproductions of each historical day's deployed policy or actual ledger. Identical explicit lots, observed offer/bid books, costs, 0.05% per-side slippage and execution checks were used in both arms. Upstox-assisted observations retain their evidence limitations. No missing fills were fabricated. The simulation proceeds chronologically, including replacement entries and portfolio state.

Standard window starts 09:27 IST, after the observed opening contract universe, and ends at the available 15:29 boundary. Nov 9 is only a sparse 14:07+ window. Nov 23 starts 09:30 and has partial quote coverage. `P` means execution incomplete; the number is only the closed segment. `U` means unsupported opening universe. `Z` means a sparse zero-trade result, not full-session validation.

| Date | Nifty net | Bank Nifty net | Sensex net | Additional limitations |
|---|---:|---:|---:|---|
| Nov 9 | ₹0 Z | U | ₹0 Z | Sparse partial archives |
| Nov 11 | ₹4,003.67 | ₹17,644.35 | ₹2,538.18 | Modeled windows |
| Nov 12 | ₹7,323.63 | ₹0 P | −₹5,303.05 P | Bank/Sensex incomplete |
| Nov 13 | ₹4,096.39 | ₹8,737.13 P | −₹3,916.55 P | Bank/Sensex incomplete |
| Nov 17 | ₹16,796.96 | ₹15,500.15 P | ₹8,957.61 | Bank incomplete |
| Nov 18 | −₹6,062.42 | ₹3,955.98 | ₹5,190.58 | Modeled windows |
| Nov 19 | ₹22,611.77 | ₹11,132.56 | −₹4,322.91 | Modeled windows |
| Nov 20 | ₹11,414.56 | −₹15,968.22 | ₹6,845.96 | Modeled windows |
| Nov 23 | ₹10,099.17 | ₹0 P | ₹3,560.92 P | Partial archives; not full-session coverage |
| Nov 25 | −₹11,979.13 | −₹18,367.36 P | −₹9,269.70 | Bank incomplete |
| Nov 26 | −₹9,227.12 | −₹43,453.84 | −₹5,731.16 | Modeled windows |
| Nov 27 | −₹3,643.04 | −₹6,024.76 | −₹14,121.82 | Modeled windows, not actual ledger parity |

For every listed date, the retained Sensex thesis correction changes **₹0** and preserves its trade/fill array. The final removed reset candidate also changes ₹0 for Nifty/Bank. Earlier rejected-candidate differences are listed above. Full-array equality was checked for all 36 market-date pairs, rather than relying only on equal totals.

Earlier available ledgers/candles: Oct 26, 27, 28, 29, 30 and Nov 2, 3, 4, 5, 6. Their existing entry-screen evidence is in `entry-and-replay-audit-2026-11-26.md`. No new bid/ask execution comparison is established for these dates. Nov 5/6 legacy observations are LTP-only for the relevant paths. Nov 10/16 have no trades in the earlier ledger audit. The available month is explicitly accounted for; this is **not** a claim of a complete month of verified counterfactual fills.

Today's baseline matches the two Nifty and five Bank Nifty entry decision times/contracts, but not actual fill P&L. Sensex does not reproduce today's three actual entries under these native-history/explicit-settings inputs. Therefore neither the candidate net totals nor excluded modeled losses are actual money saved.

## Verification and evidence

- Focused checks: 40 unit tests, 10 contract tests, one real isolated PostgreSQL integration test passed.
- Integration covers all-three stale HTTP-success fallback, both histories stale, verified primary recovery, per-market routing and persistence, unchanged partial fills/order identity, and no live execution intents.
- Retry-boundary tests reject unclosed/expired index candles; confirmation tests preserve zero-volume and stale-data fail-closed behavior.
- Wider regression checks: 1,029 unit, 479 contract and three architecture tests passed; the real PostgreSQL integration test also passed separately (1,512 distinct passing tests across these checks).
- No strategy candidate remains in `StructuralReentryPolicy`, `BankNiftyEntryStructurePolicy`, `MiddayChopConfirmationPolicy`, `NiftyPreQuoteEntryGatePolicy` or `BankIndexPreQuoteGatePolicy`; their final substantive diffs are empty.
- Evidence root: `D:/AlgoTrading/.tools/entry-fixes-20261127/`. `daily.json`, `candles.json`, `sensex-confirmation-blocks.json`, immutable archived quote inputs, frozen baseline/candidate binaries and `month/<date>/*-baseline.json`, `bank-candidate.json`, `bank-impulse.json`, `*-reset.json`, `sensex-sensex-candidate.json` retain the individual results. Earlier initial-root runs with shorter history are superseded by the `month` inputs.

## Outstanding requirement

The Sensex technical corrections are local and tested, not deployed or operationally verified. **A profitable, winner-preserving Nifty/Bank Nifty entry/re-entry refinement has not been demonstrated.** It would be incorrect to label that part fixed or report the losing candidates as improvements.
