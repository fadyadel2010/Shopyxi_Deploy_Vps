set -euo pipefail
test "$AFFILIATE_FULL_TEST_ISOLATION" = network-none-local-socket
install -d -o postgres -g postgres /tmp/journey-pg
gosu postgres initdb -D /tmp/journey-pg --auth=trust --no-locale --encoding=UTF8 > /tmp/initdb.log
gosu postgres pg_ctl -D /tmp/journey-pg -l /tmp/pg.log -o "-k /tmp -c listen_addresses=''" -w start
trap 'gosu postgres pg_ctl -D /tmp/journey-pg -m fast -w stop' EXIT
gosu postgres createuser -h /tmp --superuser affiliate_fixture
gosu postgres createdb -h /tmp -O affiliate_fixture affiliate_journey_regression
cd /src
/dotnet/dotnet restore validation/affiliate-full-e2e/AffiliateJourneyTests.csproj --source /packages
/dotnet/dotnet test validation/affiliate-full-e2e/AffiliateJourneyTests.csproj --no-restore -m:1 -p:UseSharedCompilation=false \
  --logger "trx;LogFileName=journey-$JOURNEY_TEST_PHASE.trx" --results-directory /src/validation/affiliate-full-e2e/results
