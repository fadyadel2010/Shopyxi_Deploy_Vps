-- Candidate unmanaged build registry. No active pointer, search mutation or cutover is
-- enabled by this module. A SQL lease alone is NOT an external search-write fence.
CREATE SCHEMA marketplace_generation;
REVOKE ALL ON SCHEMA marketplace_generation FROM PUBLIC;
CREATE TABLE marketplace_generation.control (
    singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
    journal_id uuid NOT NULL,
    epoch bigint NOT NULL DEFAULT 0 CHECK(epoch >= 0),
    owner_id uuid,
    lease_until timestamptz,
    CHECK((owner_id IS NULL) = (lease_until IS NULL))
);
INSERT INTO marketplace_generation.control(singleton,journal_id)
SELECT true,journal_id FROM marketplace_journal.gate WHERE singleton;
CREATE TABLE marketplace_generation.builds (
    generation_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL UNIQUE,
    journal_id uuid NOT NULL,
    physical_name text NOT NULL UNIQUE CHECK(physical_name ~ '^marketplace_services_g_[0-9a-f]{32}$'),
    physical_uuid text,
    fingerprint text CHECK(fingerprint ~ '^[0-9a-f]{64}$'),
    state text NOT NULL DEFAULT 'new' CHECK(state IN ('new','snapshot-ready','replaying')),
    snapshot_revision bigint CHECK(snapshot_revision >= 0),
    checkpoint_revision bigint CHECK(checkpoint_revision >= 0),
    checkpoint_ordinal bigint CHECK(checkpoint_ordinal >= 0),
    created_epoch bigint NOT NULL CHECK(created_epoch > 0),
    last_epoch bigint NOT NULL CHECK(last_epoch >= created_epoch),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK((physical_uuid IS NULL) = (fingerprint IS NULL)),
    CHECK(physical_uuid IS NULL OR (length(physical_uuid) BETWEEN 1 AND 200 AND physical_uuid <> '_na_')),
    CHECK((state='new' AND snapshot_revision IS NULL AND checkpoint_revision IS NULL AND checkpoint_ordinal IS NULL)
       OR (state<>'new' AND snapshot_revision IS NOT NULL AND checkpoint_revision IS NOT NULL
           AND checkpoint_ordinal IS NOT NULL AND checkpoint_revision >= snapshot_revision))
);
-- Tombstones retain preimages and versions indefinitely in this candidate protocol.
-- External-index delete-version retention must never stand in for durable fencing.
CREATE TABLE marketplace_generation.inputs (
    generation_id uuid NOT NULL REFERENCES marketplace_generation.builds(generation_id),
    source_table text NOT NULL,
    source_id uuid NOT NULL,
    revision bigint NOT NULL CHECK(revision >= 0),
    ordinal bigint NOT NULL CHECK(ordinal >= 0),
    deleted boolean NOT NULL,
    body jsonb NOT NULL,
    PRIMARY KEY(generation_id,source_table,source_id),
    CHECK(source_id=(body->>'Id')::uuid)
);
-- Durable, unacknowledged fan-out work. Capturing this work is not a claim that
-- projection/materialization/fan-out has been completed or externally acknowledged.
CREATE TABLE marketplace_generation.invalidations (
    generation_id uuid NOT NULL REFERENCES marketplace_generation.builds(generation_id),
    revision bigint NOT NULL,
    ordinal bigint NOT NULL,
    source_table text NOT NULL,
    old_row jsonb,
    new_row jsonb,
    PRIMARY KEY(generation_id,revision,ordinal)
);
REVOKE ALL ON ALL TABLES IN SCHEMA marketplace_generation FROM PUBLIC;
