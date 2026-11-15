set -euo pipefail
export DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/readiness-dotnet-home NUGET_PACKAGES=/packages
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export Testing__TempRoot=/tmp/readiness-tests DataProtection__KeysPath=/tmp/readiness-tests/keys
install -d -o postgres -g postgres /tmp/readiness-pg
gosu postgres initdb -D /tmp/readiness-pg --auth=trust --no-locale --encoding=UTF8 >/tmp/initdb.log
gosu postgres pg_ctl -D /tmp/readiness-pg -l /tmp/pg.log -o "-k /tmp -c listen_addresses='127.0.0.1'" -w start
trap 'gosu postgres pg_ctl -D /tmp/readiness-pg -m fast -w stop' EXIT
printf '\n127.0.0.1 app17c-pg\n' >> /etc/hosts
gosu postgres createuser -h /tmp --superuser app17c_fixture
gosu postgres createdb -h /tmp -O app17c_fixture app17c_fixture
export ConnectionStrings__DefaultConnection='Host=app17c-pg;Database=app17c_fixture;Username=app17c_fixture;Pooling=false'
export Testing__DatabasePrefix=affiliate_readiness
cd /src
/dotnet/dotnet test PointOfSale.Tests/PointOfSale.Tests.csproj --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~AffiliatePublishingReadiness' \
  --logger 'trx;LogFileName=readiness-postgres.trx' --results-directory /src/validation/affiliate-publishing-readiness/results
