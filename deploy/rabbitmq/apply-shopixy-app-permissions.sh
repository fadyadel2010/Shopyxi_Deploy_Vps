#!/usr/bin/env bash
# Apply the canonical RabbitMQ permissions for the Shopyxi application user.
#
# This is an infrastructure policy deployment, not an application-image rollout.
# It is intentionally idempotent: running it repeatedly converges the broker to
# the committed policy and exits without changing anything when already correct.
#
# Prerequisites:
#   - kubectl access to the shopixy-messaging namespace
#   - curl not required; Python 3 is used for the management API
#   - the broker management listener on TLS port 15671
#
# Usage:
#   ./apply-shopixy-app-permissions.sh [policy.json]
#
# The policy file defaults to shopixy-app-permissions.json next to this script.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DRY_RUN=0
if [[ "${1:-}" == "--dry-run" ]]; then
  DRY_RUN=1
  shift
fi
POLICY_FILE="${1:-${SCRIPT_DIR}/shopixy-app-permissions.json}"

if [[ ! -f "${POLICY_FILE}" ]]; then
  echo "ERROR: policy file not found: ${POLICY_FILE}" >&2
  exit 2
fi

command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl is required" >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 is required" >&2; exit 2; }

ADMIN_USER="$(kubectl -n shopixy-messaging get secret shopixy-rabbitmq-default-user -o jsonpath='{.data.username}' | base64 -d)"
ADMIN_PASS="$(kubectl -n shopixy-messaging get secret shopixy-rabbitmq-default-user -o jsonpath='{.data.password}' | base64 -d)"
if [[ -z "${ADMIN_USER}" || -z "${ADMIN_PASS}" ]]; then
  echo "ERROR: RabbitMQ administrator credentials were not found" >&2
  exit 2
fi

PORT_FORWARD_LOG="$(mktemp)"
kubectl -n shopixy-messaging port-forward pod/shopixy-rabbitmq-server-0 15671:15671 >"${PORT_FORWARD_LOG}" 2>&1 &
PF_PID=$!
cleanup() {
  kill "${PF_PID}" >/dev/null 2>&1 || true
  wait "${PF_PID}" >/dev/null 2>&1 || true
  rm -f "${PORT_FORWARD_LOG}"
}
trap cleanup EXIT

for _ in $(seq 1 30); do
  if grep -q "Forwarding from" "${PORT_FORWARD_LOG}" 2>/dev/null; then
    break
  fi
  sleep 1
done

if ! grep -q "Forwarding from" "${PORT_FORWARD_LOG}" 2>/dev/null; then
  echo "ERROR: RabbitMQ management port-forward did not become ready" >&2
  cat "${PORT_FORWARD_LOG}" >&2 || true
  exit 3
fi

POLICY_B64="$(base64 -w0 < "${POLICY_FILE}")"
export POLICY_B64
export RABBITMQ_ADMIN_USER="${ADMIN_USER}"
export RABBITMQ_ADMIN_PASS="${ADMIN_PASS}"
export RABBITMQ_DRY_RUN="${DRY_RUN}"

python3 - <<'PY'
import base64
import json
import os
import ssl
import sys
import urllib.error
import urllib.request

policy = json.loads(base64.b64decode(os.environ["POLICY_B64"]).decode("utf-8"))
user = policy["user"]
vhost = policy["vhost"]
desired = {k: policy[k] for k in ("configure", "write", "read")}

ctx = ssl._create_unverified_context()
auth = base64.b64encode(
    f'{os.environ["RABBITMQ_ADMIN_USER"]}:{os.environ["RABBITMQ_ADMIN_PASS"]}'.encode()
).decode()
base = "https://127.0.0.1:15671"


def request(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(
        base + path,
        data=data,
        method=method,
        headers={
            "Authorization": "Basic " + auth,
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, context=ctx, timeout=60) as response:
            raw = response.read().decode("utf-8", "replace")
            return response.status, raw
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        raise SystemExit(f"ERROR: {method} {path} returned HTTP {exc.code}: {raw}")


status, raw = request("GET", f"/api/permissions/{vhost}/{user}")
current = json.loads(raw)
before = {k: current.get(k) for k in ("configure", "write", "read")}

if before == desired:
    print("RabbitMQ permissions already match the committed policy; no change applied.")
    print(json.dumps({"user": user, "vhost": vhost, "changed": False}, indent=2))
    sys.exit(0)

if os.environ.get("RABBITMQ_DRY_RUN") == "1":
    print("RabbitMQ permissions differ from the committed policy (dry run; no change applied).")
    print(json.dumps({
        "user": user,
        "vhost": vhost,
        "changed": False,
        "would_change": True,
        "configure_differs": before.get("configure") != desired["configure"],
        "write_differs": before.get("write") != desired["write"],
        "read_differs": before.get("read") != desired["read"],
    }, indent=2))
    sys.exit(4)

status, _ = request("PUT", f"/api/permissions/{vhost}/{user}", desired)
if status not in (201, 204):
    raise SystemExit(f"ERROR: unexpected PUT status {status}")

status, raw = request("GET", f"/api/permissions/{vhost}/{user}")
after = json.loads(raw)
actual = {k: after.get(k) for k in ("configure", "write", "read")}
if actual != desired:
    raise SystemExit("ERROR: permissions did not converge after PUT")

print("RabbitMQ permissions updated and verified.")
print(json.dumps({
    "user": user,
    "vhost": vhost,
    "changed": True,
    "configure_before_length": len(before.get("configure") or ""),
    "configure_after_length": len(actual["configure"]),
    "write_matches": actual["write"] == desired["write"],
    "read_matches": actual["read"] == desired["read"],
    "configure_matches": actual["configure"] == desired["configure"],
}, indent=2))
PY
