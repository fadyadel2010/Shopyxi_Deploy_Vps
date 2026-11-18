#!/usr/bin/env bash
# Apply the production CommerceAI configuration required by the Admin
# "Create Product with AI" workflow.
#
# The OpenRouter API key is read from stdin on every run. It is never written
# to disk, never passed as a command-line argument, and never printed.
#
# Usage:
#   ./apply-production-config.sh < /path/to/openrouter.key.txt
#
# Rollback snapshots are written to /var/lib/shopixy/ai-product-generation-<UTC>/
# before any mutation.

set -euo pipefail

NAMESPACE="${NAMESPACE:-shopixy-app}"
DEPLOYMENT="${DEPLOYMENT:-shopixy-api}"
CONFIGMAP="${CONFIGMAP:-shopixy-api-json}"
SECRET="${SECRET:-shopixy-api-commerceai-v1}"
SECRET_KEY="${SECRET_KEY:-api-key}"
CONTAINER="${CONTAINER:-api}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
BACKUP_DIR="${BACKUP_DIR:-/var/lib/shopixy/ai-product-generation-${STAMP}}"

if ! command -v kubectl >/dev/null 2>&1; then
  echo "kubectl is required" >&2
  exit 1
fi

# Read the credential once, from stdin only. Validate shape without echoing it.
OPENROUTER_API_KEY="$(cat)"
OPENROUTER_API_KEY="${OPENROUTER_API_KEY%$'\r'}"
OPENROUTER_API_KEY="${OPENROUTER_API_KEY%$'\n'}"
if [[ -z "${OPENROUTER_API_KEY}" ]]; then
  echo "No OpenRouter API key supplied on stdin" >&2
  exit 1
fi
if [[ "${OPENROUTER_API_KEY}" != sk-or-* ]]; then
  echo "The supplied credential does not look like an OpenRouter key" >&2
  exit 1
fi
if (( ${#OPENROUTER_API_KEY} < 20 )); then
  echo "The supplied OpenRouter credential is too short" >&2
  exit 1
fi

mkdir -p "${BACKUP_DIR}"
chmod 700 "${BACKUP_DIR}"

echo "Writing rollback snapshots to ${BACKUP_DIR}"
kubectl -n "${NAMESPACE}" get configmap "${CONFIGMAP}" -o json > "${BACKUP_DIR}/configmap.json"
kubectl -n "${NAMESPACE}" get deployment "${DEPLOYMENT}" -o json > "${BACKUP_DIR}/deployment.json"
if kubectl -n "${NAMESPACE}" get secret "${SECRET}" -o json > "${BACKUP_DIR}/secret.json" 2>/dev/null; then
  echo "Existing CommerceAI secret snapshotted"
else
  rm -f "${BACKUP_DIR}/secret.json"
  echo "No existing CommerceAI secret; rollback will delete the new secret"
fi
echo "${BACKUP_DIR}" > /var/lib/shopixy/ai-product-generation-latest

# 1. Non-secret provider configuration. Preserve every unrelated setting in
#    appsettings.json; only replace the CommerceAI block.
python3 - "${BACKUP_DIR}/configmap.json" "${BACKUP_DIR}/configmap-patch.json" <<'PY'
import json
import sys

source, target = sys.argv[1], sys.argv[2]
with open(source, encoding="utf-8") as handle:
    configmap = json.load(handle)
raw = configmap["data"]["appsettings.json"]
settings = json.loads(raw)
commerce_ai = settings.get("CommerceAI") or {}
commerce_ai.update({
    "Enabled": True,
    "Provider": "openrouter",
    "Model": "openrouter/free",
    "FreeModelsOnly": True,
    "Endpoint": "https://openrouter.ai/api/v1/chat/completions",
    "TimeoutSeconds": 90,
    "MaxTokens": 8192,
    "Temperature": 0.3,
})
settings["CommerceAI"] = commerce_ai
with open(target, "w", encoding="utf-8") as handle:
    json.dump({"data": {"appsettings.json": json.dumps(settings, indent=2)}}, handle)
PY
kubectl -n "${NAMESPACE}" patch configmap "${CONFIGMAP}" --type=merge --patch-file "${BACKUP_DIR}/configmap-patch.json"

# 2. Credential. The value is streamed into kubectl; it is never an argv value.
printf '%s' "${OPENROUTER_API_KEY}" | \
  kubectl -n "${NAMESPACE}" create secret generic "${SECRET}" \
    --from-file="${SECRET_KEY}=/dev/stdin" \
    --dry-run=client -o yaml | \
  kubectl -n "${NAMESPACE}" apply -f - >/dev/null
unset OPENROUTER_API_KEY

# 3. Bind the secret to the existing CommerceAI:ApiKey option. Strategic merge
#    keeps every existing env var and adds/updates only this one.
python3 - "${BACKUP_DIR}/deployment-patch.json" "${SECRET}" "${SECRET_KEY}" "${CONTAINER}" <<'PY'
import json
import sys

target, secret, key, container = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
patch = {
    "spec": {
        "template": {
            "spec": {
                "containers": [
                    {
                        "name": container,
                        "env": [
                            {
                                "name": "CommerceAI__ApiKey",
                                "valueFrom": {"secretKeyRef": {"name": secret, "key": key}},
                            }
                        ],
                    }
                ]
            }
        }
    }
}
with open(target, "w", encoding="utf-8") as handle:
    json.dump(patch, handle)
PY
kubectl -n "${NAMESPACE}" patch deployment "${DEPLOYMENT}" --type=strategic --patch-file "${BACKUP_DIR}/deployment-patch.json"

kubectl -n "${NAMESPACE}" rollout status "deployment/${DEPLOYMENT}" --timeout=180s

# 4. Verify only non-secret configuration and secret presence.
python3 - "${NAMESPACE}" "${CONFIGMAP}" <<'PY'
import json
import subprocess
import sys

namespace, configmap = sys.argv[1], sys.argv[2]
raw = subprocess.check_output([
    "kubectl", "-n", namespace, "get", "configmap", configmap,
    "-o", "jsonpath={.data.appsettings\\.json}",
], text=True)
settings = json.loads(raw)["CommerceAI"]
safe = {key: value for key, value in settings.items() if "key" not in key.lower() and "secret" not in key.lower()}
print(json.dumps({"CommerceAI": safe}, indent=2, sort_keys=True))
assert settings.get("Enabled") is True
assert settings.get("Provider") == "openrouter"
assert settings.get("Model") == "openrouter/free"
assert settings.get("FreeModelsOnly") is True
PY

if kubectl -n "${NAMESPACE}" exec "deployment/${DEPLOYMENT}" -c "${CONTAINER}" -- sh -c 'test -n "${CommerceAI__ApiKey:-}"' >/dev/null 2>&1; then
  echo "CommerceAI__ApiKey is present in the running API container"
else
  echo "CommerceAI__ApiKey is NOT present in the running API container" >&2
  exit 1
fi

echo "Applied. Rollback snapshot: ${BACKUP_DIR}"
