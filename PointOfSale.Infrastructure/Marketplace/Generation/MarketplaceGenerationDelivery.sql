-- Unregistered delivery substrate, NOT readiness/cutover authority. An unknown
-- dispatch is never retried/reclaimed by lease expiry or an observed GET.
CREATE TABLE marketplace_generation.provider_bindings (
    generation_id uuid PRIMARY KEY REFERENCES marketplace_generation.builds(generation_id),
    journal_id uuid NOT NULL,
    cluster_uuid text NOT NULL CHECK(length(cluster_uuid) BETWEEN 1 AND 200 AND cluster_uuid<>'_na_'),
    physical_uuid text NOT NULL,
    fingerprint text NOT NULL CHECK(fingerprint ~ '^[0-9a-f]{64}$')
);
CREATE TABLE marketplace_generation.dispatch_claims (
    operation_id uuid PRIMARY KEY REFERENCES marketplace_generation.write_intents(operation_id),
    claimed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE marketplace_generation.refresh_observations (
    run_id uuid PRIMARY KEY REFERENCES marketplace_generation.output_seals(run_id),
    generation_id uuid NOT NULL REFERENCES marketplace_generation.builds(generation_id),
    journal_id uuid NOT NULL,
    cluster_uuid text NOT NULL CHECK(length(cluster_uuid) BETWEEN 1 AND 200 AND cluster_uuid<>'_na_'),
    physical_uuid text NOT NULL,
    fingerprint text NOT NULL CHECK(fingerprint ~ '^[0-9a-f]{64}$'),
    source_revision bigint NOT NULL CHECK(source_revision>=0),
    seal_digest text NOT NULL CHECK(seal_digest ~ '^[0-9a-f]{64}$'),
    item_count bigint NOT NULL CHECK(item_count>=0),
    observed_epoch bigint NOT NULL CHECK(observed_epoch>0),
    observed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.dispatch_claims
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.dispatch_claims
    FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.refresh_observations
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.refresh_observations
    FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
REVOKE ALL ON marketplace_generation.dispatch_claims,marketplace_generation.refresh_observations FROM PUBLIC;
CREATE TRIGGER immutable_rows BEFORE UPDATE OR DELETE ON marketplace_generation.provider_bindings
    FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
CREATE TRIGGER immutable_truncate BEFORE TRUNCATE ON marketplace_generation.provider_bindings
    FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
REVOKE ALL ON marketplace_generation.provider_bindings FROM PUBLIC;
