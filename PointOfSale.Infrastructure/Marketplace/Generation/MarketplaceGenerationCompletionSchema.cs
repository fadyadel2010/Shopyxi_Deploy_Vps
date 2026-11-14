namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal static class MarketplaceGenerationCompletionSchema
{
    internal const string Sql = """
        ALTER TABLE marketplace_generation.reader_operations ADD COLUMN deadline timestamptz;
        CREATE TABLE marketplace_generation.delivery_signals (
            operation_id uuid PRIMARY KEY,
            kind text NOT NULL CHECK(kind IN ('service','provider','repair')),
            source_id uuid NOT NULL,
            journal_id uuid NOT NULL,
            source_revision bigint NOT NULL CHECK(source_revision>=0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TRIGGER immutable_delivery_signal BEFORE UPDATE OR DELETE ON marketplace_generation.delivery_signals
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_delivery_signal_truncate BEFORE TRUNCATE ON marketplace_generation.delivery_signals
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        REVOKE ALL ON marketplace_generation.delivery_signals FROM PUBLIC;
        CREATE TABLE marketplace_generation.retention_releases (
            operation_id uuid PRIMARY KEY,
            generation_id uuid NOT NULL REFERENCES marketplace_generation.lifecycle(generation_id),
            journal_id uuid NOT NULL,
            minimum_retention interval NOT NULL CHECK(minimum_retention>=interval '1 day' AND minimum_retention<=interval '365 days'),
            retain_until timestamptz NOT NULL,
            issued_epoch bigint NOT NULL CHECK(issued_epoch>0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TRIGGER immutable_retention_release BEFORE UPDATE OR DELETE ON marketplace_generation.retention_releases
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_retention_release_truncate BEFORE TRUNCATE ON marketplace_generation.retention_releases
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        REVOKE ALL ON marketplace_generation.retention_releases FROM PUBLIC;
        CREATE TABLE marketplace_generation.delivery_attempt_outcomes (
            attempt_id uuid PRIMARY KEY REFERENCES marketplace_generation.delivery_attempts(attempt_id),
            outcome text NOT NULL CHECK(outcome IN ('acknowledged','not-sent')),
            completed_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TRIGGER immutable_attempt_outcome BEFORE UPDATE OR DELETE ON marketplace_generation.delivery_attempt_outcomes
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_attempt_outcome_truncate BEFORE TRUNCATE ON marketplace_generation.delivery_attempt_outcomes
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        REVOKE ALL ON marketplace_generation.delivery_attempt_outcomes FROM PUBLIC;
        CREATE TABLE marketplace_generation.catchup_intents (
            operation_id uuid PRIMARY KEY,
            generation_id uuid NOT NULL REFERENCES marketplace_generation.lifecycle(generation_id),
            journal_id uuid NOT NULL,
            from_revision bigint NOT NULL CHECK(from_revision>=0),
            required_revision bigint NOT NULL CHECK(required_revision>from_revision),
            issued_epoch bigint NOT NULL CHECK(issued_epoch>0),
            state text NOT NULL DEFAULT 'running' CHECK(state IN ('running','completed')),
            run_id uuid REFERENCES marketplace_generation.refresh_observations(run_id),
            CHECK((state='completed')=(run_id IS NOT NULL))
        );
        CREATE UNIQUE INDEX one_generation_catchup ON marketplace_generation.catchup_intents(generation_id) WHERE state='running';
        CREATE FUNCTION marketplace_generation.guard_catchup_outcome() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF TG_OP<>'UPDATE' OR (to_jsonb(NEW)-'state'-'run_id') IS DISTINCT FROM (to_jsonb(OLD)-'state'-'run_id')
              OR OLD.state<>'running' OR NEW.state<>'completed' THEN
                RAISE EXCEPTION 'Immutable catchup outcome' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER immutable_catchup BEFORE UPDATE OR DELETE ON marketplace_generation.catchup_intents
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_catchup_outcome();
        CREATE TRIGGER immutable_catchup_truncate BEFORE TRUNCATE ON marketplace_generation.catchup_intents
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE OR REPLACE FUNCTION marketplace_generation.guard_write_barrier() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF NEW.generation_id<>OLD.generation_id OR NEW.last_epoch<OLD.last_epoch OR NOT (
              (OLD.state='open' AND NEW.state='draining') OR (OLD.state='draining' AND NEW.state='drained') OR
              (OLD.state='drained' AND NEW.state='open' AND NEW.last_epoch>OLD.last_epoch AND EXISTS(
                SELECT 1 FROM marketplace_generation.catchup_intents i
                JOIN marketplace_generation.lifecycle l ON l.generation_id=i.generation_id
                CROSS JOIN marketplace_generation.active_pointer p
                CROSS JOIN marketplace_generation.control c
                WHERE i.generation_id=NEW.generation_id AND i.issued_epoch=NEW.last_epoch AND i.state='running'
                  AND l.state='retained' AND p.singleton AND p.previous_generation=i.generation_id
                  AND p.active_generation IS DISTINCT FROM i.generation_id AND c.singleton
                  AND c.journal_id=i.journal_id AND c.epoch=i.issued_epoch AND c.lease_until>clock_timestamp()))) THEN
                RAISE EXCEPTION 'GENERATION_WRITE_BARRIER_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION marketplace_generation.guard_retirement_outcome() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF TG_OP<>'UPDATE' OR (to_jsonb(NEW)-'state') IS DISTINCT FROM (to_jsonb(OLD)-'state') OR NOT (
              (OLD.state='intent' AND NEW.state='deleted') OR (OLD.state='deleted' AND NEW.state='completed')) THEN
                RAISE EXCEPTION 'Immutable retirement outcome' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER immutable_retirement BEFORE UPDATE OR DELETE ON marketplace_generation.retirement_intents
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_retirement_outcome();
        CREATE TRIGGER immutable_retirement_truncate BEFORE TRUNCATE ON marketplace_generation.retirement_intents
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        REVOKE ALL ON marketplace_generation.catchup_intents FROM PUBLIC;
        REVOKE ALL ON FUNCTION marketplace_generation.guard_catchup_outcome(),marketplace_generation.guard_retirement_outcome() FROM PUBLIC;
        """;
}
