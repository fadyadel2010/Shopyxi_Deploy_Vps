-- Explicit operator-reviewed grant template, not executed by startup or this task.
-- Invoke psql with -v generation_coordinator_role=<existing restricted role>.
-- Run as the trusted migration/function owner. This grants no table mutation,
-- schema ownership, role membership, broad function access or activation.
\if :{?generation_coordinator_role}
BEGIN;
SELECT format('GRANT EXECUTE ON FUNCTION marketplace_generation.lock_source_revision(uuid) TO %I', :'generation_coordinator_role') \gexec
COMMIT;
\else
\echo 'generation_coordinator_role must name an explicitly reviewed existing restricted coordinator'
\quit 3
\endif
