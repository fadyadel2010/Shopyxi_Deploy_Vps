-- Candidate-only immutable source export. This is NOT a materialized search index,
-- external writer fence, output/fan-out acknowledgement or active-reader pointer.
CREATE TABLE marketplace_generation.exports (
    export_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL UNIQUE,
    generation_id uuid NOT NULL REFERENCES marketplace_generation.builds(generation_id),
    journal_id uuid NOT NULL,
    revision bigint NOT NULL CHECK(revision>=0),
    physical_name text NOT NULL,
    physical_uuid text NOT NULL,
    fingerprint text NOT NULL CHECK(fingerprint ~ '^[0-9a-f]{64}$'),
    created_epoch bigint NOT NULL CHECK(created_epoch>0),
    capture_tables text[] NOT NULL,
    input_count bigint NOT NULL CHECK(input_count>=0),
    invalidation_count bigint NOT NULL CHECK(invalidation_count>=0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE marketplace_generation.export_inputs (
    export_id uuid NOT NULL REFERENCES marketplace_generation.exports(export_id) DEFERRABLE INITIALLY DEFERRED,
    source_table text COLLATE "C" NOT NULL,
    source_id uuid NOT NULL,
    revision bigint NOT NULL CHECK(revision>=0),
    ordinal bigint NOT NULL CHECK(ordinal>=0),
    deleted boolean NOT NULL,
    body jsonb NOT NULL,
    PRIMARY KEY(export_id,source_table,source_id),
    CHECK(body ? 'Id' AND source_id=(body->>'Id')::uuid)
);
CREATE TABLE marketplace_generation.export_invalidations (
    export_id uuid NOT NULL REFERENCES marketplace_generation.exports(export_id) DEFERRABLE INITIALLY DEFERRED,
    revision bigint NOT NULL CHECK(revision>0),
    ordinal bigint NOT NULL CHECK(ordinal>0),
    source_table text NOT NULL,
    old_row jsonb,
    new_row jsonb,
    PRIMARY KEY(export_id,revision,ordinal),
    CHECK(old_row IS NOT NULL OR new_row IS NOT NULL)
);
-- Data is inserted before its immutable manifest, in one transaction. The deferred
-- FKs prevent committing an unsealed export. Manifest existence prevents later append.
CREATE FUNCTION marketplace_generation.guard_export_append() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,marketplace_generation AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM marketplace_generation.exports WHERE export_id=NEW.export_id) THEN
        RAISE EXCEPTION 'GENERATION_EXPORT_ALREADY_SEALED' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
CREATE FUNCTION marketplace_generation.reject_export_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    RAISE EXCEPTION 'GENERATION_EXPORT_IMMUTABLE' USING ERRCODE='55000';
END $$;
CREATE TRIGGER guard_append BEFORE INSERT ON marketplace_generation.export_inputs
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_export_append();
CREATE TRIGGER guard_append BEFORE INSERT ON marketplace_generation.export_invalidations
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_export_append();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.export_inputs
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_export_mutation();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.export_invalidations
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_export_mutation();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.exports
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_export_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.export_inputs
    FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_export_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.export_invalidations
    FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_export_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.exports
    FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_export_mutation();
REVOKE ALL ON marketplace_generation.exports,marketplace_generation.export_inputs,
    marketplace_generation.export_invalidations FROM PUBLIC;
REVOKE ALL ON FUNCTION marketplace_generation.guard_export_append(),
    marketplace_generation.reject_export_mutation() FROM PUBLIC;
