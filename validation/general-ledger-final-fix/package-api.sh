set -euo pipefail
export PATH="/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH"
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home NUGET_PACKAGES=/home/fadya/.nuget/packages DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-general-ledger-final-fix
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/general-ledger-final-fix
grep -q 'failed="0"' "$e/after.trx"
test ! -e "$base/artifacts"
mkdir -p "$base/artifacts/context" "$base/artifacts/publish"
cd "$base/source"
dotnet restore PointOfSale.API/PointOfSale.API.csproj --runtime linux-musl-x64 -p:NuGetLockFilePath=packages.production.lock.json --locked-mode > "$e/api-restore.log" 2>&1
dotnet publish PointOfSale.API/PointOfSale.API.csproj -c Release -r linux-musl-x64 --self-contained false --no-restore -m:1 -p:UseSharedCompilation=false --output "$base/artifacts/publish" > "$e/api-publish.log" 2>&1
for component in API Infrastructure; do
 cp "$base/artifacts/publish/PointOfSale.$component.dll" "$base/artifacts/publish/PointOfSale.$component.pdb" "$base/artifacts/context/"
done
cat > "$base/artifacts/context/Dockerfile" <<'EOF'
ARG BASE_IMAGE
FROM ${BASE_IMAGE}
COPY --chown=1654:1654 PointOfSale.API.dll PointOfSale.API.pdb PointOfSale.Infrastructure.dll PointOfSale.Infrastructure.pdb /app/
LABEL org.opencontainers.image.title="Shopixy General Ledger running balance and stale-count guidance correction"
EOF
old=shopixy/api:storefront-20261114-cancel-atomic
test "$(docker image inspect "$old" --format '{{.Id}}')" = sha256:fcdc623d757ae9f2ddf2314b8b6b81b0c993d85240c846ebffd6206fd2e468c0
tag=shopixy/api:general-ledger-final-fix-20261114
docker build --network=none --pull=false --build-arg "BASE_IMAGE=$old" --tag "$tag" "$base/artifacts/context" > "$e/api-image-build.log" 2>&1
for which in before after; do
 image="$old"; if [ "$which" = after ]; then image="$tag"; fi
 docker run --rm --network=none --entrypoint /bin/sh "$image" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/api-$which-files.txt"
 grep -vE ' \./PointOfSale\.(API|Infrastructure)\.(dll|pdb)$' "$e/api-$which-files.txt" > "$e/api-$which-unchanged.txt"
done
diff -u "$e/api-before-unchanged.txt" "$e/api-after-unchanged.txt" > "$e/api-unrelated-parity.diff"
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/api-trivy-high-critical.json" "$tag" > "$e/api-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/artifacts/api.tar.gz"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
echo "docker.io/$ref" > "$e/api-image-reference.txt"
echo "$tag" > "$e/api-image-tag.txt"
docker image inspect "$tag" > "$e/api-image-inspect.json"
echo 'API successor packaged. Only API/Infrastructure DLL/PDB replaced; all other runtime files identical.'
