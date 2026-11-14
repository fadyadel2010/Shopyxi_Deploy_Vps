set -euo pipefail
export PATH="/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH"
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home
export NUGET_PACKAGES=/home/fadya/.nuget/packages DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/storefront-20261114
test -s "$e/api-after.trx"
test ! -e "$base/artifacts"
mkdir -p "$base/artifacts/api-context" "$base/artifacts/publish"
cd "$base/source"
dotnet restore PointOfSale.API/PointOfSale.API.csproj --runtime linux-musl-x64 -p:NuGetLockFilePath=packages.production.lock.json --locked-mode > "$e/api-restore.log" 2>&1
dotnet publish PointOfSale.API/PointOfSale.API.csproj -c Release -r linux-musl-x64 --self-contained false --no-restore -m:1 -p:UseSharedCompilation=false --output "$base/artifacts/publish" > "$e/api-publish.log" 2>&1
cp "$base/artifacts/publish/PointOfSale.API.dll" "$base/artifacts/publish/PointOfSale.API.pdb" "$base/artifacts/api-context/"
cp "$e/api-overlay.Dockerfile" "$base/artifacts/api-context/Dockerfile"
base_tag=shopixy/api:r10-expense-v3-7b1d8bbb8836
test "$(docker image inspect "$base_tag" --format '{{.Id}}')" = sha256:d0f09f8b0ccd0b230b7cce35415d03243bbe54715a9dc270e5343727783890be
docker run --rm --network=none --entrypoint /bin/sh "$base_tag" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/api-base-files.txt"
source_sha=$(sha256sum "$e/api-source-index.json" | awk '{print $1}')
assembly_sha=$(sha256sum "$base/artifacts/api-context/PointOfSale.API.dll" | awk '{print $1}')
tag="shopixy/api:storefront-20261114-${assembly_sha:0:12}"
docker build --pull=false --network=none --build-arg "BASE_IMAGE=$base_tag" --build-arg "SOURCE_INPUT_SHA256=$source_sha" --tag "$tag" "$base/artifacts/api-context" > "$e/api-image-build.log" 2>&1
test "$(docker run --rm --network=none --entrypoint /usr/bin/id "$tag" -u)" = 1654
docker run --rm --network=none --entrypoint /bin/sh "$tag" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/api-image-files.txt"
grep -vE ' \./PointOfSale.API\.(dll|pdb)$' "$e/api-base-files.txt" > "$e/api-base-unchanged-files.txt"
grep -vE ' \./PointOfSale.API\.(dll|pdb)$' "$e/api-image-files.txt" > "$e/api-image-unchanged-files.txt"
diff -u "$e/api-base-unchanged-files.txt" "$e/api-image-unchanged-files.txt" > "$e/api-non-api-files-parity.diff"
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/api-trivy-high-critical.json" "$tag" > "$e/api-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/artifacts/api.tar.gz"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
test -n "$ref"
echo "docker.io/$ref" > "$e/api-image-reference.txt"
echo "$tag" > "$e/api-image-tag.txt"
docker image inspect "$tag" > "$e/api-image-inspect.json"
sha256sum "$base/artifacts/api.tar.gz" > "$e/api-archive-SHA256SUMS.txt"
echo 'API packaged: existing dependencies/configuration unchanged; only API DLL/PDB replaced.'
