set -euo pipefail
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-analytics-20261115
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
export DOTNET_BIN=/home/fadya/.dotnet10-10.0.404/dotnet
export DOCKER_BIN=/opt/shopixy-toolchain/bin/docker
export TRIVY_BIN=/opt/shopixy-toolchain/bin/trivy
slot=${ANALYTICS_PRODUCTION_API_SLOT:-analytics-production-api}
export IMAGE_TAG=shopixy/api:affiliate-analytics-production-20261116-${slot##*-}
export IMAGE_ARCHIVE="$base/$slot/api.tar"
export BASE_IMAGE=docker.io/shopixy/api@sha256:39a79e0baaa7a6a935e2f84b6e77a7d4b22618c6f5d9b5e59a5d8f1ebbc9856b
test "$("$DOCKER_BIN" image inspect "$BASE_IMAGE" --format '{{.Id}}')" = sha256:39a79e0baaa7a6a935e2f84b6e77a7d4b22618c6f5d9b5e59a5d8f1ebbc9856b
bash "$base/source/deploy/automatic-subdomains/build-successor-image.sh" > "$base/${slot}-build-private.log" 2>&1
