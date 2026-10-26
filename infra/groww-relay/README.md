# Groww execution relay

This relay gives Groww live-execution traffic a stable DigitalOcean egress IP
without moving the Sarthi application, database, charts, market-data feeds, or
paper trading away from Azure.

The relay:

- terminates public TLS with Caddy;
- requires `X-Sarthi-Relay-Key` on every proxied request;
- allows only Groww TOTP authentication plus the order, advanced-order, and F&O
  position routes used by `GrowwBrokerGateway`;
- strips the relay key before forwarding to Groww;
- has no automatic retry layer, preserving the gateway's reconciliation rules;
- does not enable Caddy access logging, so bearer tokens and order bodies are
  not written to access logs.

Production configuration keeps `Groww:ApiBaseUrl` pointed directly at Groww for
read-only market data and sets only `Groww:ExecutionApiBaseUrl` to the relay.
The shared relay key is stored in the Droplet service environment and the Azure
App Service environment, never in source control.

When `GROWW_TOTP_API_KEY` and `GROWW_TOTP_SECRET` are configured in Azure, the
application generates the short-lived TOTP code and obtains the daily Groww
access token through this relay. Groww's official cURL documentation still
requires daily approval on the Cloud API Keys page; the automation removes the
daily token copy/paste but does not bypass that account approval.

The current DigitalOcean Reserved IPv4 is `174.138.120.226`; the TLS hostname
is `relay.174-138-120-226.nip.io`.

## Investigating order authorization failures

A Reserved IP used to reach the relay is not proof of its outbound IP.
`bootstrap.sh` does not configure outbound routing. DigitalOcean requires
[outbound routing configuration](https://docs.digitalocean.com/products/networking/reserved-ips/how-to/outbound-traffic/)
to send traffic through a Reserved IP. The running host may have been configured
separately; verify before changing it.

Run `sh check-egress.sh` on the relay host and compare the public outbound IPv4
with the Groww API Keys allowlist. This only reads networking state and calls a
public IP lookup service; it does not submit an order or read credentials.
Check IPv6 routing separately if the host resolves and connects to Groww over IPv6.
Do not assume the incoming TLS hostname identifies the address Groww sees.

Before the next manual live attempt, verify the daily Groww approval, current
token status, and the execution base URL configured in the application. TOTP
changes token generation, not IP allowlisting. Working quotes and positions do
not prove authorization to create orders.

Execution errors now identify the HTTP operation and direct/relay route without
including tokens, order identifiers, or raw broker responses. A failure at
`GET /v1/order/status/reference/{reference}` occurs before order submission;
`POST /v1/order/create` identifies the create call. No automatic order retry was
added. After an ambiguous outcome, reconcile the broker order before trying again.
