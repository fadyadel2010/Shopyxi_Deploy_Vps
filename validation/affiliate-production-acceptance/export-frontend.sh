set -euo pipefail
export PATH=/opt/shopixy-toolchain/bin:$PATH
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-20261115
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/affiliate-production-acceptance
grep -q '"inputsUnchanged": true' "$e/frontend-gates.json"
test ! -s "$e/frontend-dist-parity.diff"
grep -q 'configuration file .* test is successful' "$base/frontend-nginx.log"
content=$(sha256sum "$e/frontend-dist-SHA256SUMS.txt" | awk '{print $1}')
source=$(sha256sum "$e/frontend-source-index.json" | awk '{print $1}')
tag="shopixy/web:affiliate-20261115-${content:0:12}"
test "$(docker image inspect "$tag" --format '{{index .Config.Labels "shopixy.io/source-input-sha256"}}')" = "$source"
test "$(docker image inspect "$tag" --format '{{index .Config.Labels "shopixy.io/dist-sha256"}}')" = "$content"
test "$(docker run --rm --network=none --entrypoint /usr/bin/id "$tag" -u)" = 101
trivy image --skip-db-update --skip-java-db-update --offline-scan --scanners vuln --severity HIGH,CRITICAL \
    --exit-code 1 --format json --output "$base/frontend-trivy-private.json" "$tag" > "$base/frontend-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/artifacts/web.tar.gz"
sha256sum "$base/artifacts/web.tar.gz" > "$e/frontend-archive-SHA256SUMS.txt"
docker image inspect "$tag" --format '{{json .RepoDigests}}' > "$e/frontend-registry-digests.json"
docker image inspect "$tag" --format '{{.Id}}' > "$e/frontend-image-id.txt"
printf '%s\n' "$tag" > "$e/frontend-image-tag.txt"
printf '%s\n' '{"build":true,"nonroot":true,"nginxConfiguration":true,"distParity":true,"vulnerabilityGate":true,"registryPush":false,"registryError":"insufficient_scope: authorization failed","releaseTransport":"Established OCI archive import into existing k3s; not a successful registry push"}' > "$e/frontend-image-gates.json"
printf '%s\n' 'Existing immutable Affiliate frontend image verified and exported for the established k3s archive-import release path.'
