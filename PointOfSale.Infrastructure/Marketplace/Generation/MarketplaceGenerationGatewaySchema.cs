namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal static class MarketplaceGenerationGatewaySchema
{
    internal const string Sql = """
        CREATE TABLE marketplace_generation.gateway_attempts (
            attempt_id uuid PRIMARY KEY,
            operation_id uuid NOT NULL REFERENCES marketplace_generation.recovery_operations(operation_id),
            epoch bigint NOT NULL CHECK(epoch>0),
            request_digest text NOT NULL CHECK(request_digest ~ '^[0-9a-f]{64}$'),
            admitted_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE marketplace_generation.gateway_settlements (
            attempt_id uuid PRIMARY KEY REFERENCES marketplace_generation.gateway_attempts(attempt_id),
            request_digest text NOT NULL CHECK(request_digest ~ '^[0-9a-f]{64}$'),
            witness_operation_id uuid NOT NULL REFERENCES marketplace_generation.recovery_operations(operation_id),
            witness_request_digest text NOT NULL CHECK(witness_request_digest ~ '^[0-9a-f]{64}$'),
            verified_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE marketplace_generation.gateway_supersessions (
            operation_id uuid NOT NULL REFERENCES marketplace_generation.recovery_operations(operation_id),
            witness_operation_id uuid NOT NULL REFERENCES marketplace_generation.recovery_operations(operation_id),
            witness_request_digest text NOT NULL CHECK(witness_request_digest ~ '^[0-9a-f]{64}$'),
            refreshed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY(operation_id,witness_operation_id)
        );
        CREATE FUNCTION marketplace_generation.guard_gateway_admission() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        DECLARE c record; r record;
        BEGIN
            SELECT * INTO STRICT c FROM marketplace_generation.control WHERE singleton FOR UPDATE;
            SELECT * INTO STRICT r FROM marketplace_generation.recovery_operations WHERE operation_id=NEW.operation_id;
            IF c.epoch<>NEW.epoch OR c.owner_id IS NULL OR c.lease_until<=clock_timestamp()
                OR r.request_digest<>NEW.request_digest
                OR (r.identity_json::jsonb#>>'{Ticket,Epoch}')::bigint IS DISTINCT FROM NEW.epoch THEN
                RAISE EXCEPTION 'GENERATION_GATEWAY_ADMISSION_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION marketplace_generation.guard_gateway_epoch() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF current_setting('fsync')<>'on' OR current_setting('full_page_writes')<>'on' OR pg_is_in_recovery() THEN
                RAISE EXCEPTION 'GENERATION_DURABLE_PRIMARY_REQUIRED' USING ERRCODE='55000';
            END IF;
            PERFORM set_config('synchronous_commit','on',true);
            IF NEW.epoch<OLD.epoch OR NEW.journal_id<>OLD.journal_id OR NEW.singleton<>OLD.singleton
                OR (NEW.owner_id IS DISTINCT FROM OLD.owner_id AND NEW.owner_id IS NOT NULL AND NEW.epoch<=OLD.epoch) THEN
                RAISE EXCEPTION 'GENERATION_GATEWAY_AUTHORITY_REGRESSION' USING ERRCODE='55000';
            END IF;
            IF NEW.epoch<>OLD.epoch AND (EXISTS(SELECT 1 FROM marketplace_generation.gateway_attempts a
                WHERE NOT EXISTS(SELECT 1 FROM marketplace_generation.gateway_settlements s
                    WHERE s.attempt_id=a.attempt_id AND s.request_digest=a.request_digest))
                OR EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempts a
                    WHERE (a.attempt_id<>a.operation_id OR NOT EXISTS(
                        SELECT 1 FROM marketplace_generation.recovery_operations r WHERE r.operation_id=a.operation_id))
                    AND NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempt_outcomes o WHERE o.attempt_id=a.attempt_id))) THEN
                RAISE EXCEPTION 'GENERATION_GATEWAY_UNRESOLVED_REQUESTS' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION marketplace_generation.guard_gateway_pointer() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF current_setting('fsync')<>'on' OR current_setting('full_page_writes')<>'on' OR pg_is_in_recovery() THEN
                RAISE EXCEPTION 'GENERATION_DURABLE_PRIMARY_REQUIRED' USING ERRCODE='55000';
            END IF;
            PERFORM set_config('synchronous_commit','on',true);
            PERFORM 1 FROM marketplace_generation.control WHERE singleton FOR UPDATE;
            IF EXISTS(SELECT 1 FROM marketplace_generation.gateway_attempts a
                WHERE NOT EXISTS(SELECT 1 FROM marketplace_generation.gateway_settlements s
                    WHERE s.attempt_id=a.attempt_id AND s.request_digest=a.request_digest)) THEN
                RAISE EXCEPTION 'GENERATION_GATEWAY_CUTOVER_UNDRAINED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION marketplace_generation.guard_gateway_settlement() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM marketplace_generation.gateway_attempts a
                JOIN marketplace_generation.recovery_operations r ON r.operation_id=a.operation_id
                JOIN marketplace_generation.recovery_operations w ON w.operation_id=NEW.witness_operation_id
                WHERE a.attempt_id=NEW.attempt_id AND a.request_digest=NEW.request_digest
                    AND w.request_digest=NEW.witness_request_digest AND w.generation_id=r.generation_id AND w.service_id=r.service_id
                    AND (w.source_revision>r.source_revision OR w.operation_id=r.operation_id)
                    AND w.identity_json::jsonb->'Target'=r.identity_json::jsonb->'Target'
                    AND (w.identity_json::jsonb#>>'{Ticket,Epoch}')::bigint >= (r.identity_json::jsonb#>>'{Ticket,Epoch}')::bigint) THEN
                RAISE EXCEPTION 'GENERATION_GATEWAY_SETTLEMENT_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION marketplace_generation.guard_gateway_supersession() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM marketplace_generation.recovery_operations r
                JOIN marketplace_generation.recovery_operations w ON w.operation_id=NEW.witness_operation_id
                WHERE r.operation_id=NEW.operation_id AND w.request_digest=NEW.witness_request_digest
                    AND w.generation_id=r.generation_id AND w.service_id=r.service_id AND w.source_revision>r.source_revision
                    AND w.identity_json::jsonb->'Target'=r.identity_json::jsonb->'Target'
                    AND (w.identity_json::jsonb#>>'{Ticket,Epoch}')::bigint >= (r.identity_json::jsonb#>>'{Ticket,Epoch}')::bigint) THEN
                RAISE EXCEPTION 'GENERATION_GATEWAY_SUPERSESSION_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER gateway_admission BEFORE INSERT ON marketplace_generation.gateway_attempts
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_gateway_admission();
        CREATE TRIGGER gateway_epoch BEFORE UPDATE ON marketplace_generation.control
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_gateway_epoch();
        CREATE TRIGGER immutable_gateway_control_delete BEFORE DELETE ON marketplace_generation.control
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_gateway_control_truncate BEFORE TRUNCATE ON marketplace_generation.control
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER gateway_pointer BEFORE UPDATE ON marketplace_generation.active_pointer
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_gateway_pointer();
        CREATE TRIGGER gateway_settlement BEFORE INSERT ON marketplace_generation.gateway_settlements
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_gateway_settlement();
        CREATE TRIGGER gateway_supersession BEFORE INSERT ON marketplace_generation.gateway_supersessions
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.guard_gateway_supersession();
        CREATE TRIGGER immutable_gateway_attempt BEFORE UPDATE OR DELETE ON marketplace_generation.gateway_attempts
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_gateway_settlement BEFORE UPDATE OR DELETE ON marketplace_generation.gateway_settlements
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_gateway_attempt_truncate BEFORE TRUNCATE ON marketplace_generation.gateway_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_gateway_settlement_truncate BEFORE TRUNCATE ON marketplace_generation.gateway_settlements
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_gateway_supersession BEFORE UPDATE OR DELETE ON marketplace_generation.gateway_supersessions
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        CREATE TRIGGER immutable_gateway_supersession_truncate BEFORE TRUNCATE ON marketplace_generation.gateway_supersessions
            FOR EACH STATEMENT EXECUTE FUNCTION marketplace_generation.reject_output_mutation();
        ALTER TABLE marketplace_generation.recovery_events DROP CONSTRAINT recovery_events_state_check;
        ALTER TABLE marketplace_generation.recovery_events ADD CHECK(state IN
            ('Submitted','OutcomeUnknown','Applied','VisibleAfterRefresh','Acknowledged','ConfirmedRejected','SupersededAfterRefresh'));
        CREATE OR REPLACE FUNCTION marketplace_generation.guard_recovery_event() RETURNS trigger
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
                (prior_state='VisibleAfterRefresh' AND NEW.state='Acknowledged') OR
                (prior_state IN ('Submitted','OutcomeUnknown','Applied','VisibleAfterRefresh','Acknowledged') AND NEW.state='SupersededAfterRefresh'
                    AND EXISTS(SELECT 1 FROM marketplace_generation.gateway_supersessions s
                        JOIN marketplace_generation.recovery_operations r ON r.operation_id=s.operation_id
                        JOIN marketplace_generation.recovery_operations w ON w.operation_id=s.witness_operation_id
                        WHERE r.operation_id=NEW.operation_id AND w.generation_id=r.generation_id AND w.service_id=r.service_id
                            AND w.source_revision>r.source_revision AND w.request_digest=s.witness_request_digest))) THEN
                RAISE EXCEPTION 'GENERATION_RECOVERY_TRANSITION_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        ALTER TABLE marketplace_generation.write_intents DROP CONSTRAINT write_intents_state_check;
        ALTER TABLE marketplace_generation.write_intents ADD CHECK(state IN ('outstanding','acknowledged','not-sent','superseded'));
        ALTER TABLE marketplace_generation.delivery_attempt_outcomes DROP CONSTRAINT delivery_attempt_outcomes_outcome_check;
        ALTER TABLE marketplace_generation.delivery_attempt_outcomes ADD CHECK(outcome IN ('acknowledged','not-sent','superseded'));
        CREATE OR REPLACE FUNCTION marketplace_generation.guard_write_outcome() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
            IF (to_jsonb(NEW)-'state') IS DISTINCT FROM (to_jsonb(OLD)-'state') OR OLD.state<>'outstanding'
                OR (NEW.state NOT IN ('acknowledged','not-sent') AND NOT (
                    NEW.state='superseded' AND EXISTS(SELECT 1 FROM marketplace_generation.recovery_events e
                        WHERE e.operation_id=NEW.operation_id AND e.state='SupersededAfterRefresh')
                    AND NOT EXISTS(SELECT 1 FROM marketplace_generation.gateway_attempts a
                        WHERE a.operation_id=NEW.operation_id AND NOT EXISTS(
                            SELECT 1 FROM marketplace_generation.gateway_settlements s WHERE s.attempt_id=a.attempt_id)))) THEN
                RAISE EXCEPTION 'GENERATION_WRITE_OUTCOME_REJECTED' USING ERRCODE='55000';
            END IF;
            RETURN NEW;
        END $$;
        REVOKE ALL ON marketplace_generation.gateway_attempts,marketplace_generation.gateway_settlements,marketplace_generation.gateway_supersessions FROM PUBLIC;
        REVOKE ALL ON FUNCTION marketplace_generation.guard_gateway_admission(),marketplace_generation.guard_gateway_epoch(),marketplace_generation.guard_gateway_settlement(),marketplace_generation.guard_gateway_pointer(),marketplace_generation.guard_gateway_supersession() FROM PUBLIC;
        """;
}
