# 26 November 2026 — retained trading improvements and replay evidence

## Decision

Retain two local, production-intended improvements: Bank Nifty continuation-qualified earlier profit protection, and Sensex failed-breakout entry confirmation with opposite-side replacement protection. No deployment, orders, arming changes, recorder changes or capture interruption occurred. Nifty and Hero Zero trading rules are unchanged.

These address two demonstrated problems, not every losing trade. The broader entry-quality problem remains open for the other losses; their later price paths do not justify widening stops or disabling FailedSetupExit.

## Actual day, independently checked

Production daily diagnostics generated at 12:09 UTC: 15 closed paper rows (13 ordinary trades and two Sensex Hero Zero legs), gross **−₹1,279.00**, costs **₹5,473.20**, net **−₹6,752.20**. Nifty −₹8,470.97; Bank Nifty +₹5,512.93; Sensex including HZ −₹3,794.16. No open positions or live fills in this ledger.

### All trades: what happened and what changes

Times below are entry times in IST. Peaks are observed **gross** profits, not executable net profits.

| Market/time | Actual net | Gross peak | Finding / action |
|---|---:|---:|---|
| Nifty 10:43 CE | +6,179.48 | 9,912.50 | Existing 70% protection did operate. Stop 264.125, fill 263.65; ₹308.75 gross fill difference plus ₹450.52 costs explains the lower net. Unchanged. |
| Nifty 13:58 PE | −14,650.45 | 3,315.00 | Established downswing followed by a reversal. Peak only 0.237R, below protection activation. Premium subsequently 162.70/140.60 after 30/60 minutes versus 214.60 entry. This was not a stopped-out winner that then recovered. Hard stop unchanged. |
| Bank Nifty 10:49 CE | −7,237.80 | 4,807.50 | Noisy-chop entry. Narrow continuation-qualified protection does not activate. Loss unchanged. |
| Bank Nifty 11:06 PE | +9,988.44 | 15,744.00 | Existing 70% net protection and 60-unit partial operated. Recorded maximum floor ₹10,000.669; remaining 90 units exited near it. Preserve this winner. |
| Bank Nifty 11:55 CE | −611.16 | 4,260.00 | Opposing-context escape entry. New rule does not qualify; unchanged. |
| Bank Nifty 13:00 CE | −9,085.03 | 360.00 | Directional entry despite 22% efficiency / 72% flips. FailedSetupExit reduced exposure into further premium decline. A blanket flip veto also rejects the winning 13:50 PE, so not retained. |
| Bank Nifty 13:50 PE | +6,156.22 | 13,965.00 | Peak 0.722R never reached old 0.75R 70% activation. New continuation-qualified protection improves modeled exit net to **8,731.26**. |
| Bank Nifty 14:24 CE | +6,302.26 | 14,085.00 | Peak 0.639R never reached old activation. New protection improves modeled exit net to **8,091.32**. |
| Sensex 10:03 CE | −9,218.92 | 855.00 | Early-pullback fallback accepted a bounce after the 10:00 breakout was fully erased at 10:01, with no reclaim. New entry confirmation rejects this setup. Full-session model waits until 10:24 rather than blindly buying the bounce or immediately fading it. |
| Sensex 10:42 CE | +9,128.33 | 9,405.00 | Target booking worked; subsequent premium was lower. Actual-entry screen retains it; full-session comparison retains its corresponding winner exactly. |
| Sensex 12:00 PE | −68.43 | 3,215.00 | Break-even protection worked approximately as designed: 785.20 stop versus 784.45 fill. Unchanged. |
| Sensex 12:52 PE | +2,209.27 | 4,945.00 | Existing 50% gross lock exited at 798.55 versus 798.875 stop. Later continuation merits runner review; tightening every trail would not solve that. No exit change retained. |
| Sensex 14:05 PE | −6,789.04 | 1,130.00 | FailedSetupExit; premium subsequently 644.65/593.75 after 30/60 minutes versus 753.20 exit. Keeping the failed position longer was not supported. Unchanged. |
| Sensex HZ 14:38 CE | +1,877.90 | 2,530.00 | Managed with PE as one basket, not a standalone call. |
| Sensex HZ 14:38 PE | −933.27 | — | Pair debit ₹3,888; net **+₹944.63**. Basket peak net ₹1,529.53, trigger floor ₹1,147.1475; actual exit below trigger by ₹202.52. No HZ change; leg peak is not basket profit. |

## 1. Bank Nifty: earlier protection only after continuation proves itself

Cause: the two late winners remained below **0.75 of their original risk (R)**. The 70% net trail therefore never armed; the older 0.50R/50%-gross stop was all they had. This was an activation gap, not an armed trail disappearing.

New shared production/replay rule:

1. Two fresh bid observations establish at least +0.50 original R.
2. After that lock, underlying price must pull back and then produce a **completed candle close beyond the pre-pullback extreme**, mirrored for CE/PE.
3. That confirmed continuation can arm the existing **70% confirmed net-profit trail** before +0.75R. Initial impulse retracements alone do not qualify. Existing +0.75R activation remains.
4. Floors only ratchet upward. No future/incomplete candles or stitched candle gaps. Context and floor commit together; restart restores context but never invents a fresh two-quote confirmation. Existing hard stop, reversal exit, partial quantities and live reconciliation remain unchanged.

The 70% floor refers to confirmed bid-based net profit including booked partials and costs, not 70% of a single observed gross peak. Actual bid at exit is used, not a fabricated fill at the floor.

### Fixed-entry, recorded-management comparison

Original entries, quantities, standing-stop receipts, partial fills and terminal exits are held fixed. The candidate can exit earlier using saved fresh bids and the production cost model. **46 current-era trades reproduce their actual after-cost baseline**; compiled candidate changes only the two late November 26 exits. This is an exit-path comparison, not a claim that every historical entry was independently regenerated.

| Session | Trades | Baseline net | Candidate net | Difference | Evidence |
|---|---:|---:|---:|---:|---|
| Nov 9 | 2 | −1,953.99 | −1,953.99 | 0.00 | Partial feed samples; two additional trades have no path |
| Nov 11 | 10 | −5,241.23 | −5,241.23 | 0.00 | Older feed-sample model, not actual-ledger parity |
| Nov 12 | 10 | +2,628.04 | +2,628.04 | 0.00 | Older feed-sample model, not actual-ledger parity |
| Nov 13 | 5 | −24,572.54 | −24,572.54 | 0.00 | Older feed-sample model |
| Nov 17 | 8 | +10,402.40 | +10,402.40 | 0.00 | Quote receipts; actual baseline matched |
| Nov 18 | 4 | +5,760.19 | +5,760.19 | 0.00 | Quote receipts; actual baseline matched |
| Nov 19 | 7 | +10,415.03 | +10,415.03 | 0.00 | Quote receipts; actual baseline matched |
| Nov 20 | 11 | +28,683.47 | +28,683.47 | 0.00 | Quote receipts; actual baseline matched |
| Nov 23 | 6 | +48,426.69 | +48,426.69 | 0.00 | Quote receipts; actual baseline matched |
| Nov 25 | 4 | −34,076.78 | −34,076.78 | 0.00 | Quote receipts; actual baseline matched |
| Nov 26 | 6 | +5,512.93 | **+9,877.03** | **+4,364.10** | Quote receipts; actual baseline matched |

🟢 13:50 PE: +₹2,575.04; exit 13:56:12.798 instead of 13:56:41.428. 🟢 14:24 CE: +₹1,789.06; exit 14:46:39.587 instead of 14:52:41.436. Other 44 receipt-based trades unchanged, including the 11:06 winner and all six Nov 23 trades. No tested winner reduced or loser enlarged/extended in this comparison.

Replacement checks: no new completed candle exists in the first earlier-exit interval. The compiled Bank entry policy produced **zero candidates at each 14:47–14:52 close** in the second newly flat interval, using actual preceding closed-trade state. Separate chronological shared-engine runs on Nov 11/12/13/17/18/19/20/25/26 had identical full trade/fill arrays before/after. Those runs use explicit reconstructed settings and do not reproduce the actual ledger; Nov 12/13/17 have incomplete executions. Their unchanged results are regression evidence, not additional claimed profit.

🔴 Rejected: simply arming 70% at +0.50R cut the 11:06 winner by ₹4,292.95 and the late CE by ₹239.54. Earlier +0.30R evidence already cut Nov 23 winners by ₹6,790.15. Neither blanket change is in production source.

## 2. Sensex: confirm a rebuilt setup, not the first bounce or automatic opposite trade

At an inefficient range edge, a directional eight-bar breakout fully erased by the next close must reclaim its extreme or establish an accepted post-failure pivot before the early-pullback fallback can enter. The opposite range-fade path must also close beyond the failed candle in its new direction while that auction remains unresolved. This is not a time ban or a blanket gap-day veto.

Reuse the existing momentum minimum **0.45 ATR directional body** so tiny-body/gap-only prints do not become false failed-breakout events. Reuse existing efficiency/range-edge thresholds and two-close/pivot acceptance. Stops, sizing, HZ, profit protection, and other strategy paths are unchanged.

All-session actual-entry screen: **170 Sensex ledger rows** since Oct 26; **34 applicable entries, all 17 positive applicable entries retained**. Using only completed regular-session candles from 09:15, the final rule flags Oct 30 09:33 (actual −₹7,041.15), Nov 20 10:55 (actual −₹6,101.95) and Nov 26 10:03 (actual −₹9,218.92). These excluded amounts are **not** themselves claimed replay savings. No applicable entry lacked the required candle window. The Oct 30 opposite-range entry is screening-only; replacement-entry performance is not established for that date.

### Chronological session comparison, after costs

Shared entry/admission/exit policies, native Groww spot candles, recorded futures volume, observed option bids/offers, identical 100-unit quantity and 0.05% each-side slippage assumptions in both arms. Original prior-session thesis/settings are not fully reconstructed: neutral prior thesis and explicit configuration are used. Full displayed-size execution is not required in this paper-quantity model. Therefore these are **modeled session P&Ls, not actual daily P&Ls**. Historical volume-only compatibility data is never used as futures prices; the existing study's price-independence checks passed.

| Session | Before | After | Difference | Result |
|---|---:|---:|---:|---|
| Nov 11 | +2,329.43 | +2,329.43 | 0.00 | ⚪ All four positions unchanged |
| Nov 12 | +1,555.30 | +1,555.30 | 0.00 | ⚪ Closed segment only; later entry unresolved |
| Nov 13 | −511.00 | −511.00 | 0.00 | ⚪ Closed segment only; later entry unresolved |
| Nov 17 | +9,318.97 | +9,318.97 | 0.00 | ⚪ All six positions unchanged |
| Nov 18 | +8,605.34 | +8,605.34 | 0.00 | ⚪ All nine positions unchanged |
| Nov 19 | +4,641.51 | +4,641.51 | 0.00 | ⚪ All five positions unchanged |
| Nov 20 | −8,817.26 | **−3,180.51** | **+5,636.75** | 🟢 One losing entry removed; seven others identical |
| Nov 25 | −1,348.34 | −1,348.34 | 0.00 | ⚪ All seven positions unchanged |
| Nov 26 | −21,742.70 | **−5,731.16** | **+16,011.54** | 🟢 Includes delayed/replacement entries, not just excluding losses |

November 26 model: 10:03 CE −₹9,143.92 becomes confirmed 10:24 CE +₹4,597.34; 10:42 CE winner +₹8,268.97 stays identical; 10:56 CE −₹6,905.49, 11:56 PE −₹4,963.28 and 14:02 PE −₹8,549.89 stay identical. The 12:40 PE −₹449.08 becomes 12:52 PE +₹1,821.20. No modeled winner cut; no remaining modeled loser enlarged/extended. Realized-sequence drawdown improves Nov 20 ₹13,464.61→₹7,827.86 and Nov 26 ₹21,742.70→₹18,597.47. These are not mark-to-market drawdowns.

🔴 The first early-entry-only version worsened today's full model by ₹4,469.85: a replacement put activated the existing direction-change cooldown and blocked the 10:42 winning call. A second version misclassified a tiny Nov 18 candle and cut a ₹4,030.40 winner. Both flaws were corrected and the full comparisons rerun; no rejected flags or alternative policy branches retained.

## Historical coverage and limits that affect the decision

- Oct 26/27/28/29/30 and Nov 2/3/4: actual ledgers and causal underlying entry screening available, but no local usable option bid path for this new trail comparison. Do not label these full strategy replays. Final Sensex screen blocks only the Oct 30 losing range-fade entry in this group; no historical savings are assigned to it.
- Nov 5/6: recovered CRC-valid members of older compressed archives; respectively 830/1,496 Bank option LTP observations, **zero qualifying bid-depth observations** in those holding windows. LTP is not substituted for bid proof.
- Nov 9: partial Bank paths only. Nov 10/16 daily ledgers have no trades. Nov 24 is outside the traded-session set. Nov 23 provides Bank holding-window paths and Sensex entry screening, not a complete Sensex full-session comparison.
- Nov 11–20: reused valid local raw archives. Three corrupt shards excluded (Nov 11 09:17 UTC; Nov 13 05:57 and 08:15 UTC); unresolved fills remain explicitly incomplete, not zero-profit complete days.
- Nov 25/26: downloaded current private website quote archives read-only, including management receipts. The website spool retains these two days. Direct central-blob listing with the existing Azure sign-in lacks Blob Data Reader; no roles, storage keys or production configuration were changed.

## Verification and reproducibility

- Final shared-policy application SHA256: `5EC01EAE03AF83099CAB3121748E6E14694CA025EF870401393152B31E6AB78A`.
- Release replay tool and backend builds pass. **1,007 unit tests +475 contract tests pass**; focused real PostgreSQL persistence/rollback test **1/1 passes**. Loopback test instance stopped afterward. Required deployment CI has not been dispatched.
- Tests cover mirrored CE/PE, normal pullback, completed continuation, wick-only breaks, entry/future candles, gaps, tiny bodies, reclaimed/pivot acceptance, floor ratcheting, restart quote confirmation and atomic persistence rollback. Existing stop/reversal/partial contracts remain intact.
- Raw evidence: `D:/AlgoTrading/.tools/session-analysis-20261126/`. `daily.json` is the valid final report; `management-input.json`, `compiled-policy-receipt-results.json`, `bank-fixed-entry-comparison.json`, `comparison-summary.json`, `sensex-entry-screen-results.json`, `sensex-full-before.json`, `sensex-full-final.json`, `sensex-25-before.json`, `sensex-25-final.json`, and `history/sensex-*-before.json` / `*-final.json` retain inputs/results. Older rejected result files are evidence only, not release inputs.
- Exploration used existing compiled replay tooling plus in-memory invocations. No new experimental CLI or disabled candidate was added. Preserve unrelated pre-existing local research files and execution documentation.

**Bottom line:** two targeted positive changes are implemented locally. Bank Nifty's observed-entry profit retention improves by ₹4,364.10 today; Sensex's chronological model improves on Nov 20 and 26 with tested winners retained. Do not combine these different comparison bases into a claimed live daily profit. Other losing entries and Sensex runner giveback are not declared fixed by this batch.
