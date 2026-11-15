set -euo pipefail
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-full-e2e-20261115
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
export DOTNET_BIN=/home/fadya/.dotnet10-10.0.404/dotnet
export DOCKER_BIN=/opt/shopixy-toolchain/bin/docker
export TRIVY_BIN=/opt/shopixy-toolchain/bin/trivy
export IMAGE_TAG=shopixy/api:affiliate-full-e2e-20261115
export IMAGE_ARCHIVE="$base/production-api-candidate/api.tar"
# This imported-image toolchain resolves verified local tags during builds,
# while digest-form FROM attempts an unavailable private-registry lookup.
# Check the exact pinned base identity before using its local tag alias.
export BASE_IMAGE=shopixy/api:app82-cart-r11-393cb0cbd46a-r1
test "$("$DOCKER_BIN" image inspect "$BASE_IMAGE" --format '{{.Id}}')" = sha256:5efeb7042bcaf6ea223f3df06c7983862e073787e78634fb996ffbf9c4cf266e
bash "$base/production-api-candidate/deploy/automatic-subdomains/build-successor-image.sh" > "$base/production-api-build-local-base-private.log" 2>&1
