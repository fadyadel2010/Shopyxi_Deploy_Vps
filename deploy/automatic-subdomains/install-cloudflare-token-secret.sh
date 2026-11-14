#!/usr/bin/env bash
set +x
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 /absolute/path/to/cloudflare-dns01-token" >&2
  exit 64
fi

token_file=$1
if [[ ! -f "$token_file" || -L "$token_file" ]]; then
  echo "token input must be a regular, non-symlink file" >&2
  exit 65
fi

case "$(stat -c '%a' "$token_file")" in
  400|600) ;;
  *)
    echo "token file permissions must be 0400 or 0600" >&2
    exit 66
    ;;
esac

if [[ "$(stat -c '%u' "$token_file")" != "$(id -u)" ]]; then
  echo "token file must be owned by the invoking operator" >&2
  exit 67
fi

python3 "$(dirname "${BASH_SOURCE[0]}")/validate-token-file.py" "$token_file"

# Fail BEFORE installing credential authority if an application identity could
# read it, including via group grants or list/watch (which expose Secret data).
check_access() {
  local namespace=$1 account=$2 verb=$3 resource=$4
  local result
  result=$(kubectl auth can-i "$verb" "$resource" -n cert-manager \
    --as="system:serviceaccount:$namespace:$account" \
    --as-group=system:authenticated --as-group=system:serviceaccounts \
    --as-group="system:serviceaccounts:$namespace" 2>/dev/null || true)
  if [[ "$result" != yes && "$result" != no ]]; then
    echo 'RBAC query failed; refusing credential installation' >&2
    exit 72
  fi
  printf '%s\n' "$result"
}
controller_access=$(check_access cert-manager cert-manager get secret/cloudflare-dns01-token) || exit 72
if [[ "$controller_access" != yes ]]; then
  echo 'cert-manager controller cannot read its solver Secret' >&2
  exit 70
fi
accounts=$(kubectl get serviceaccount -n shopixy-app -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}')
if [[ -z "$accounts" ]]; then
  echo 'No application ServiceAccounts discovered; refusing credential installation' >&2
  exit 72
fi
for account in $accounts; do
  for verb in get list watch; do
    resource=secrets
    [[ "$verb" != get ]] || resource=secret/cloudflare-dns01-token
    app_access=$(check_access shopixy-app "$account" "$verb" "$resource") || exit 72
    if [[ "$app_access" == yes ]]; then
      echo "unexpected solver Secret access: shopixy-app/$account $verb" >&2
      exit 71
    fi
  done
done

# The token is sent directly to the Kubernetes API and is never printed or
# materialized in a rendered manifest. ClusterIssuer secrets are resolved from
# cert-manager's cluster-resource namespace.
kubectl -n cert-manager create secret generic cloudflare-dns01-token \
  --from-file=api-token="$token_file" \
  --dry-run=client \
  -o json \
  | kubectl apply --server-side --field-manager=shopixy-dns01-token-installer -f - >/dev/null

echo "cloudflare-dns01-token configured in cert-manager; application service accounts are denied"
