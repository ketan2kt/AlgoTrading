# Upstox execution integration

Status: website deployment45204899531 succeeded for ada301f on23November2026 after required CI45204086818 passed. Recorder skipped. The SameSite=Strict callback correction is deployed. Production Controls verifies Upstox Authorized, including successful real relay order-book/position reads; authorization expires24Nov03:30IST. All three markets remain Groww with live entries OFF; no order placed. The resolved30Oct Groww intent was deleted on explicit user instruction with its original row retained in an audit; post-commit verification confirms zero unresolved execution intents. First real order/protective-stop lifecycle verification remains a user-controlled test.

## Routing and isolation

- Controls selects Groww or Upstox separately for Nifty, Sensex and Bank Nifty. New entries must be OFF before a broker change; selection leaves them OFF.
- Every durable live intent owns its broker and account permanently. Existing rows migrate to Groww/primary. Source/client-reference uniqueness remains global: the same paper trade cannot be submitted to both brokers.
- Existing live lifecycle manages broker-confirmed entry fills, protective SL, partial exits, trailing amendments, external exits and restart reconciliation. Broker acknowledgement alone is not SL confirmation. Unknown writes are never automatically resubmitted.
- Upstox has a separate worker so its outage cannot delay Groww position management. Unresolved exposure still blocks new entries across brokers. Paper strategy, sizing, stops, profit protection and recorder ownership are unchanged.
- Repeated account reads share at most one second of evidence. Every mutation invalidates that cache before sending, and a late pre-mutation response cannot restore it. Priced fills are reused for at most two minutes only while broker cumulative filled quantity is unchanged; additional/missing fills are fetched again. This avoids multiplying order-book/fill traffic by every open trade.
- The existing backup-data-session block on NEW live orders remains intact for both brokers. This release is broker selection, not automatic execution failover. Existing positions remain with their entry broker.

## Authentication and network prerequisites

- Free Sarthico Execution app created with approved terms and callback `https://sarthico.com/api/upstox-execution/callback`.
- App credentials stored using Windows user-bound encryption, separate from read-only Analytics credentials. No credentials belong in repository, chat or logs.
- Connect Upstox trading uses administrator authentication, antiforgery protection, short-lived browser-bound OAuth state and server-side code exchange. Account, product and exchange segments are checked. Tokens are encrypted in the existing database vault; no live switch is changed by authorization.
- The broker's cross-site callback validates the protected state and Lax browser nonce but only renders an inert same-site continuation form. It cannot exchange or store a token. Complete secure connection submits to the administrator-protected completion endpoint, which rechecks the original administrator and nonce before exchanging the single-use code. The administrator cookie remains SameSite=Strict; no-store/no-referrer/CSP prevent caching, external transmission or framing. The return flow is covered through the real HTTP authentication pipeline, including anonymous returns, expired/tampered state, missing nonce, wrong administrator and wrong role.
- Official execution tokens expire at the next 03:30 IST boundary. Controls shows expiry and reconnect status; read-only Analytics token is never substituted.
- The existing DigitalOcean relay's actual outbound IP174.138.120.226 was verified and registered with Upstox on23Nov. Additive Caddy routes were validated and gracefully reloaded; Groww routing remains unchanged. Only required read/order paths on pinned official upstream hosts are allowed, authenticated by the existing relay header. No automatic proxy retries or request/body access logging.
- Activate only after capture, required checks and approved website rollout. The setup helper verifies the checked Infrastructure DLL hash before changing website settings. Recorder settings remain untouched.

## Verification still required before live readiness

- India-only FATCA declaration saved with explicit factual confirmation and submission approval; NSEFO/BSEFO segments verified. Execution OAuth completed and real read-only order-book/position access through the relay verified. Preserve all existing live switches. Renew execution authorization after its03:30IST expiry before next-session use.
- Legacy30Oct Groww blocker removed by explicit user-authorized administrative deletion, not invented historical fills: target e40511dd-f968-49b9-829d-ef3d69f3e452 absent; audit679e0c04-278a-4cbd-9d88-328210317890 retains its complete original row. Seven other intents remain (sixRejected/oneClosed). Broker orders and global unresolved-exposure safety guard unchanged.
- Broker-payload/database fixtures cover weighted duplicate-deduplicated partial fills, cancel/fill races, confirmed stop and stop-fill cancellation race, restarted ownership, wrong-account rejection and global source uniqueness. Required PostgreSQL CI passed, including authorization relay success/failure checks. HTTP timeout/no-retry,429backoff,combined write budget,expiry and cache invalidation checks passed.
- Sandbox execution is unsupported and rejected before network access; it cannot bypass static-IP verification or send to production. Never use real orders as an unattended test.
- Full required CI and authorized website deployment passed. User-controlled broker order test remains separate; no real order was submitted by this task.
- Orders over exchange freeze quantity fail closed; this initial implementation does not split one trade into multiple child orders. Live cost figures remain estimates from the existing paper charge schedule, not contract-note charges.

Official references: [OAuth token](https://upstox.com/developer/api-documentation/get-token/), [V3 orders](https://upstox.com/developer/api-documentation/v3/place-order/), [order fills](https://upstox.com/developer/api-documentation/get-trades-by-order/), [rate limits](https://upstox.com/developer/api-documentation/rate-limiting/).
