# Sensex local-resumption correction — 18 November 2026

## Decision

Keep the existing exits. Correct the persistent-directional entry gate instead:
an hour-old favourable move must not override a currently stalled setup.
After the existing opening-hour exemption, require either one ATR of favourable
five-minute progress, or two completed closes beyond the preceding three-bar
swing. A single breakout poke is not acceptance. This reuses the shared Sensex
gate in production and replay; other Sensex entry paths, Nifty, Bank Nifty,
hard stops, size, partial booking and profit protection are unchanged.

## Evidence

Production-backed exports contain 27 regular Sensex entries on November 12, 13,
17 and 18, including 13 actual winners. The new gate retains all 13 winners.
It newly defers November 17 at 10:28: five-minute progress was **-31.90 points**,
but the old rule admitted the call because the preceding hour was **+313.47**.
That trade actually lost **₹5,056.13 after costs**. The proposed gate remains
closed through the original 10:32:45 exit; first local eligibility returns at
10:45. Eligibility alone does not create a replacement trade.

The November 12 13:24 rejection already existed in v1. It is not credited to
this correction. All seven November 18 entries are unchanged.

### Same-entry modeled comparison, after costs

| Date | Supported paths / recorded entries | Current exit net | With admission correction | Difference |
|---|---:|---:|---:|---:|
| November 12 | 1 / 6 | -₹6,166.18 | -₹6,166.18 | ₹0 |
| November 13 | 1 / 4 | -₹5,547.38 | -₹5,547.38 | ₹0 |
| November 17 | 10 / 10 | ₹4,456.36 | ₹9,948.49 | **+₹5,492.13** |
| November 18 | 7 / 7 | ₹3,345.37 | ₹3,345.37 | ₹0 |

This applies the new as-of admission decision to the existing shared-exit replay
of original entry prices, times and units. Rejected entries contribute no fill
or costs; retained paths are unchanged. Initial stops/targets are reconstructed
at 10%; execution uses saved sized books, observed backup quotes, 0.05% modeled
exit slippage and BSE costs. This is a fixed-entry cohort comparison, **not** a
replacement-entry full-session portfolio or a claim that actual P&L would equal
the modeled total. The eight unsupported older exit paths are excluded from
both net totals. They remain included in the 27-entry admission scan.

## Rejected changes — removed, not shipped

- Earlier unconditional partial booking: reduced important winners.
- Faster structural failure exit: worsened the supported comparison.
- Giveback-only partial booking: cut the largest winners; adding existing
  non-trending classification produced no change on the recent sessions.
- Sensex 70% minimum lock from the first trigger: worsened both November 17
  and 18, including cutting the November 18 12:27 modeled winner from
  ₹5,556.07 to ₹1,926.43. The current Sensex trail remains unchanged.
- Applying Nifty's existing countertrend gate rejected none of these entries.

## Reproducible local evidence

Folder: `D:/AlgoTrading/.tools/setup-boundary-20261118`.
Inputs: `trades-2026-11-{12,13,17,18}.json`, `chart-inputs.json`, archived quotes.
Results: `sensex-recorded-baseline-{12,13,17,18}.json` and
`sensex-confirmed-resumption-{12,13,17,18}.json`.
The existing local recorded-entry tool retains its separate replay-correction
scope and is not included in this trading release.

Focused checks: 46 shared Sensex admission/exit/replay unit tests passed.
Mandatory CI remains required before deployment.
