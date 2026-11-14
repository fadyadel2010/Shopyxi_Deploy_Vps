#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
dotnet_bin=${DOTNET_BIN:-dotnet}
docker_bin=${DOCKER_BIN:-docker}
trivy_bin=${TRIVY_BIN:-trivy}
base_image=${BASE_IMAGE:-docker.io/shopixy/api@sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e}
runtime_url=https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.15/aspnetcore-runtime-10.0.15-linux-musl-x64.tar.gz
runtime_sha512=0c4608259c99236bcb860c145b8794c72c6817d01d4ed0ad2132b78cd90e13c6527341edb787b38d9e0382d806b61bfbb170b9951cec68d1043840ff176fa90b
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

test "$($dotnet_bin --version)" = "10.0.404"
source_sha=$(python3 "$repo_root/deploy/automatic-subdomains/seal-source-inputs.py")

curl -fL --retry 3 -o "$work/aspnet-runtime.tar.gz" "$runtime_url"
printf '%s  %s\n' "$runtime_sha512" "$work/aspnet-runtime.tar.gz" | sha512sum --check

mkdir -p "$work/context/runtime" "$work/context/app"
tar -xzf "$work/aspnet-runtime.tar.gz" -C "$work/context/runtime"

"$dotnet_bin" restore "$repo_root/PointOfSale.API/PointOfSale.API.csproj" \
  --runtime linux-musl-x64 \
  -p:NuGetLockFilePath=packages.production.lock.json \
  --locked-mode
"$dotnet_bin" list "$repo_root/PointOfSale.API/PointOfSale.API.csproj" package \
  --vulnerable \
  --include-transitive \
  --no-restore \
  | tee "$repo_root/deploy/automatic-subdomains/successor-nuget-vulnerability-audit.txt"
if ! grep -q "has no vulnerable packages" "$repo_root/deploy/automatic-subdomains/successor-nuget-vulnerability-audit.txt"; then
  echo "NuGet vulnerability gate failed" >&2
  exit 68
fi

"$dotnet_bin" publish "$repo_root/PointOfSale.API/PointOfSale.API.csproj" \
  --configuration Release \
  --runtime linux-musl-x64 \
  --self-contained false \
  --no-restore \
  --output "$work/context/app"

api_sha=$(sha256sum "$work/context/app/PointOfSale.API.dll" | awk '{print $1}')
tag=${IMAGE_TAG:-shopixy/api:automatic-subdomains-${api_sha:0:12}-r3}

find "$work/context/app" -maxdepth 1 -type f -name '*.dll' -print0 \
  | sort -z \
  | xargs -0 sha256sum \
  | sed "s#  $work/context/app/#  #" \
  > "$repo_root/deploy/automatic-subdomains/successor-assembly-sha256.txt"

cp "$repo_root/deploy/automatic-subdomains/successor.Dockerfile" "$work/context/Dockerfile"
"$docker_bin" build \
  --pull=false \
  --network=none \
  --build-arg "BASE_IMAGE=$base_image" \
  --build-arg "API_ASSEMBLY_SHA256=$api_sha" \
  --build-arg "SOURCE_INPUT_SHA256=$source_sha" \
  --tag "$tag" \
  "$work/context"

image_id=$("$docker_bin" image inspect "$tag" --format '{{.Id}}')
image_ref=$("$docker_bin" image inspect "$tag" --format '{{index .RepoDigests 0}}')
test "$image_ref" = "shopixy/api@$image_id"

"$docker_bin" image inspect "$tag" > "$repo_root/deploy/automatic-subdomains/successor-image-inspect.json"
printf '%s\n' "$image_id" | tee "$repo_root/deploy/automatic-subdomains/successor-image-digest.txt"
printf '%s\n' "$image_ref" > "$repo_root/deploy/automatic-subdomains/successor-image-reference.txt"
printf '%s\n' "$tag" > "$repo_root/deploy/automatic-subdomains/successor-image-tag.txt"
printf '%s\n' "$api_sha  PointOfSale.API.dll" \
  > "$repo_root/deploy/automatic-subdomains/successor-api-assembly-sha256.txt"

"$docker_bin" run --rm --entrypoint /usr/share/dotnet/dotnet "$tag" --list-runtimes \
  | tee "$repo_root/deploy/automatic-subdomains/successor-runtime.txt"
grep -q 'Microsoft.AspNetCore.App 10.0.15' "$repo_root/deploy/automatic-subdomains/successor-runtime.txt"
grep -q 'Microsoft.NETCore.App 10.0.15' "$repo_root/deploy/automatic-subdomains/successor-runtime.txt"
test "$("$docker_bin" run --rm --entrypoint /usr/bin/id "$tag" -u)" = "1654"
test "$("$docker_bin" run --rm --entrypoint /usr/bin/sha256sum "$tag" /app/PointOfSale.API.dll | awk '{print $1}')" = "$api_sha"

if ! command -v "$trivy_bin" >/dev/null 2>&1 && [[ ! -x "$trivy_bin" ]]; then
  echo "Trivy is required for the production image gate" >&2
  exit 69
fi
"$trivy_bin" image \
  --scanners vuln \
  --severity HIGH,CRITICAL \
  --exit-code 1 \
  --format json \
  --output "$repo_root/deploy/automatic-subdomains/successor-trivy-high-critical.json" \
  "$tag"

if [[ -n "${IMAGE_ARCHIVE:-}" ]]; then
  "$docker_bin" save --output "$IMAGE_ARCHIVE" "$image_ref"
  sha256sum "$IMAGE_ARCHIVE" > "$IMAGE_ARCHIVE.sha256"
fi

echo "built and verified $image_ref"
