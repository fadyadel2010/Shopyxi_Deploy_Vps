set -euo pipefail
export PATH="/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH"
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home NUGET_PACKAGES=/home/fadya/.nuget/packages DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114-shipping-fix
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/storefront-20261114
grep -q 'failed="0"' "$e/shipping-fix-after-v3.trx"
test ! -e "$base/artifacts"
mkdir -p "$base/artifacts/context" "$base/artifacts/publish"
cd "$base/source"
dotnet restore PointOfSale.API/PointOfSale.API.csproj --runtime linux-musl-x64 -p:NuGetLockFilePath=packages.production.lock.json --locked-mode > "$e/api-shipping-restore.log" 2>&1
dotnet publish PointOfSale.API/PointOfSale.API.csproj -c Release -r linux-musl-x64 --self-contained false --no-restore -m:1 -p:UseSharedCompilation=false --output "$base/artifacts/publish" > "$e/api-shipping-publish.log" 2>&1
for name in PointOfSale.API PointOfSale.Infrastructure; do cp "$base/artifacts/publish/$name.dll" "$base/artifacts/publish/$name.pdb" "$base/artifacts/context/"; done
cat > "$base/artifacts/context/Dockerfile" <<'EOF'
ARG BASE_IMAGE
FROM ${BASE_IMAGE}
COPY --chown=1654:1654 PointOfSale.API.dll PointOfSale.API.pdb PointOfSale.Infrastructure.dll PointOfSale.Infrastructure.pdb /app/
LABEL org.opencontainers.image.title="Shopixy canonical shipment cancellation binding and terminal action correction 20261114"
EOF
old=shopixy/api:storefront-20261114-87ede42ada35
test "$(docker image inspect "$old" --format '{{.Id}}')" = sha256:6b7b654fe94b007703865ff5510b37a4f48cbf1112b0b6c95aec99a6ee12a25c
tag=shopixy/api:storefront-20261114-shipment-cancellation
docker build --network=none --pull=false --build-arg "BASE_IMAGE=$old" --tag "$tag" "$base/artifacts/context" > "$e/api-shipping-build.log" 2>&1
for which in before after; do
  image="$old"; if [ "$which" = after ]; then image="$tag"; fi
  docker run --rm --network=none --entrypoint /bin/sh "$image" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/api-shipping-$which-files.txt"
  grep -vE ' \./PointOfSale\.(API|Infrastructure)\.(dll|pdb)$' "$e/api-shipping-$which-files.txt" > "$e/api-shipping-$which-unchanged.txt"
done
diff -u "$e/api-shipping-before-unchanged.txt" "$e/api-shipping-after-unchanged.txt" > "$e/api-shipping-unrelated-parity.diff"
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/api-shipping-trivy-high-critical.json" "$tag" > "$e/api-shipping-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/artifacts/api-shipping.tar.gz"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
echo "docker.io/$ref" > "$e/api-shipping-image-reference.txt"
docker image inspect "$tag" > "$e/api-shipping-image-inspect.json"
echo 'Shipment fix packaged; configuration and all other assemblies/dependencies unchanged.'
