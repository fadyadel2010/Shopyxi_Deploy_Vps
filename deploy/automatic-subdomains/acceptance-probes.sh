#!/usr/bin/env bash
set -euo pipefail

origin_ip=${ORIGIN_IP:-207.231.111.200}
canonical_host=${CANONICAL_HOST:-ahram.shopyxi.com}
probe_label=${PROBE_LABEL:-wildcard-probe-20261111}
probe_host="$probe_label.shopyxi.com"
multi_label_host="x.$probe_host"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for command in curl dig; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "required command is missing: $command" >&2
    exit 64
  }
done

request() {
  local name=$1
  local expected=$2
  shift 2
  local headers="$work/$name.headers"
  local body="$work/$name.body"
  local status
  status=$(curl --silent --show-error --connect-timeout 10 --max-time 45 --dump-header "$headers" --output "$body" --write-out '%{http_code}' "$@")
  if [[ "$status" != "$expected" ]]; then
    echo "$name: expected HTTP $expected, received $status" >&2
    exit 65
  fi
  echo "$name: HTTP $status"
}

assert_no_store() {
  local name=$1
  if ! grep -Eiq '^cache-control:.*no-store' "$work/$name.headers"; then
    echo "$name: Cache-Control no-store is missing" >&2
    exit 66
  fi
  if grep -Eiq '^cf-cache-status:[[:space:]]*HIT' "$work/$name.headers"; then
    echo "$name: Cloudflare unexpectedly served a cached response" >&2
    exit 67
  fi
}

wildcard_answers=$(dig +short A "$probe_host")
if [[ -z "$wildcard_answers" ]]; then
  echo "wildcard DNS did not resolve $probe_host" >&2
  exit 68
fi
if grep -Fxq "$origin_ip" <<<"$wildcard_answers"; then
  echo "wildcard DNS is not proxied by Cloudflare" >&2
  exit 69
fi
# DNS wildcard synthesis can legitimately answer more than one label. DNS is
# neither hostname grammar nor tenant admission authority. Multi-label denial is
# proven against Traefik below, not by requiring an incorrect DNS NXDOMAIN.
echo "wildcard DNS: proxied resolution confirmed; routing/TLS enforce label depth"

redirect_status=$(curl --silent --show-error --connect-timeout 10 --max-time 45 --output /dev/null --write-out '%{http_code}' "http://$canonical_host/")
if [[ "$redirect_status" != "301" && "$redirect_status" != "302" && "$redirect_status" != "307" && "$redirect_status" != "308" ]]; then
  echo "canonical HTTP redirect failed: $redirect_status" >&2
  exit 71
fi
echo "canonical redirect: HTTP $redirect_status"

request canonical-root 200 "https://$canonical_host/"
request canonical-bootstrap 200 "https://$canonical_host/api/storefront/store-config"
assert_no_store canonical-bootstrap

request unknown-tenant 404 "https://$probe_host/api/storefront/store-config"
assert_no_store unknown-tenant

request canonical-health-private 403 "https://$canonical_host/health/live"
assert_no_store canonical-health-private
request canonical-registration-private 403 \
  --request POST \
  --header 'Content-Type: application/json' \
  --data '{}' \
  "https://$canonical_host/api/platform/register"
assert_no_store canonical-registration-private
request assurance-health-private 403 'https://app82-assurance-a.shopyxi.com/health/live'
assert_no_store assurance-health-private

# Direct-origin TLS validation proves the wildcard origin certificate is valid
# for an arbitrary one-label host without bypassing certificate verification.
request direct-origin-wildcard-tls 404 \
  --resolve "$probe_host:443:$origin_ip" \
  "https://$probe_host/api/storefront/store-config"
assert_no_store direct-origin-wildcard-tls

# Route grammar rejects malformed and multi-label hosts. -k is intentional here:
# a single-label wildcard certificate must not cover the multi-label test host.
request direct-origin-multi-label 404 \
  --insecure \
  --resolve "$multi_label_host:443:$origin_ip" \
  "https://$multi_label_host/"
request direct-origin-malformed 404 \
  --insecure \
  --header 'Host: -bad.shopyxi.com' \
  "https://$origin_ip/"

echo 'automatic-subdomain acceptance probes: PASS'
