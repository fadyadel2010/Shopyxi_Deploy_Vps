-- Durable outstanding-operation barrier. This is NOT an external delivery or
-- refreshed visibility certificate. Unknown requests never expire with a lease.
CREATE TABLE marketplace_generation.write_barriers (
    generation_id uuid PRIMARY KEY REFERENCES marketplace_generation.builds(generation_id),
    state text NOT NULL DEFAULT 'open' CHECK(state IN ('open','draining','drained')),
    last_epoch bigint NOT NULL CHECK(last_epoch>0)
);
CREATE TABLE marketplace_generation.write_intents (
    operation_id uuid PRIMARY KEY CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'::uuid),
    completion_token uuid NOT NULL UNIQUE,
    generation_id uuid NOT NULL REFERENCES marketplace_generation.write_barriers(generation_id),
    run_id uuid NOT NULL REFERENCES marketplace_generation.output_seals(run_id),
    service_id uuid NOT NULL,
    source_revision bigint NOT NULL CHECK(source_revision>=0),
    physical_uuid text NOT NULL,
    fingerprint text NOT NULL CHECK(fingerprint ~ '^[0-9a-f]{64}$'),
    payload text,
    digest text NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'),
    issued_epoch bigint NOT NULL CHECK(issued_epoch>0),
    state text NOT NULL DEFAULT 'outstanding' CHECK(state IN ('outstanding','acknowledged','not-sent')),
    FOREIGN KEY(run_id,service_id) REFERENCES marketplace_generation.output_rows(run_id,service_id)
);
CREATE UNIQUE INDEX one_outstanding_service_write ON marketplace_generation.write_intents(generation_id,service_id)
    WHERE state='outstanding';
CREATE INDEX outstanding_generation_writes ON marketplace_generation.write_intents(generation_id,state);
CREATE FUNCTION marketplace_generation.guard_write_barrier() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF NEW.generation_id<>OLD.generation_id OR NEW.last_epoch<OLD.last_epoch
      OR NOT ((OLD.state='open' AND NEW.state='draining') OR (OLD.state='draining' AND NEW.state='drained')) THEN
        RAISE EXCEPTION 'GENERATION_WRITE_BARRIER_REJECTED' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
CREATE FUNCTION marketplace_generation.guard_write_outcome() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF (to_jsonb(NEW)-'state') IS DISTINCT FROM (to_jsonb(OLD)-'state')
      OR OLD.state<>'outstanding' OR NEW.state NOT IN ('acknowledged','not-sent') THEN
        RAISE EXCEPTION 'GENERATION_WRITE_OUTCOME_REJECTED' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER progress BEFORE UPDATE ON marketplace_generation.write_barriers FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_write_barrier();
CREATE TRIGGER outcome BEFORE UPDATE ON marketplace_generation.write_intents FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_write_outcome();
CREATE TRIGGER immutable_delete BEFORE DELETE ON marketplace_generation.write_barriers FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_delete BEFORE DELETE ON marketplace_generation.write_intents FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.write_barriers FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.write_intents FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
REVOKE ALL ON marketplace_generation.write_barriers,marketplace_generation.write_intents FROM PUBLIC;
REVOKE ALL ON FUNCTION marketplace_generation.guard_write_barrier(),marketplace_generation.guard_write_outcome() FROM PUBLIC;
