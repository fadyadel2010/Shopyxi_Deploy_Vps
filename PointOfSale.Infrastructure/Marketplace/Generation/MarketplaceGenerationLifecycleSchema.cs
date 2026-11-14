namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// No startup installation. The lifecycle extends existing immutable build/delivery
/// history; it never rewrites journal rows, source tables, output seals or tombstones.
/// </summary>
internal static class MarketplaceGenerationLifecycleSchema
{
    internal const string Sql = """
        CREATE TABLE marketplace_generation.lifecycle (
            generation_id uuid PRIMARY KEY REFERENCES marketplace_generation.builds(generation_id),
            state text NOT NULL DEFAULT 'building' CHECK(state IN ('building','verified','active','retained','retiring','retired')),
            retain_until timestamptz NOT NULL DEFAULT 'infinity',
            retired_at timestamptz,
            CHECK ((state='retired') = (retired_at IS NOT NULL))
        );
        CREATE TABLE marketplace_generation.active_pointer (
            singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
            journal_id uuid NOT NULL,
            version bigint NOT NULL DEFAULT 0 CHECK(version>=0),
            active_generation uuid REFERENCES marketplace_generation.lifecycle(generation_id),
            previous_generation uuid REFERENCES marketplace_generation.lifecycle(generation_id),
            operation_id uuid,
            CHECK(active_generation IS DISTINCT FROM previous_generation OR active_generation IS NULL)
        );
        INSERT INTO marketplace_generation.active_pointer(singleton,journal_id)
            SELECT true,journal_id FROM marketplace_generation.control WHERE singleton;
        -- Readers may acquire a serialization lock without UPDATE authority on
        -- the authoritative pointer. Never grant pointer DML to a reader role.
        CREATE FUNCTION marketplace_generation.lock_reader_pointer() RETURNS bigint
        LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
            SELECT version FROM marketplace_generation.active_pointer WHERE singleton FOR SHARE
        $$;
        REVOKE ALL ON FUNCTION marketplace_generation.lock_reader_pointer() FROM PUBLIC;
        CREATE FUNCTION marketplace_generation.lock_source_revision(expected_journal uuid) RETURNS bigint
        LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
            SELECT revision FROM marketplace_journal.gate WHERE singleton AND journal_id=expected_journal FOR SHARE
        $$;
        REVOKE ALL ON FUNCTION marketplace_generation.lock_source_revision(uuid) FROM PUBLIC;
        CREATE TABLE marketplace_generation.pointer_intents (
            operation_id uuid PRIMARY KEY,
            kind text NOT NULL CHECK(kind IN ('cutover','rollback')),
            journal_id uuid NOT NULL,
            expected_version bigint NOT NULL CHECK(expected_version>=0 AND expected_version<9223372036854775807),
            expected_active uuid REFERENCES marketplace_generation.lifecycle(generation_id),
            target_generation uuid NOT NULL REFERENCES marketplace_generation.lifecycle(generation_id),
            required_revision bigint NOT NULL CHECK(required_revision>=0),
            run_id uuid NOT NULL REFERENCES marketplace_generation.refresh_observations(run_id),
            issued_epoch bigint NOT NULL CHECK(issued_epoch>0),
            state text NOT NULL DEFAULT 'intent' CHECK(state IN ('intent','committed','completed','superseded')),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(target_generation IS DISTINCT FROM expected_active)
        );
        CREATE TABLE marketplace_generation.reader_pins (
            pin_id uuid PRIMARY KEY,
            secret_hash text NOT NULL CHECK(secret_hash ~ '^[0-9a-f]{64}$'),
            generation_id uuid NOT NULL REFERENCES marketplace_generation.lifecycle(generation_id),
            journal_id uuid NOT NULL,
            pointer_version bigint NOT NULL CHECK(pointer_version>0),
            tenant_id uuid NOT NULL,
            reader_id uuid NOT NULL,
            query_hash text NOT NULL CHECK(query_hash ~ '^[0-9a-f]{64}$'),
            expires_at timestamptz NOT NULL,
            released_at timestamptz
        );
        CREATE INDEX reader_pins_generation ON marketplace_generation.reader_pins(generation_id,expires_at)
            WHERE released_at IS NULL;
        -- An admitted request has its own durable reference. Pin expiry alone cannot
        -- erase an in-flight external query with an unknown completion outcome.
        CREATE TABLE marketplace_generation.reader_operations (
            operation_id uuid PRIMARY KEY,
            pin_id uuid NOT NULL REFERENCES marketplace_generation.reader_pins(pin_id),
            admitted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            completed_at timestamptz
        );
        CREATE TABLE marketplace_generation.retirement_intents (
            operation_id uuid PRIMARY KEY,
            generation_id uuid NOT NULL UNIQUE REFERENCES marketplace_generation.lifecycle(generation_id),
            physical_uuid text NOT NULL,
            cluster_uuid text NOT NULL,
            fingerprint text NOT NULL CHECK(fingerprint ~ '^[0-9a-f]{64}$'),
            state text NOT NULL DEFAULT 'intent' CHECK(state IN ('intent','deleted','completed')),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE marketplace_generation.delivery_attempts (
            attempt_id uuid PRIMARY KEY,
            operation_id uuid NOT NULL REFERENCES marketplace_generation.write_intents(operation_id),
            request_hash text NOT NULL CHECK(request_hash ~ '^[0-9a-f]{64}$'),
            admitted_epoch bigint NOT NULL CHECK(admitted_epoch>0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TRIGGER immutable_delivery_attempt BEFORE UPDATE OR DELETE ON marketplace_generation.delivery_attempts
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_delivery_attempt_truncate BEFORE TRUNCATE ON marketplace_generation.delivery_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE FUNCTION marketplace_generation.guard_pointer_intent() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF TG_OP<>'UPDATE' THEN RAISE EXCEPTION 'Immutable pointer history' USING ERRCODE='55000'; END IF;
            IF (to_jsonb(NEW)-'state') IS DISTINCT FROM (to_jsonb(OLD)-'state') OR
               NOT ((OLD.state='intent' AND NEW.state IN ('committed','superseded')) OR
                    (OLD.state='committed' AND NEW.state='completed')) THEN
                RAISE EXCEPTION 'Illegal pointer outcome transition' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER immutable_pointer_intent BEFORE UPDATE OR DELETE ON marketplace_generation.pointer_intents
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_pointer_intent();
        CREATE TRIGGER immutable_pointer_intent_truncate BEFORE TRUNCATE ON marketplace_generation.pointer_intents
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        REVOKE ALL ON marketplace_generation.lifecycle,marketplace_generation.active_pointer,
            marketplace_generation.pointer_intents,marketplace_generation.reader_pins,
            marketplace_generation.reader_operations,marketplace_generation.retirement_intents,
            marketplace_generation.delivery_attempts FROM PUBLIC;
        REVOKE ALL ON FUNCTION marketplace_generation.guard_pointer_intent() FROM PUBLIC;
        """;
}
