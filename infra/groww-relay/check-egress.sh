#!/bin/sh
# Read-only: run on the relay host. No credentials or broker requests are used.
set -eu
printf 'IPv4 route (local source may differ from public NAT address):\n'
ip -4 route get 1.1.1.1
printf '\nPublic outbound IPv4 (compare with Groww API Keys allowlist):\n'
curl -4 --fail --silent --show-error --max-time 10 https://api.ipify.org
printf '\n\nGroww IPv4 route:\n'
groww_ip=$(getent ahostsv4 api.groww.in | awk 'NR == 1 { print $1 }')
test -n "$groww_ip"
ip -4 route get "$groww_ip"
