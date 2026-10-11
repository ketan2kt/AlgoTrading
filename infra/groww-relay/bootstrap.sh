#!/bin/sh
set -eu

umask 077
if ! grep -Eq '^GROWW_EXECUTION_RELAY_KEY=[0-9a-f]{64}$' /etc/caddy/relay.env 2>/dev/null; then
    printf 'GROWW_EXECUTION_RELAY_KEY=%s\n' "$(openssl rand -hex 32)" \
        > /etc/caddy/relay.env
fi

mkdir -p /etc/systemd/system/caddy.service.d
printf '[Service]\nEnvironmentFile=/etc/caddy/relay.env\n' \
    > /etc/systemd/system/caddy.service.d/override.conf

systemctl daemon-reload
set -a
. /etc/caddy/relay.env
set +a
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
systemctl restart caddy
systemctl is-active --quiet caddy
