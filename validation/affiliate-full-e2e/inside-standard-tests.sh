set -euo pipefail
test "$AFFILIATE_FULL_TEST_ISOLATION" = network-none-local-socket
install -d -o postgres -g postgres /tmp/journey-pg
gosu postgres initdb -D /tmp/journey-pg --auth=trust --no-locale --encoding=UTF8 > /tmp/initdb.log
gosu postgres pg_ctl -D /tmp/journey-pg -l /tmp/pg.log -o "-k /tmp -c listen_addresses='127.0.0.1'" -w start
trap 'gosu postgres pg_ctl -D /tmp/journey-pg -m fast -w stop' EXIT
printf '\n127.0.0.1 app17c-pg\n' >> /etc/hosts
gosu postgres createuser -h /tmp --superuser app17c_fixture
gosu postgres createdb -h /tmp -O app17c_fixture app17c_fixture
export ConnectionStrings__DefaultConnection='Host=app17c-pg;Database=app17c_fixture;Username=app17c_fixture;Pooling=false'
export Testing__DatabasePrefix=affiliate_full_standard
export Testing__TempRoot=/tmp/affiliate-standard-tests
export DataProtection__KeysPath=/tmp/affiliate-standard-tests/keys
cd /src
/dotnet/dotnet test PointOfSale.Tests/PointOfSale.Tests.csproj --configuration Release --no-restore --no-build \
  --filter FullyQualifiedName~AffiliateJourneyCashboxAuthorityTests \
  --logger 'trx;LogFileName=journey-standard-owned-fixture.trx' --results-directory /src/validation/affiliate-full-e2e/results
