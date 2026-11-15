set -euo pipefail
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
docker=/opt/shopixy-toolchain/bin/docker
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-full-e2e-20261115
phase=${1:?before or after required}
test "$phase" = before || test "$phase" = after
"$docker" run --name "affiliate-full-regression-$phase" --network=none --pull=never --entrypoint /bin/bash \
  -v "$base/source:/src" -v /home/fadya/.dotnet10-10.0.404:/dotnet:ro -v /home/fadya/.nuget/packages:/packages:ro \
  -e AFFILIATE_FULL_TEST_ISOLATION=network-none-local-socket \
  -e 'AFFILIATE_FULL_TEST_DB=Host=/tmp;Database=affiliate_journey_regression;Username=affiliate_fixture;Pooling=false' \
  -e DOTNET_ROOT=/dotnet -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 -e DOTNET_CLI_HOME=/tmp/dotnet-home \
  -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e NUGET_PACKAGES=/packages \
  -e "JOURNEY_TEST_PHASE=$phase" postgres:18.6 \
  /src/validation/affiliate-full-e2e/inside-focused.sh > "$base/focused-$phase-private.log" 2>&1
