set -euo pipefail
export PATH=/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home NUGET_PACKAGES=/home/fadya/.nuget/packages
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 TRIVY_SKIP_VERSION_CHECK=true
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115
repo=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source
e="$repo/validation/affiliate-production-remediation"
python3 - "$e/gate-summary.json" <<'PY'
import json,sys
g=json.load(open(sys.argv[1]))
assert g['dotnetCounters']['total'] == g['dotnetCounters']['passed'] and g['dotnetCounters']['failed'] == 0
assert g['frontend']['tests'] and g['frontend']['typescript'] and g['frontend']['build']
assert g['runtimeGrantsAndRetainingRevokeRehearsed'] and not g['productionDeployment']
PY
source=$(sha256sum "$e/source-index.json" | awk '{print $1}')
artifacts="$base/candidate-${source:0:12}"
test ! -e "$artifacts"
mkdir -p "$artifacts/api-context" "$artifacts/web-context" "$artifacts/publish"
cd "$base/source"
dotnet restore PointOfSale.API/PointOfSale.API.csproj --source /home/fadya/.nuget/packages --runtime linux-musl-x64 \
  -p:NuGetLockFilePath=packages.production.lock.json --locked-mode > "$base/candidate-api-restore.log" 2>&1
dotnet publish PointOfSale.API/PointOfSale.API.csproj -c Release -r linux-musl-x64 --self-contained false --no-restore -m:1 \
  -p:UseSharedCompilation=false --output "$artifacts/publish" > "$base/candidate-api-publish.log" 2>&1
for component in API Core Infrastructure; do
  cp "$artifacts/publish/PointOfSale.$component.dll" "$artifacts/publish/PointOfSale.$component.pdb" "$artifacts/api-context/"
done
cp "$e/api-successor.Dockerfile" "$artifacts/api-context/Dockerfile"
api_base=shopixy/api:general-ledger-final-fix-20261114
web_base=shopixy/web:affiliate-20261115-cf0a986217d2
test "$(docker image inspect "$api_base" --format '{{.Id}}')" = sha256:d3f10ae2cc93eb1fdddb7bd89c55726ca3f6d0ea1e4dbbf7825f47aaa19dbcb9
test "$(docker image inspect "$web_base" --format '{{.Id}}')" = sha256:1a594262000458743141e30bc416a51d05d7717aa565a33621b961507a3670d9
api_tag="shopixy/api:affiliate-return-20261115-${source:0:12}"
web_tag="shopixy/web:affiliate-return-20261115-${source:0:12}"
docker build --pull=false --network=none --build-arg "BASE_IMAGE=$api_base" --build-arg "SOURCE_INPUT_SHA256=$source" \
  --tag "$api_tag" "$artifacts/api-context" > "$base/candidate-api-image-build.log" 2>&1
for state in before after; do
  image="$api_base"; if [ "$state" = after ]; then image="$api_tag"; fi
  docker run --rm --network=none --entrypoint /bin/sh "$image" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$artifacts/api-$state-files.txt"
  grep -vE ' \./PointOfSale\.(API|Core|Infrastructure)\.(dll|pdb)$' "$artifacts/api-$state-files.txt" > "$artifacts/api-$state-unrelated.txt"
done
diff -u "$artifacts/api-before-unrelated.txt" "$artifacts/api-after-unrelated.txt" > "$artifacts/api-unrelated-parity.diff"
test "$(docker run --rm --network=none --entrypoint /usr/bin/id "$api_tag" -u)" = 1654
docker run --rm --network=none --entrypoint /bin/sh "$api_tag" -ec 'cat /app/PointOfSale.API.deps.json' > "$artifacts/api-runtime.deps.json"
cmp "$artifacts/api-runtime.deps.json" "$artifacts/publish/PointOfSale.API.deps.json"
cp -a "$base/admin-dist" "$artifacts/web-context/dist"
cp "$repo/validation/qa-erp-remediation-r9/tools/frontend-successor-r9.Dockerfile" "$artifacts/web-context/Dockerfile"
(cd "$artifacts/web-context/dist" && find . -type f -print0 | sort -z | xargs -0 sha256sum) > "$artifacts/web-dist-SHA256SUMS.txt"
dist=$(sha256sum "$artifacts/web-dist-SHA256SUMS.txt" | awk '{print $1}')
docker build --pull=false --network=none --build-arg "BASE_IMAGE=$web_base" \
  --build-arg 'BASE_REFERENCE=docker.io/shopixy/web@sha256:1a594262000458743141e30bc416a51d05d7717aa565a33621b961507a3670d9' \
  --build-arg "SOURCE_INPUT_SHA256=$source" --build-arg "DIST_SHA256=$dist" \
  --build-arg 'IMAGE_TITLE=Shopixy audited Affiliate manual payout return 20261115' \
  --tag "$web_tag" "$artifacts/web-context" > "$base/candidate-web-image-build.log" 2>&1
test "$(docker run --rm --network=none --entrypoint /usr/bin/id "$web_tag" -u)" = 101
docker run --rm --network=none --entrypoint /usr/sbin/nginx "$web_tag" -t > "$base/candidate-web-nginx.log" 2>&1
docker run --rm --network=none --entrypoint /bin/sh "$web_tag" -ec 'cd /usr/share/nginx/html; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$artifacts/web-image-dist-SHA256SUMS.txt"
diff -u "$artifacts/web-dist-SHA256SUMS.txt" "$artifacts/web-image-dist-SHA256SUMS.txt" > "$artifacts/web-dist-parity.diff"
for kind in api web; do
  tag="$api_tag"; if [ "$kind" = web ]; then tag="$web_tag"; fi
  trivy image --skip-db-update --skip-java-db-update --offline-scan --scanners vuln --severity HIGH,CRITICAL \
    --exit-code 1 --format json --output "$base/candidate-$kind-trivy-private.json" "$tag" > "$base/candidate-$kind-trivy.log" 2>&1
  docker save "$tag" | gzip -1 > "$artifacts/$kind.tar.gz"
  docker image inspect "$tag" --format '{{.Id}}' > "$artifacts/$kind-image-id.txt"
  printf '%s\n' "$tag" > "$artifacts/$kind-image-tag.txt"
done
sha256sum "$artifacts/api.tar.gz" "$artifacts/web.tar.gz" > "$artifacts/archive-SHA256SUMS.txt"
printf '%s\n' "$artifacts" > "$base/verified-candidate-directory.txt"
printf '%s\n' 'Release candidates packaged: API unrelated-file parity, dependency parity, non-root, Web dist parity/nginx, cached-DB HIGH/CRITICAL scans and archive checksums passed. No registry push or deployment performed.'
