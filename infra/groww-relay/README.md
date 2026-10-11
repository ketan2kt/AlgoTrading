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
