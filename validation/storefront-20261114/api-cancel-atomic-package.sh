set -euo pipefail
export PATH="/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH"
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home NUGET_PACKAGES=/home/fadya/.nuget/packages DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114-cancel-atomic
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/storefront-20261114
grep -q 'failed="0"' "$e/cancel-atomic-shipping-all.trx"
grep -q 'failed="0"' "$e/cancel-atomic-checkout-all.trx"
test ! -e "$base/artifacts"
mkdir -p "$base/artifacts/context" "$base/artifacts/publish"
cd "$base/source"
dotnet restore PointOfSale.API/PointOfSale.API.csproj --runtime linux-musl-x64 -p:NuGetLockFilePath=packages.production.lock.json --locked-mode > "$e/api-cancel-atomic-restore.log" 2>&1
dotnet publish PointOfSale.API/PointOfSale.API.csproj -c Release -r linux-musl-x64 --self-contained false --no-restore -m:1 -p:UseSharedCompilation=false --output "$base/artifacts/publish" > "$e/api-cancel-atomic-publish.log" 2>&1
cp "$base/artifacts/publish/PointOfSale.Infrastructure.dll" "$base/artifacts/publish/PointOfSale.Infrastructure.pdb" "$base/artifacts/context/"
cat > "$base/artifacts/context/Dockerfile" <<'EOF'
ARG BASE_IMAGE
FROM ${BASE_IMAGE}
COPY --chown=1654:1654 PointOfSale.Infrastructure.dll PointOfSale.Infrastructure.pdb /app/
LABEL org.opencontainers.image.title="Shopixy cancellation event transaction boundary correction 20261114"
EOF
old=shopixy/api:storefront-20261114-shipment-cancellation
test "$(docker image inspect "$old" --format '{{.Id}}')" = sha256:3e655fefa873be3a7eae0446c96cfc2d98f761a51860b0d002e38093c7ac7265
tag=shopixy/api:storefront-20261114-cancel-atomic
docker build --network=none --pull=false --build-arg "BASE_IMAGE=$old" --tag "$tag" "$base/artifacts/context" > "$e/api-cancel-atomic-build.log" 2>&1
for which in before after; do
 image="$old"; if [ "$which" = after ]; then image="$tag"; fi
 docker run --rm --network=none --entrypoint /bin/sh "$image" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/api-cancel-atomic-$which-files.txt"
 grep -vE ' \./PointOfSale\.Infrastructure\.(dll|pdb)$' "$e/api-cancel-atomic-$which-files.txt" > "$e/api-cancel-atomic-$which-unchanged.txt"
done
diff -u "$e/api-cancel-atomic-before-unchanged.txt" "$e/api-cancel-atomic-after-unchanged.txt" > "$e/api-cancel-atomic-unrelated-parity.diff"
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/api-cancel-atomic-trivy-high-critical.json" "$tag" > "$e/api-cancel-atomic-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/artifacts/api-cancel-atomic.tar.gz"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
echo "docker.io/$ref" > "$e/api-cancel-atomic-image-reference.txt"
echo "$tag" > "$e/api-cancel-atomic-image-tag.txt"
docker image inspect "$tag" > "$e/api-cancel-atomic-image-inspect.json"
echo 'Cancellation atomicity fix packaged; only Infrastructure DLL/PDB replaced.'
