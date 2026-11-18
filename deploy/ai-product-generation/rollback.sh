#!/usr/bin/env bash
# Restore the CommerceAI configuration captured by apply-production-config.sh.
#
# Usage:
#   ./rollback.sh [BACKUP_DIR]
#
# With no argument the most recent snapshot recorded in
# /var/lib/shopixy/ai-product-generation-latest is used.

set -euo pipefail

NAMESPACE="${NAMESPACE:-shopixy-app}"
DEPLOYMENT="${DEPLOYMENT:-shopixy-api}"
CONFIGMAP="${CONFIGMAP:-shopixy-api-json}"
SECRET="${SECRET:-shopixy-api-commerceai-v1}"
BACKUP_DIR="${1:-$(cat /var/lib/shopixy/ai-product-generation-latest 2>/dev/null || true)}"

if [[ -z "${BACKUP_DIR}" || ! -d "${BACKUP_DIR}" ]]; then
  echo "Rollback snapshot not found: ${BACKUP_DIR:-<none>}" >&2
  exit 1
fi
if [[ ! -f "${BACKUP_DIR}/configmap.json" || ! -f "${BACKUP_DIR}/deployment.json" ]]; then
  echo "Rollback snapshot is incomplete: ${BACKUP_DIR}" >&2
  exit 1
fi

# Replace preserves the captured resourceVersion semantics only when the live
# object has not changed. If it has, inspect and reconcile intentionally.
kubectl -n "${NAMESPACE}" replace -f "${BACKUP_DIR}/configmap.json"
kubectl -n "${NAMESPACE}" replace -f "${BACKUP_DIR}/deployment.json"

if [[ -f "${BACKUP_DIR}/secret.json" ]]; then
  kubectl -n "${NAMESPACE}" replace -f "${BACKUP_DIR}/secret.json"
else
  kubectl -n "${NAMESPACE}" delete secret "${SECRET}" --ignore-not-found
fi

kubectl -n "${NAMESPACE}" rollout status "deployment/${DEPLOYMENT}" --timeout=180s
echo "Rolled back to ${BACKUP_DIR}"
