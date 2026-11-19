# 18 November: recorded-entry exit replay

All nine Nifty/Bank Nifty positions completed. Original ledger entry time, price and quantity are now retained; no synthetic entry quote or second entry slippage is applied. Subsequent partial/final sales use later saved sized bids, 0.05% exit slippage and modeled exchange charges.

This checks the **already-deployed exit policies**, not a newly improved strategy. The actual column is recorded ledger net. The replay column is modeled net, not an exact trade match: cadence, reconstructed original 10% stop/target, regime and historical inputs differ. Differences below must not be advertised as savings caused by this reporting release.

| Index / entry IST | Actual net | Replay net | Difference after costs |
|---|---:|---:|---:|
| Nifty 10:04 | -9,566.84 | -8,161.12 | +1,405.72 |
| Nifty 10:23 | -5,844.30 | +4,985.68 | +10,829.98 |
| Nifty 10:37 | -7,179.87 | -986.65 | +6,193.22 |
| Nifty 13:38 | -5,860.25 | -7,800.79 | -1,940.54 |
| Nifty 14:39 | -4,719.38 | -2,690.24 | +2,029.14 |
| Bank Nifty 09:27 | +15,099.06 | +14,342.89 | -756.17 |
| Bank Nifty 11:50 | +4,780.63 | +6,358.01 | +1,577.38 |
| Bank Nifty 12:46 | -7,099.34 | -7,021.64 | +77.70 |
| Bank Nifty 13:13 | -7,020.16 | -6,052.83 | +967.34 |

Amounts in rupees, rounded only for presentation. Bank Nifty cohort: actual +5,760.19 versus modeled +7,626.43; difference +1,866.24. Both winners remain winners; the first retains less profit. Two losses remain losses.

Do not sum the Nifty isolated rows as an executable day: the 10:23 PE remains open until 10:45, overlapping the actual 10:37 same-contract entry. The existing active-contract guard prevents that repeat. Separately, the deployed countertrend gate rejects the 10:04 CE; its exit-only row deliberately retains the actual entry to compare exits. The 13:38 loss worsens. No new strategy change is justified or included by this comparison alone.

## Evidence and scope

- Production-backed trade snapshot plus the final Nifty trade from authenticated Daily Analysis; saved Groww-native minute bars and Groww/Upstox option observations.
- Raw inputs and result: `D:/AlgoTrading/.tools/setup-boundary-20261118/recorded-entry-corrected.json` (contains prices, partials, fees, exit reasons, entry-gate classifications and application hash).
- All nine rows complete without unsupported entry/exit or partial execution in this modeled run. This is not a full replacement-entry session or proof of original-ledger parity.
- Earlier `recorded-*-baseline*` / `recorded-bank-two-close.json` exploratory artifacts used re-priced entries; superseded, not valid strategy improvement evidence.
- Strategy rules and recorder unchanged. On19Nov the user authorized deployment of all pending changes, including this recorded-entry audit correction; no new strategy improvement is claimed.
