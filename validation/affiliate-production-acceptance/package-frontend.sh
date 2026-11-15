set -euo pipefail
export PATH=/opt/shopixy-toolchain/bin:$PATH
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-20261115
repo=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source
e=$repo/validation/affiliate-production-acceptance
grep -q '"inputsUnchanged": true' "$e/frontend-gates.json"
test ! -e "$base/artifacts"
mkdir -p "$base/artifacts/context"
cp -a "$base/admin-dist" "$base/artifacts/context/dist"
cp "$repo/validation/qa-erp-remediation-r9/tools/frontend-successor-r9.Dockerfile" "$base/artifacts/context/Dockerfile"
(cd "$base/artifacts/context/dist" && find . -type f -print0 | sort -z | xargs -0 sha256sum) > "$e/frontend-dist-SHA256SUMS.txt"
content=$(sha256sum "$e/frontend-dist-SHA256SUMS.txt" | awk '{print $1}')
source=$(sha256sum "$e/frontend-source-index.json" | awk '{print $1}')
tag="shopixy/web:affiliate-20261115-${content:0:12}"
baseline=shopixy/web:storefront-20261114-f2ba162a9555
baseline_id=sha256:756879210827a50c84d8d42da66e2d6e2235907e1af33930a2a450cab0f46682
test "$(docker image inspect "$baseline" --format '{{.Id}}')" = "$baseline_id"
docker build --pull=false --network=none --build-arg "BASE_IMAGE=$baseline" \
    --build-arg "BASE_REFERENCE=docker.io/shopixy/web@$baseline_id" \
    --build-arg "SOURCE_INPUT_SHA256=$source" --build-arg "DIST_SHA256=$content" \
    --build-arg 'IMAGE_TITLE=Shopixy Affiliate profile timestamp correction 20261115' \
    --tag "$tag" "$base/artifacts/context" > "$base/frontend-image-build.log" 2>&1
test "$(docker run --rm --network=none --entrypoint /usr/bin/id "$tag" -u)" = 101
docker run --rm --network=none --entrypoint /usr/sbin/nginx "$tag" -t > "$base/frontend-nginx.log" 2>&1
docker run --rm --network=none --entrypoint /bin/sh "$tag" -ec 'cd /usr/share/nginx/html; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/frontend-image-dist-SHA256SUMS.txt"
diff -u "$e/frontend-dist-SHA256SUMS.txt" "$e/frontend-image-dist-SHA256SUMS.txt" > "$e/frontend-dist-parity.diff"
docker push "$tag" > "$base/frontend-registry-push.log" 2>&1
docker image inspect "$tag" --format '{{json .RepoDigests}}' > "$e/frontend-registry-digests.json"
docker image inspect "$tag" --format '{{.Id}}' > "$e/frontend-image-id.txt"
printf '%s\n' "$tag" > "$e/frontend-image-tag.txt"
printf '%s\n' '{"build":true,"nonroot":true,"nginxConfiguration":true,"distParity":true,"registryPush":true}' > "$e/frontend-image-gates.json"
printf '%s\n' 'Affiliate frontend image built, verified and pushed to the existing registry.'
