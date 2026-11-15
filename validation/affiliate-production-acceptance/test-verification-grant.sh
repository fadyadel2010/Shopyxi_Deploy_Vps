set -euo pipefail
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
root=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source
docker=/opt/shopixy-toolchain/bin/docker
image=postgres:18.6
test "$($docker image inspect "$image" --format '{{.Id}}')" = sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42
$docker run --rm --pull=never --network=none --memory=512m --cpus=1 --pids-limit=128 \
    --mount "type=bind,source=$root/deploy/affiliate,target=/release,readonly" \
    --entrypoint /bin/bash -i "$image" -s <<'LOCAL_TEST'
set -euo pipefail
gosu postgres initdb -D /tmp/affiliate-acceptance-pg --auth=trust >/dev/null
gosu postgres pg_ctl -D /tmp/affiliate-acceptance-pg -o "-c listen_addresses='' -k /tmp" -w start >/dev/null
trap 'gosu postgres pg_ctl -D /tmp/affiliate-acceptance-pg -m fast -w stop >/dev/null' EXIT
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 <<'SETUP'
CREATE ROLE shopixy_api_runtime NOLOGIN;
CREATE ROLE shopixy_api_runtime_dml_v1 NOLOGIN;
CREATE ROLE app NOLOGIN;
GRANT shopixy_api_runtime_dml_v1 TO shopixy_api_runtime;
CREATE TABLE "AffiliateEmailVerificationTokens" ("Id" integer PRIMARY KEY, "AffiliateUserId" integer NOT NULL, "ConsumedAtUtc" timestamptz);
CREATE TABLE "AffiliateUsers" ("Id" integer PRIMARY KEY);
ALTER TABLE "AffiliateEmailVerificationTokens" OWNER TO app;
CREATE TABLE "SyntheticQaOrders" ("OrderId" integer, "RunId" integer);
CREATE TABLE "SyntheticQaEvidence" ("OrderId" integer, "Signature" text);
CREATE TABLE "SyntheticQaCommissionPostings" ("OrderId" integer, "Amount" numeric);
ALTER TABLE "SyntheticQaOrders" OWNER TO app;
ALTER TABLE "SyntheticQaEvidence" OWNER TO app;
ALTER TABLE "SyntheticQaCommissionPostings" OWNER TO app;
GRANT SELECT ("OrderId") ON "SyntheticQaOrders" TO shopixy_api_runtime_dml_v1;
GRANT SELECT,INSERT,UPDATE ON "AffiliateEmailVerificationTokens" TO shopixy_api_runtime_dml_v1;
INSERT INTO "AffiliateEmailVerificationTokens" VALUES (1,1,NULL),(2,2,NULL),(3,1,now());
SET ROLE shopixy_api_runtime;
SELECT 1 FROM "AffiliateEmailVerificationTokens" WHERE false;
SET ROLE postgres;
SET default_transaction_read_only=off;
SETUP
if psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -c 'SET ROLE shopixy_api_runtime; DELETE FROM "AffiliateEmailVerificationTokens" WHERE "AffiliateUserId"=999;' >/tmp/denied.out 2>&1; then
    exit 1
fi
grep -q 'permission denied for table AffiliateEmailVerificationTokens' /tmp/denied.out
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -f /release/01-verification-token-runtime-grant.sql
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -f /release/01-verification-token-runtime-grant.sql
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 <<'VERIFY'
DO $$ BEGIN
 IF (SELECT count(*) FROM "AffiliateEmailVerificationTokens") <> 3 THEN RAISE EXCEPTION 'Grant changed data'; END IF;
 IF has_table_privilege('shopixy_api_runtime','"AffiliateUsers"','DELETE') THEN RAISE EXCEPTION 'Unrelated permission broadened'; END IF;
 IF has_table_privilege('shopixy_api_runtime','"AffiliateEmailVerificationTokens"','TRUNCATE') THEN RAISE EXCEPTION 'Truncate broadened'; END IF;
END $$;
BEGIN;
SET LOCAL ROLE shopixy_api_runtime;
DELETE FROM "AffiliateEmailVerificationTokens" WHERE "AffiliateUserId"=999;
DELETE FROM "AffiliateEmailVerificationTokens" WHERE "AffiliateUserId"=1 AND "ConsumedAtUtc" IS NULL;
INSERT INTO "AffiliateEmailVerificationTokens" VALUES (4,1,NULL);
COMMIT;
DO $$ BEGIN
 IF (SELECT count(*) FROM "AffiliateEmailVerificationTokens") <> 3 OR
    EXISTS(SELECT 1 FROM "AffiliateEmailVerificationTokens" WHERE "Id"=1) OR
    NOT EXISTS(SELECT 1 FROM "AffiliateEmailVerificationTokens" WHERE "Id"=2) OR
    NOT EXISTS(SELECT 1 FROM "AffiliateEmailVerificationTokens" WHERE "Id"=3) OR
    NOT EXISTS(SELECT 1 FROM "AffiliateEmailVerificationTokens" WHERE "Id"=4) THEN
    RAISE EXCEPTION 'Token replacement not scoped to unconsumed owned tokens'; END IF;
END $$;
VERIFY
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -f /release/02-verification-token-runtime-grant-rollback.sql
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 <<'UNDO'
DO $$ BEGIN
 IF has_table_privilege('shopixy_api_runtime','"AffiliateEmailVerificationTokens"','DELETE') THEN RAISE EXCEPTION 'Rollback did not restore denial'; END IF;
 IF (SELECT count(*) FROM "AffiliateEmailVerificationTokens") <> 3 THEN RAISE EXCEPTION 'Rollback changed token rows'; END IF;
END $$;
UNDO
if psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -c 'SET ROLE shopixy_api_runtime; SELECT "OrderId" FROM "SyntheticQaEvidence";' >/tmp/classification-denied.out 2>&1; then exit 1; fi
grep -q 'permission denied for table SyntheticQaEvidence' /tmp/classification-denied.out
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -f /release/03-commission-classification-read-grant.sql
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -f /release/03-commission-classification-read-grant.sql
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 <<'CLASSIFICATION'
SET ROLE shopixy_api_runtime;
SELECT "OrderId" FROM "SyntheticQaOrders" UNION SELECT "OrderId" FROM "SyntheticQaEvidence" UNION SELECT "OrderId" FROM "SyntheticQaCommissionPostings";
SET ROLE postgres;
DO $$ BEGIN
 IF has_column_privilege('shopixy_api_runtime','"SyntheticQaEvidence"','Signature','SELECT') OR
    has_column_privilege('shopixy_api_runtime','"SyntheticQaCommissionPostings"','Amount','SELECT') OR
    has_table_privilege('shopixy_api_runtime','"SyntheticQaOrders"','INSERT') OR
    has_table_privilege('shopixy_api_runtime','"SyntheticQaEvidence"','UPDATE') OR
    has_table_privilege('shopixy_api_runtime','"SyntheticQaCommissionPostings"','DELETE') THEN
    RAISE EXCEPTION 'Classification grant broadened unrelated access'; END IF;
END $$;
CLASSIFICATION
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 -f /release/04-commission-classification-read-rollback.sql
psql -h /tmp -U postgres -X -q -v ON_ERROR_STOP=1 <<'CLASSIFICATION_UNDO'
DO $$ BEGIN
 IF has_column_privilege('shopixy_api_runtime','"SyntheticQaEvidence"','OrderId','SELECT') THEN
    RAISE EXCEPTION 'Classification rollback failed'; END IF;
 IF NOT has_column_privilege('shopixy_api_runtime','"SyntheticQaOrders"','OrderId','SELECT') THEN
    RAISE EXCEPTION 'Rollback removed a pre-existing grant'; END IF;
END $$;
CLASSIFICATION_UNDO
printf '%s\n' '{"passed":true,"engine":"PostgreSQL18.6","network":"none","productionDataUsed":false,"checks":["pre-fix zero-row DELETE denied","exact grant applied twice","grant leaves rows unchanged","same-user outstanding-token replacement succeeds","other-user and consumed tokens retained","unrelated DELETE and TRUNCATE denied","ACL rollback restores denial without deleting rows"]}'
printf '%s\n' '{"classificationPassed":true,"checks":["pre-fix classification SELECT denied","column-only grants applied twice","exact classification union succeeds","signature and amount access denied","synthetic writes denied","column grant rollback restores denial"]}'
LOCAL_TEST
