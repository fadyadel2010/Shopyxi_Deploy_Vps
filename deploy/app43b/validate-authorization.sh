#!/bin/sh
# PREPARED ONLY. Read-only authorization checks still require separate live approval.
set -eu
: "${APP43B_SEPARATE_APPROVAL:?Separate live authorization required}"
check() {
  subject=$1 namespace=$2 verb=$3 resource=$4 expected=$5
  sa_namespace=${subject%%/*}; sa_name=${subject#*/}
  answer=$(kubectl auth can-i "$verb" "$resource" -n "$namespace" \
    --as="system:serviceaccount:$sa_namespace:$sa_name" \
    --as-group=system:authenticated --as-group=system:serviceaccounts \
    --as-group="system:serviceaccounts:$sa_namespace" 2>/dev/null || true)
  test "$answer" = "$expected" || { printf '%s\n' "Authorization mismatch: $subject $verb $resource in $namespace"; exit 1; }
}
check cnpg-system/cnpg-manager shopixy-search get secrets/search-administration no
check cnpg-system/cnpg-manager shopixy-search patch secrets/search-administration no
check rabbitmq-system/rabbitmq-cluster-operator shopixy-mongo get secrets/mongo-admin no
check cnpg-system/plugin-barman-cloud shopixy-search create secrets no
check cnpg-system/cnpg-manager shopixy-data get secrets yes
check rabbitmq-system/rabbitmq-cluster-operator shopixy-messaging get secrets yes
check cnpg-system/plugin-barman-cloud shopixy-data get secrets yes
check shopixy-data/shopixy-postgres shopixy-data get secrets/b2-credentials yes
for app in api baileys web storefront commercehub; do
  check "shopixy-app/shopixy-$app" shopixy-search get secrets/search-administration no
  check "shopixy-app/shopixy-$app" shopixy-app list secrets no
  check "shopixy-app/shopixy-$app" shopixy-app create pods no
done
printf '%s\n' 'Scoped authorization matrix passed. Admission, reconciliation, renewal and backups require their separate tests.'
