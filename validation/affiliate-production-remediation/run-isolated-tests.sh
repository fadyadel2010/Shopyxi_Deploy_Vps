set -euo pipefail
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
docker=/opt/shopixy-toolchain/bin/docker
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115
name=affiliate-return-isolated-$(date +%s)
test "$("$docker" image inspect postgres:18.6 --format '{{.Id}}')" = sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42
test "$("$docker" image inspect mongo:8.0-noble --format '{{.Id}}')" = sha256:3c64937121b9437095921c4bb2569911084c05f7b88e4a263d62a8959d2b2399
mongo="$name-mongo"
"$docker" run -d --rm --pull=never --name "$mongo" --network=none mongo:8.0-noble \
  mongod --bind_ip 127.0.0.1 --wiredTigerCacheSizeGB 0.25 > "$base/isolated-mongo-container.txt"
trap '"$docker" rm -f "$mongo" > /dev/null' EXIT
# Share only the loopback namespace of our network-none Mongo container.
# Neither container has an external interface or a published host port.
"$docker" run --rm --name "$name" --network="container:$mongo" --entrypoint /bin/bash \
  -v "$base/source:/src" -v /home/fadya/.dotnet10-10.0.404:/dotnet:ro -v /home/fadya/.nuget/packages:/packages:ro \
  -e AFFILIATE_RETURN_ISOLATION=network-none-local-socket \
  -e 'AFFILIATE_RETURN_DB=Host=/tmp;Database=affiliate_return_fixture;Username=affiliate_fixture;Pooling=false' \
  -e DOTNET_ROOT=/dotnet -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 -e DOTNET_CLI_HOME=/tmp/dotnet-home \
  -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e NUGET_PACKAGES=/packages \
  --pull=never postgres:18.6 \
  /src/validation/affiliate-production-remediation/inside-isolated-tests.sh > "$base/isolated-tests.log" 2>&1
printf '%s\n' 'Isolated PostgreSQL financial tests passed.'
