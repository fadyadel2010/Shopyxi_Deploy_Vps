set -euo pipefail
export PATH="/opt/shopixy-toolchain/bin:$PATH"
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114-admin
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/storefront-20261114
test -s "$e/admin-gates.json"
test ! -e "$base/artifacts"
mkdir -p "$base/artifacts/context"
cp -a "$base/admin-dist" "$base/artifacts/context/dist"
cp "$e/../qa-erp-remediation-r9/tools/frontend-successor-r9.Dockerfile" "$base/artifacts/context/Dockerfile"
(cd "$base/artifacts/context/dist" && find . -type f -print0 | sort -z | xargs -0 sha256sum) > "$e/admin-dist-SHA256SUMS.txt"
content=$(sha256sum "$e/admin-dist-SHA256SUMS.txt" | awk '{print $1}')
source_sha=$(sha256sum "$e/admin-source-index.json" | awk '{print $1}')
tag="shopixy/web:storefront-20261114-${content:0:12}"
base_tag=shopixy/web:r10-ui-v2-0c9cb11e998a
test "$(docker image inspect "$base_tag" --format '{{.Id}}')" = sha256:702a8deee6a3d83735f2067315a368ef6bae0f6a27feab7eb37fcf96432a1e2e
docker build --pull=false --network=none --build-arg "BASE_IMAGE=$base_tag" --build-arg BASE_REFERENCE=docker.io/shopixy/web@sha256:702a8deee6a3d83735f2067315a368ef6bae0f6a27feab7eb37fcf96432a1e2e --build-arg "SOURCE_INPUT_SHA256=$source_sha" --build-arg "DIST_SHA256=$content" --build-arg 'IMAGE_TITLE=Shopixy Storefront EGP Appearance preview fix 20261114' --tag "$tag" "$base/artifacts/context" > "$e/admin-image-build.log" 2>&1
test "$(docker run --rm --network=none --entrypoint /usr/bin/id "$tag" -u)" = 101
docker run --rm --network=none --entrypoint /bin/sh "$tag" -ec 'cd /usr/share/nginx/html; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/admin-image-dist-SHA256SUMS.txt"
diff -u "$e/admin-dist-SHA256SUMS.txt" "$e/admin-image-dist-SHA256SUMS.txt" > "$e/admin-dist-parity.diff"
docker run --rm --network=none --entrypoint /usr/sbin/nginx "$tag" -t > "$e/admin-nginx-test.log" 2>&1
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/admin-trivy-high-critical.json" "$tag" > "$e/admin-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/artifacts/admin.tar.gz"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
test -n "$ref"
echo "docker.io/$ref" > "$e/admin-image-reference.txt"
echo "$tag" > "$e/admin-image-tag.txt"
docker image inspect "$tag" > "$e/admin-image-inspect.json"
sha256sum "$base/artifacts/admin.tar.gz" > "$e/admin-archive-SHA256SUMS.txt"
echo 'Admin packaged: EGP-only Appearance preview; content parity and non-root verified.'
