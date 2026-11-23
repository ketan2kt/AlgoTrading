# Upstox execution integration — local implementation

Status: not deployed or operationally verified. No market has been armed and no order placed during setup.

## Routing and isolation

- Controls selects Groww or Upstox separately for Nifty, Sensex and Bank Nifty. New entries must be OFF before a broker change; selection leaves them OFF.
- Every durable live intent owns its broker and account permanently. Existing rows migrate to Groww/primary. Source/client-reference uniqueness remains global: the same paper trade cannot be submitted to both brokers.
- Existing live lifecycle manages broker-confirmed entry fills, protective SL, partial exits, trailing amendments, external exits and restart reconciliation. Broker acknowledgement alone is not SL confirmation. Unknown writes are never automatically resubmitted.
- Upstox has a separate worker so its outage cannot delay Groww position management. Unresolved exposure still blocks new entries across brokers. Paper strategy, sizing, stops, profit protection and recorder ownership are unchanged.
- The existing backup-data-session block on NEW live orders remains intact for both brokers. This release is broker selection, not automatic execution failover. Existing positions remain with their entry broker.

## Authentication and network prerequisites

- Free Sarthico Execution app created with approved terms and callback `https://sarthico.com/api/upstox-execution/callback`.
- App credentials stored using Windows user-bound encryption, separate from read-only Analytics credentials. No credentials belong in repository, chat or logs.
- Connect Upstox trading uses administrator authentication, antiforgery protection, short-lived browser-bound OAuth state and server-side code exchange. Account, product and exchange segments are checked. Tokens are encrypted in the existing database vault; no live switch is changed by authorization.
- Official execution tokens expire at the next 03:30 IST boundary. Controls shows expiry and reconnect status; read-only Analytics token is never substituted.
- The existing DigitalOcean relay's actual outbound IP174.138.120.226 was verified and registered with Upstox on23Nov. Additive Caddy routes were validated and gracefully reloaded; Groww routing remains unchanged. Only required read/order paths on pinned official upstream hosts are allowed, authenticated by the existing relay header. No automatic proxy retries or request/body access logging.
- Activate only after capture, required checks and approved website rollout. The setup helper verifies the checked Infrastructure DLL hash before changing website settings. Recorder settings remain untouched.

## Verification still required before live readiness

- Account/derivatives-segment verification and OAuth callback exchange using deployed code. The account profile currently requires the user's FATCA declaration; do not guess tax residency. Preserve all existing live switches.
- Added broker-payload/database fixture for weighted duplicate-deduplicated partial fills, cancel/fill races, confirmed stop and stop-fill cancellation race, restarted ownership, wrong-account rejection and global source uniqueness. Required PostgreSQL CI must pass. HTTP timeout/no-retry,429backoff,combined write budget,expiry and ownership-model checks pass locally.
- Sandbox execution is unsupported and rejected before network access; it cannot bypass static-IP verification or send to production. Never use real orders as an unattended test.
- Full required CI, authorized deployment and user-controlled broker test. Focused local tests are not broker acceptance proof.
- Orders over exchange freeze quantity fail closed; this initial implementation does not split one trade into multiple child orders. Live cost figures remain estimates from the existing paper charge schedule, not contract-note charges.

Official references: [OAuth token](https://upstox.com/developer/api-documentation/get-token/), [V3 orders](https://upstox.com/developer/api-documentation/v3/place-order/), [order fills](https://upstox.com/developer/api-documentation/get-trades-by-order/), [rate limits](https://upstox.com/developer/api-documentation/rate-limiting/).
