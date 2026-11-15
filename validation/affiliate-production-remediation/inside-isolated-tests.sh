set -euo pipefail
test "$AFFILIATE_RETURN_ISOLATION" = network-none-local-socket
install -d -o postgres -g postgres /tmp/affiliate-pg
gosu postgres initdb -D /tmp/affiliate-pg --auth=trust --no-locale > /tmp/initdb.log
gosu postgres pg_ctl -D /tmp/affiliate-pg -l /tmp/pg.log -o "-k /tmp -c listen_addresses=''" -w start
trap 'gosu postgres pg_ctl -D /tmp/affiliate-pg -m fast -w stop' EXIT
gosu postgres createuser -h /tmp --superuser affiliate_fixture
gosu postgres createdb -h /tmp -O affiliate_fixture affiliate_return_fixture
cd /src
/dotnet/dotnet restore validation/affiliate-production-remediation/AffiliateRemediationTests.csproj --source /packages
/dotnet/dotnet test validation/affiliate-production-remediation/AffiliateRemediationTests.csproj --no-restore -m:1 \
  -p:UseSharedCompilation=false --logger 'trx;LogFileName=affiliate-remediation.trx' --results-directory /src/validation/affiliate-production-remediation/results
psql -h /tmp -U affiliate_fixture -d affiliate_return_fixture -v ON_ERROR_STOP=1 <<'SQL'
CREATE ROLE app NOLOGIN;
CREATE ROLE shopixy_api_runtime_dml_v1 NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE shopixy_api_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
GRANT shopixy_api_runtime_dml_v1 TO shopixy_api_runtime;
ALTER TABLE "AffiliateManualPayoutReturns" OWNER TO app;
ALTER TABLE "AffiliateManualPayoutReturnAllocations" OWNER TO app;
SQL
psql -h /tmp -U affiliate_fixture -d affiliate_return_fixture -v ON_ERROR_STOP=1 -f /src/deploy/affiliate/05-manual-payout-return-runtime-grant.sql
psql -h /tmp -U affiliate_fixture -d affiliate_return_fixture -v ON_ERROR_STOP=1 <<'SQL'
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM (VALUES ('AffiliateManualPayoutReturns'), ('AffiliateManualPayoutReturnAllocations')) AS t(name)
        WHERE NOT has_table_privilege('shopixy_api_runtime', format('public.%I', name), 'SELECT')
           OR NOT has_table_privilege('shopixy_api_runtime', format('public.%I', name), 'INSERT')
           OR has_table_privilege('shopixy_api_runtime', format('public.%I', name), 'UPDATE,DELETE,TRUNCATE')) THEN
        RAISE EXCEPTION 'Runtime audit privileges must be SELECT/INSERT only';
    END IF;
END $$;
SQL
psql -h /tmp -U affiliate_fixture -d affiliate_return_fixture -v ON_ERROR_STOP=1 -f /src/deploy/affiliate/06-manual-payout-return-runtime-revoke.sql
psql -h /tmp -U affiliate_fixture -d affiliate_return_fixture -v ON_ERROR_STOP=1 <<'SQL'
DO $$ BEGIN
    IF has_table_privilege('shopixy_api_runtime', 'public."AffiliateManualPayoutReturns"', 'SELECT,INSERT') THEN
        RAISE EXCEPTION 'Rollback must revoke feature runtime access without dropping history';
    END IF;
END $$;
SQL
psql -h /tmp -U affiliate_fixture -d affiliate_return_fixture -v ON_ERROR_STOP=1 -f /src/deploy/affiliate/05-manual-payout-return-runtime-grant.sql
printf '%s\n' 'PASS: additive migration, append-only guards, narrow runtime grants and retention-preserving grant rollback rehearsed in disposable PostgreSQL.'
