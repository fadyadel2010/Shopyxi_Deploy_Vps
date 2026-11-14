-- Candidate LOCAL composition receipts only. None is an external delivery,
-- refreshed-index acknowledgement, reader pin, ready pointer or cutover state.
CREATE TABLE marketplace_generation.output_runs (
    run_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL UNIQUE,
    export_id uuid NOT NULL REFERENCES marketplace_generation.exports(export_id),
    composer_fingerprint text NOT NULL CHECK(composer_fingerprint ~ '^[0-9a-f]{64}$'),
    next_page bigint NOT NULL DEFAULT 0 CHECK(next_page>=0),
    after_service uuid,
    exhausted boolean NOT NULL DEFAULT false,
    item_count bigint NOT NULL DEFAULT 0 CHECK(item_count>=0),
    created_epoch bigint NOT NULL CHECK(created_epoch>0),
    last_epoch bigint NOT NULL CHECK(last_epoch>=created_epoch)
);
CREATE TABLE marketplace_generation.output_pages (
    run_id uuid NOT NULL REFERENCES marketplace_generation.output_runs(run_id),
    page_number bigint NOT NULL CHECK(page_number>=0),
    operation_id uuid NOT NULL UNIQUE,
    expected_after uuid,
    after_service uuid,
    page_limit integer NOT NULL CHECK(page_limit BETWEEN 1 AND 128),
    row_count integer NOT NULL CHECK(row_count BETWEEN 0 AND page_limit),
    exhausted boolean NOT NULL,
    digest text NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'),
    created_epoch bigint NOT NULL CHECK(created_epoch>0),
    PRIMARY KEY(run_id,page_number),
    CHECK(exhausted=(row_count<page_limit))
);
CREATE TABLE marketplace_generation.output_rows (
    run_id uuid NOT NULL,
    page_number bigint NOT NULL,
    service_id uuid NOT NULL CHECK(service_id<>'00000000-0000-0000-0000-000000000000'::uuid),
    payload text,
    digest text NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'),
    PRIMARY KEY(run_id,service_id),
    FOREIGN KEY(run_id,page_number) REFERENCES marketplace_generation.output_pages(run_id,page_number) DEFERRABLE INITIALLY DEFERRED,
    CHECK(payload IS NULL OR jsonb_typeof(payload::jsonb)='object')
);
CREATE INDEX ON marketplace_generation.output_rows(run_id,page_number,service_id);
CREATE TABLE marketplace_generation.output_seals (
    run_id uuid PRIMARY KEY REFERENCES marketplace_generation.output_runs(run_id),
    operation_id uuid NOT NULL UNIQUE,
    page_count bigint NOT NULL CHECK(page_count>0),
    item_count bigint NOT NULL CHECK(item_count>=0),
    digest text NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'),
    created_epoch bigint NOT NULL CHECK(created_epoch>0),
    UNIQUE(run_id,digest)
);
CREATE TABLE marketplace_generation.output_local_acks (
    run_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL UNIQUE,
    seal_digest text NOT NULL,
    created_epoch bigint NOT NULL CHECK(created_epoch>0),
    purpose text NOT NULL DEFAULT 'local-composition' CHECK(purpose='local-composition'),
    FOREIGN KEY(run_id,seal_digest) REFERENCES marketplace_generation.output_seals(run_id,digest)
);
CREATE FUNCTION marketplace_generation.reject_output_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'GENERATION_LOCAL_OUTPUT_IMMUTABLE' USING ERRCODE='55000'; END $$;
CREATE FUNCTION marketplace_generation.guard_output_progress() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,marketplace_generation AS $$
BEGIN
    IF (NEW.run_id,NEW.operation_id,NEW.export_id,NEW.composer_fingerprint,NEW.created_epoch)
        IS DISTINCT FROM (OLD.run_id,OLD.operation_id,OLD.export_id,OLD.composer_fingerprint,OLD.created_epoch)
      OR OLD.exhausted OR NEW.next_page<>OLD.next_page+1 OR NEW.item_count<OLD.item_count OR NEW.last_epoch<OLD.last_epoch
      OR EXISTS(SELECT 1 FROM marketplace_generation.output_seals WHERE run_id=OLD.run_id) THEN
        RAISE EXCEPTION 'GENERATION_LOCAL_OUTPUT_PROGRESS_REJECTED' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
CREATE FUNCTION marketplace_generation.guard_output_append() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,marketplace_generation AS $$
DECLARE r marketplace_generation.output_runs%ROWTYPE;
BEGIN
    SELECT * INTO r FROM marketplace_generation.output_runs WHERE run_id=NEW.run_id FOR UPDATE;
    IF NOT FOUND OR r.exhausted OR NEW.page_number<>r.next_page
      OR EXISTS(SELECT 1 FROM marketplace_generation.output_seals WHERE run_id=NEW.run_id) THEN
        RAISE EXCEPTION 'GENERATION_LOCAL_OUTPUT_APPEND_REJECTED' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
CREATE FUNCTION marketplace_generation.guard_output_seal() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,marketplace_generation AS $$
DECLARE r marketplace_generation.output_runs%ROWTYPE;
BEGIN
    SELECT * INTO r FROM marketplace_generation.output_runs WHERE run_id=NEW.run_id FOR UPDATE;
    IF NOT FOUND OR NOT r.exhausted OR NEW.page_count<>r.next_page OR NEW.item_count<>r.item_count THEN
        RAISE EXCEPTION 'GENERATION_LOCAL_OUTPUT_INCOMPLETE' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER progress BEFORE UPDATE ON marketplace_generation.output_runs FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_output_progress();
CREATE TRIGGER immutable_delete BEFORE DELETE ON marketplace_generation.output_runs FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER append BEFORE INSERT ON marketplace_generation.output_rows FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_output_append();
CREATE TRIGGER append BEFORE INSERT ON marketplace_generation.output_pages FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_output_append();
CREATE TRIGGER seal BEFORE INSERT ON marketplace_generation.output_seals FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_output_seal();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.output_rows FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.output_pages FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.output_seals FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.output_local_acks FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.output_runs FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.output_rows FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.output_pages FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.output_seals FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.output_local_acks FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
REVOKE ALL ON marketplace_generation.output_runs,marketplace_generation.output_rows,marketplace_generation.output_pages,
    marketplace_generation.output_seals,marketplace_generation.output_local_acks FROM PUBLIC;
REVOKE ALL ON FUNCTION marketplace_generation.reject_output_mutation(),marketplace_generation.guard_output_progress(),
    marketplace_generation.guard_output_append(),marketplace_generation.guard_output_seal() FROM PUBLIC;
