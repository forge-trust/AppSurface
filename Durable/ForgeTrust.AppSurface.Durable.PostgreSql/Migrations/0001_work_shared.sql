CREATE SCHEMA appsurface_durable;

CREATE TABLE appsurface_durable.schema_migration
(
    version integer PRIMARY KEY CHECK (version > 0),
    name text NOT NULL,
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    applied_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE appsurface_durable.store_metadata
(
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    store_id uuid NOT NULL DEFAULT gen_random_uuid() CHECK (store_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    active_runtime_epoch uuid CHECK (active_runtime_epoch IS NULL OR active_runtime_epoch <> '00000000-0000-0000-0000-000000000000'::uuid),
    schema_version integer NOT NULL CHECK (schema_version >= 0),
    minimum_reader_version integer NOT NULL CHECK (minimum_reader_version > 0),
    maximum_reader_version integer NOT NULL CHECK (maximum_reader_version >= minimum_reader_version),
    minimum_writer_version integer NOT NULL CHECK (minimum_writer_version > 0),
    maximum_writer_version integer NOT NULL CHECK (maximum_writer_version >= minimum_writer_version),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
);

INSERT INTO appsurface_durable.store_metadata
    (schema_version, minimum_reader_version, maximum_reader_version, minimum_writer_version, maximum_writer_version)
VALUES (0, 1, 1, 1, 1);

CREATE TABLE appsurface_durable.runtime_epoch_history
(
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    previous_epoch uuid,
    active_epoch uuid NOT NULL,
    actor_id text NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 200),
    reason_code text NOT NULL CHECK (length(reason_code) BETWEEN 1 AND 120),
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE appsurface_durable.scope
(
    scope_id text PRIMARY KEY CHECK (length(scope_id) BETWEEN 1 AND 200),
    generation bigint NOT NULL DEFAULT 1 CHECK (generation > 0),
    state text NOT NULL DEFAULT 'active' CHECK (state IN ('active', 'disabled')),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE appsurface_durable.scope_history
(
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    generation bigint NOT NULL CHECK (generation > 0),
    event_type text NOT NULL CHECK (event_type IN ('disabled', 'released')),
    actor_id text NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 200),
    reason_code text NOT NULL CHECK (length(reason_code) BETWEEN 1 AND 120),
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX ix_scope_history_scope ON appsurface_durable.scope_history (scope_id, event_id);

CREATE TABLE appsurface_durable.work
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    work_id text NOT NULL CHECK (length(work_id) BETWEEN 1 AND 200),
    activity_id text NOT NULL CHECK (length(activity_id) BETWEEN 1 AND 200),
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    work_name text NOT NULL CHECK (length(work_name) BETWEEN 1 AND 200),
    work_version text NOT NULL CHECK (length(work_version) BETWEEN 1 AND 100),
    contract_id text NOT NULL CHECK (length(contract_id) BETWEEN 1 AND 256),
    payload_schema_version text NOT NULL CHECK (length(payload_schema_version) BETWEEN 1 AND 100),
    codec_id text NOT NULL CHECK (length(codec_id) BETWEEN 1 AND 320),
    payload bytea NOT NULL,
    payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
    payload_classification text NOT NULL CHECK (length(payload_classification) BETWEEN 1 AND 64),
    payload_retention text NOT NULL CHECK (length(payload_retention) BETWEEN 1 AND 128),
    request_fingerprint_schema text NOT NULL CHECK (length(request_fingerprint_schema) BETWEEN 1 AND 200),
    request_fingerprint_sha256 char(64) NOT NULL CHECK (request_fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
    state text NOT NULL CHECK (state IN
    (
        'pending', 'leased', 'reconciling', 'effect_permitted', 'retry_wait', 'cancel_pending',
        'succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect',
        'suspended_ambiguous_external_outcome', 'suspended_reconciliation_required',
        'suspended_manual_resolution', 'suspended_contract_unavailable'
    )),
    provider_safety text NOT NULL CHECK (provider_safety IN
    (
        'idempotent', 'provider_keyed', 'reconcile_before_retry', 'manual_resolution'
    )),
    accepted_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    due_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    terminal_at timestamp with time zone,
    cancellation_requested_at timestamp with time zone,
    attempt_number integer NOT NULL DEFAULT 0 CHECK (attempt_number >= 0),
    lease_generation bigint NOT NULL DEFAULT 0 CHECK (lease_generation >= 0),
    lease_owner text,
    lease_started_at timestamp with time zone,
    lease_expires_at timestamp with time zone,
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    maximum_attempts integer NOT NULL CHECK (maximum_attempts > 0),
    maximum_elapsed interval NOT NULL CHECK (maximum_elapsed > interval '0 seconds'),
    backoff_algorithm text NOT NULL CHECK (length(backoff_algorithm) BETWEEN 1 AND 100),
    initial_retry_delay interval NOT NULL CHECK (initial_retry_delay > interval '0 seconds'),
    maximum_retry_delay interval NOT NULL CHECK (maximum_retry_delay >= initial_retry_delay),
    lease_duration interval NOT NULL CHECK (lease_duration > interval '0 seconds'),
    lease_renewal_cadence interval NOT NULL CHECK (lease_renewal_cadence > interval '0 seconds'),
    maximum_lease_lifetime interval NOT NULL CHECK (maximum_lease_lifetime >= lease_duration),
    result_contract_id text,
    result_schema_version text,
    result_codec_id text,
    result_classification text,
    result_retention_policy_id text,
    result_payload bytea,
    result_sha256 bytea,
    terminal_code text,
    PRIMARY KEY (scope_id, work_id),
    UNIQUE (scope_id, activity_id),
    UNIQUE (scope_id, command_id),
    UNIQUE (scope_id, idempotency_key),
    CHECK
    (
        (result_payload IS NULL AND result_contract_id IS NULL AND result_schema_version IS NULL
            AND result_codec_id IS NULL AND result_classification IS NULL
            AND result_retention_policy_id IS NULL AND result_sha256 IS NULL)
        OR
        (result_payload IS NOT NULL AND result_contract_id IS NOT NULL AND result_schema_version IS NOT NULL
            AND result_codec_id IS NOT NULL AND result_classification IS NOT NULL
            AND result_retention_policy_id IS NOT NULL AND length(result_retention_policy_id) BETWEEN 1 AND 128
            AND result_sha256 IS NOT NULL AND octet_length(result_sha256) = 32)
    )
);

CREATE TABLE appsurface_durable.dispatch
(
    dispatch_id uuid PRIMARY KEY,
    scope_id text NOT NULL CHECK (length(scope_id) BETWEEN 1 AND 200),
    aggregate_kind text NOT NULL CHECK (aggregate_kind = 'work'),
    aggregate_id text NOT NULL CHECK (length(aggregate_id) BETWEEN 1 AND 200),
    due_at timestamp with time zone NOT NULL,
    state text NOT NULL CHECK (state IN ('available', 'leased', 'suspended', 'terminal')),
    expected_revision bigint NOT NULL CHECK (expected_revision >= 0),
    priority smallint NOT NULL DEFAULT 0,
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (scope_id, aggregate_kind, aggregate_id),
    FOREIGN KEY (scope_id, aggregate_id) REFERENCES appsurface_durable.work(scope_id, work_id)
);

-- Leased dispatch remains discoverable after due_at so every expired effect-permitted Work reaches scoped recovery.
CREATE INDEX ix_dispatch_due
    ON appsurface_durable.dispatch (due_at, priority DESC, dispatch_id)
    INCLUDE (scope_id, aggregate_kind, aggregate_id, expected_revision)
    WHERE state IN ('available', 'leased');

CREATE TABLE appsurface_durable.work_history
(
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope_id text NOT NULL,
    work_id text NOT NULL,
    aggregate_revision bigint NOT NULL CHECK (aggregate_revision > 0),
    event_type text NOT NULL CHECK (length(event_type) BETWEEN 1 AND 96),
    event_schema_version text NOT NULL DEFAULT 'work-event-v1' CHECK (length(event_schema_version) BETWEEN 1 AND 64),
    command_id text CHECK (command_id IS NULL OR length(command_id) BETWEEN 1 AND 200),
    actor_id text CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    reason_code text CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    attempt_number integer NOT NULL CHECK (attempt_number >= 0),
    lease_generation bigint NOT NULL CHECK (lease_generation >= 0),
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    is_stale_observation boolean NOT NULL DEFAULT false,
    observation_contract_id text,
    observation_schema_version text,
    observation_codec_id text,
    observation_classification text,
    observation_payload bytea,
    observation_sha256 bytea,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    FOREIGN KEY (scope_id, work_id) REFERENCES appsurface_durable.work(scope_id, work_id),
    CHECK (jsonb_typeof(details) = 'object'),
    CHECK (octet_length(details::text) <= 16384),
    CHECK ((actor_id IS NULL) = (reason_code IS NULL)),
    CHECK
    (
        (observation_payload IS NULL AND observation_contract_id IS NULL AND observation_schema_version IS NULL
            AND observation_codec_id IS NULL AND observation_classification IS NULL AND observation_sha256 IS NULL)
        OR
        (is_stale_observation AND observation_payload IS NOT NULL AND observation_contract_id IS NOT NULL
            AND observation_schema_version IS NOT NULL AND observation_codec_id IS NOT NULL
            AND observation_classification IS NOT NULL AND observation_sha256 IS NOT NULL
            AND octet_length(observation_sha256) = 32)
    )
);

CREATE INDEX ix_work_history_work
    ON appsurface_durable.work_history (scope_id, work_id, aggregate_revision, event_id);

CREATE TABLE appsurface_durable.work_operator_command
(
    scope_id text NOT NULL,
    work_id text NOT NULL,
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    command_type text NOT NULL CHECK (command_type IN ('reconcile', 'manual_resolve', 'retry_safe', 'recovery_release')),
    actor_id text NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 200),
    reason_code text NOT NULL CHECK (length(reason_code) BETWEEN 1 AND 120),
    request_schema text NOT NULL CHECK (length(request_schema) BETWEEN 1 AND 200),
    request_sha256 bytea NOT NULL CHECK (octet_length(request_sha256) = 32),
    status text NOT NULL CHECK (status IN ('started', 'completed')),
    resulting_state text,
    resulting_revision bigint,
    started_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamp with time zone,
    PRIMARY KEY (scope_id, command_id),
    FOREIGN KEY (scope_id, work_id) REFERENCES appsurface_durable.work(scope_id, work_id),
    CHECK
    (
        (status = 'started' AND resulting_state IS NULL AND resulting_revision IS NULL AND completed_at IS NULL)
        OR
        (status = 'completed' AND resulting_state IS NOT NULL AND resulting_revision > 0 AND completed_at IS NOT NULL)
    )
);

CREATE INDEX ix_work_operator_command_work
    ON appsurface_durable.work_operator_command (scope_id, work_id, started_at);

CREATE TABLE appsurface_durable.effect_permit
(
    permit_id uuid PRIMARY KEY,
    scope_id text NOT NULL,
    work_id text NOT NULL,
    activity_id text NOT NULL CHECK (length(activity_id) BETWEEN 1 AND 200),
    attempt_number integer NOT NULL CHECK (attempt_number > 0),
    lease_generation bigint NOT NULL CHECK (lease_generation > 0),
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    status text NOT NULL CHECK (status IN ('granted', 'known_succeeded', 'proven_no_effect', 'ambiguous')),
    permitted_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    observed_at timestamp with time zone,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    UNIQUE (scope_id, work_id, attempt_number, lease_generation),
    FOREIGN KEY (scope_id, work_id) REFERENCES appsurface_durable.work(scope_id, work_id),
    CHECK (jsonb_typeof(details) = 'object'),
    CHECK (octet_length(details::text) <= 16384)
);

CREATE INDEX ix_effect_permit_activity
    ON appsurface_durable.effect_permit (scope_id, work_id, activity_id, permit_id);

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
