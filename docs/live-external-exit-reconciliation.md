# Groww manual-exit reconciliation

Implementation, 23 November 2026. Website-only release authorized; required CI/deployment pending. Not broker-validated.

## Behaviour

- Nifty, Sensex and Bank Nifty use the existing live execution worker. Default account reconciliation interval is five seconds. A separate account-only Groww order/position stream provides debounced wake-up hints; REST order, position and trade reads remain authoritative. Disconnects back off and polling continues. No market-data/recorder subscription is changed.
- Read the complete paginated F&O order list and signed NRML positions. An absent/malformed array is an error, never proof that a position is flat. Confirm actual manual sell execution prices from paginated trade IDs, not LTP or paper prices.
- Reconcile before copying paper lifecycle actions. Persist an external-reconciliation hold before modifying the broker stop. A full confirmed manual exit cancels the positively identified linked SL and closes the live intent; a partial exit resizes that SL to the actual remainder. Read the account again to detect competing fills or changed quantities.
- While a manual order is partially filled/working, resize the stop to the confirmed remainder and keep automated actions held. A pending, unfilled manual sell also holds automation. Once terminal fills and quantities agree, persist the execution journal and clear the hold. Failed pricing reads leave the hold durable even if stop cancellation/resizing succeeded.
- Following a manual reduction, do not copy subsequent paper add-ons or partial booking. Continue managing the remaining SL (never loosen it) and the strategy's final exit. A new source ID may still generate a later valid trade; the existing unique source ID prevents reopening this same trade.
- Recheck available exposure before a fresh add-on, partial sell or stop amendment. Final exits retain existing cancel/confirm/quantity checks. Restart recovery can reconcile a manual exit that occurred before a pending system final sell was submitted; an uncertain/already-working system sell is not treated as absent.
- Same-contract ownership ambiguity, conflicting/manual-plus-stop fills, short exposure, or a missing/cancelled stop on a remaining position hold automation and require reconciliation. No guessed allocation, unsolicited corrective buy, or cancellation of unrelated orders. Broker-side actions can race between REST observations; these outcomes are flagged, not reported as a clean closure.

## Accounting and isolation

The live intent stores cumulative external quantity, actual order-level weighted fill prices and a durable pending flag. The additive migration defaults existing rows to zero/false/null. Existing optimistic concurrency and source/reference uniqueness remain in place.

Daily Analysis includes confirmed external fills in live gross P&L and estimated costs. It labels broker-side intervention and excludes the result from matched strategy execution comparisons. Paper positions, paper P&L, strategy decisions, risk settings and historical replay inputs are not rewritten. The live-protection monitor compares the verified stop against actual remaining live units and labels a confirmed external reduction explicitly.

## Verification and release gates

Local fake-broker tests cover all-three full closures, partial/working fills, duplicate notifications, two manual orders, cancellation timeout/restart, price-ledger delay, stop-fill races, same-contract ambiguity, in-flight system sells, failed durable writes, P&L separation, account pagination, IST parsing, null trigger prices and missing/foreign evidence. Additive migration SQL and EF model snapshot are checked without a database. A PostgreSQL restart-persistence integration test is included for required CI.

Before release: run required CI including PostgreSQL tests; deploy only after capture ends and with explicit authorization. Verify account-stream authentication/subscriptions and a controlled broker lifecycle separately before calling this operationally proven. No actual broker orders, credentials, arming settings, production data or capture processes were changed during local implementation.

Official API references: [orders](https://groww.in/trade-api/docs/curl/orders), [positions](https://groww.in/trade-api/docs/curl/portfolio), [account notifications](https://groww.in/trade-api/docs/python-sdk/feed). The published official SDK source was inspected in memory for account socket authentication and subscription subjects; no SDK or credentials were installed/copied into the repository.
