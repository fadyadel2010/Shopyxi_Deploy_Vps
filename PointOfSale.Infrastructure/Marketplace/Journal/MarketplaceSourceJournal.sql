-- Candidate schema module, NOT a production migration. Caller owns the installation transaction.
-- @journal_id is supplied once by the guarded installer. No sequence is a commit watermark.
CREATE SCHEMA marketplace_journal;
REVOKE ALL ON SCHEMA marketplace_journal FROM PUBLIC;
CREATE TABLE marketplace_journal.gate (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    journal_id uuid NOT NULL,
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0)
);
INSERT INTO marketplace_journal.gate(singleton,journal_id) VALUES (true,@journal_id);
CREATE TABLE marketplace_journal.changes (
    revision bigint NOT NULL CHECK (revision > 0),
    ordinal bigint GENERATED ALWAYS AS IDENTITY,
    source_table text NOT NULL,
    source_id uuid NOT NULL,
    old_source_id uuid,
    operation text NOT NULL CHECK (operation IN ('INSERT','UPDATE','DELETE')),
    old_row jsonb,
    new_row jsonb,
    database_actor text NOT NULL,
    actor_hint text,
    PRIMARY KEY(revision,ordinal),
    CHECK ((operation='INSERT' AND old_row IS NULL AND new_row IS NOT NULL) OR
           (operation='UPDATE' AND old_row IS NOT NULL AND new_row IS NOT NULL) OR
           (operation='DELETE' AND old_row IS NOT NULL AND new_row IS NULL)),
    CHECK (source_id = coalesce((new_row->>'Id')::uuid,(old_row->>'Id')::uuid))
);
CREATE TABLE marketplace_journal.checkpoints (
    consumer text PRIMARY KEY CHECK(length(consumer) BETWEEN 1 AND 120),
    journal_id uuid NOT NULL,
    revision bigint NOT NULL CHECK (revision >= 0),
    ordinal bigint NOT NULL CHECK (ordinal >= 0)
);
-- Statement BEFORE triggers run even for zero-row DML. This update obtains the single gate
-- tuple lock BEFORE executor row changes, and PostgreSQL retains it through transaction end.
-- UPDATE (not SELECT FOR UPDATE) also causes stale RR/serializable snapshots to abort (40001).
CREATE FUNCTION marketplace_journal.enter_gate() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, marketplace_journal AS $$
BEGIN
    UPDATE marketplace_journal.gate SET revision=revision+1 WHERE singleton;
    IF NOT FOUND THEN RAISE EXCEPTION 'MARKETPLACE_JOURNAL_GATE_MISSING'; END IF;
    RETURN NULL;
END $$;
CREATE FUNCTION marketplace_journal.capture_row() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, marketplace_journal AS $$
DECLARE prior jsonb; following jsonb; token bigint;
BEGIN
    IF TG_OP <> 'INSERT' THEN prior=to_jsonb(OLD); END IF;
    IF TG_OP <> 'DELETE' THEN following=to_jsonb(NEW); END IF;
    SELECT revision INTO STRICT token FROM marketplace_journal.gate WHERE singleton;
    INSERT INTO marketplace_journal.changes
        (revision,source_table,source_id,old_source_id,operation,old_row,new_row,database_actor,actor_hint)
    VALUES(token,TG_TABLE_NAME,coalesce((following->>'Id')::uuid,(prior->>'Id')::uuid),
        (prior->>'Id')::uuid,TG_OP,prior,following,session_user,
        nullif(current_setting('shopixy.actor_id',true),''));
    RETURN NULL;
END $$;
CREATE FUNCTION marketplace_journal.reject_truncate() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN RAISE EXCEPTION 'MARKETPLACE_JOURNAL_TRUNCATE_REJECTED: use journalled DELETE'; END $$;
CREATE TABLE marketplace_journal.capture_identity (
    signature text PRIMARY KEY,
    definition text NOT NULL
);
INSERT INTO marketplace_journal.capture_identity(signature,definition)
SELECT signature,pg_catalog.pg_get_functiondef(pg_catalog.to_regprocedure(signature))
FROM (VALUES ('marketplace_journal.enter_gate()'),('marketplace_journal.capture_row()'),
             ('marketplace_journal.reject_truncate()')) AS functions(signature);
REVOKE ALL ON ALL TABLES IN SCHEMA marketplace_journal FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA marketplace_journal FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA marketplace_journal FROM PUBLIC;
DO $$
DECLARE source text; relation oid;
BEGIN
    FOREACH source IN ARRAY ARRAY['Services','MarketplaceProfiles','ServiceRequests','ServiceReviews',
        'MarketingReviewEvidences','ProviderTrustAggregates','Tenants','UserGovernanceProfiles'] LOOP
        relation=pg_catalog.to_regclass(format('public.%I',source));
        IF relation IS NULL OR NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class WHERE oid=relation AND relkind='r')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_inherits WHERE inhrelid=relation OR inhparent=relation)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_constraint WHERE contype='f' AND conrelid=relation AND confrelid=relation)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid=relation AND NOT tgisinternal
                AND (tgtype & 1)=1 AND (tgtype & 2)=0 AND (tgtype & 64)=0) THEN
            RAISE EXCEPTION 'MARKETPLACE_JOURNAL_SOURCE_TOPOLOGY_UNSUPPORTED: %',source;
        END IF;
        -- Missing tables abort the entire installation. No partial capture or IF EXISTS bypass.
        EXECUTE format('CREATE TRIGGER marketplace_gate BEFORE INSERT OR UPDATE OR DELETE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION marketplace_journal.enter_gate()',source);
        EXECUTE format('CREATE TRIGGER marketplace_capture AFTER INSERT OR UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION marketplace_journal.capture_row()',source);
        EXECUTE format('CREATE TRIGGER marketplace_no_truncate BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION marketplace_journal.reject_truncate()',source);
        EXECUTE format('ALTER TABLE public.%I ENABLE ALWAYS TRIGGER marketplace_gate',source);
        EXECUTE format('ALTER TABLE public.%I ENABLE ALWAYS TRIGGER marketplace_capture',source);
        EXECUTE format('ALTER TABLE public.%I ENABLE ALWAYS TRIGGER marketplace_no_truncate',source);
    END LOOP;
END $$;
