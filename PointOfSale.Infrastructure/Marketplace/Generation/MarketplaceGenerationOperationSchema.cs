namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal static class MarketplaceGenerationOperationSchema
{
    internal const string Sql = """
        CREATE TABLE marketplace_generation.recovery_operations (
            operation_id uuid PRIMARY KEY,
            generation_id uuid NOT NULL REFERENCES marketplace_generation.builds(generation_id),
            service_id uuid NOT NULL,
            source_revision bigint NOT NULL CHECK(source_revision>=0),
            identity_json text NOT NULL,
            request_digest text NOT NULL CHECK(request_digest ~ '^[0-9a-f]{64}$'),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(generation_id,service_id,source_revision)
        );
        CREATE TABLE marketplace_generation.recovery_events (
            operation_id uuid NOT NULL REFERENCES marketplace_generation.recovery_operations(operation_id),
            version bigint NOT NULL CHECK(version>0),
            state text NOT NULL CHECK(state IN ('Submitted','OutcomeUnknown','Applied','VisibleAfterRefresh','Acknowledged','ConfirmedRejected')),
            recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY(operation_id,version)
        );
        CREATE FUNCTION marketplace_generation.guard_recovery_event() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        DECLARE prior_state text; prior_version bigint;
        BEGIN
            PERFORM 1 FROM marketplace_generation.recovery_operations WHERE operation_id=NEW.operation_id FOR UPDATE;
            SELECT version,state INTO prior_version,prior_state FROM marketplace_generation.recovery_events
                WHERE operation_id=NEW.operation_id ORDER BY version DESC LIMIT 1;
            prior_version:=coalesce(prior_version,0); prior_state:=coalesce(prior_state,'Pending');
            IF NEW.version<>prior_version+1 OR NOT (
                (prior_state='Pending' AND NEW.state='Submitted') OR
                (prior_state='Submitted' AND NEW.state IN ('OutcomeUnknown','Applied','ConfirmedRejected')) OR
                (prior_state='OutcomeUnknown' AND NEW.state IN ('Submitted','Applied','ConfirmedRejected')) OR
                (prior_state='Applied' AND NEW.state='VisibleAfterRefresh') OR
                (prior_state='VisibleAfterRefresh' AND NEW.state='Acknowledged')) THEN
                RAISE EXCEPTION 'GENERATION_RECOVERY_TRANSITION_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER validate_recovery_event BEFORE INSERT ON marketplace_generation.recovery_events
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_recovery_event();
        CREATE TRIGGER immutable_recovery_identity BEFORE UPDATE OR DELETE ON marketplace_generation.recovery_operations
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_recovery_event BEFORE UPDATE OR DELETE ON marketplace_generation.recovery_events
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_recovery_identity_truncate BEFORE TRUNCATE ON marketplace_generation.recovery_operations
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_recovery_event_truncate BEFORE TRUNCATE ON marketplace_generation.recovery_events
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        REVOKE ALL ON marketplace_generation.recovery_operations,marketplace_generation.recovery_events FROM PUBLIC;
        REVOKE ALL ON FUNCTION marketplace_generation.guard_recovery_event() FROM PUBLIC;
        """;
}
