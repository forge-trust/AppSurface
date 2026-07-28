CREATE TABLE appsurface_durable.flow_instance
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    flow_instance_id text NOT NULL CHECK (length(flow_instance_id) BETWEEN 1 AND 200),
    flow_id text NOT NULL CHECK (length(flow_id) BETWEEN 1 AND 200),
    flow_version text NOT NULL CHECK (length(flow_version) BETWEEN 1 AND 100),
    manifest_id text NOT NULL CHECK (length(manifest_id) BETWEEN 1 AND 256),
    authoring_model text NOT NULL CHECK (length(authoring_model) BETWEEN 1 AND 120),
    definition_fingerprint_schema text NOT NULL CHECK (length(definition_fingerprint_schema) BETWEEN 1 AND 200),
    definition_fingerprint_sha256 char(64) NOT NULL CHECK (definition_fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
    current_node_id text NOT NULL CHECK (length(current_node_id) BETWEEN 1 AND 200),
    state text NOT NULL CHECK (state IN
    (
        'ready', 'evaluating', 'waiting_event', 'waiting_timer', 'waiting_activity',
        'suspended', 'cancel_pending', 'completed', 'faulted', 'canceled'
    )),
    context_contract_id text CHECK (context_contract_id IS NULL OR length(context_contract_id) BETWEEN 1 AND 256),
    context_schema_version text CHECK (context_schema_version IS NULL OR length(context_schema_version) BETWEEN 1 AND 100),
    context_codec_id text CHECK (context_codec_id IS NULL OR length(context_codec_id) BETWEEN 1 AND 320),
    context_payload bytea,
    context_sha256 bytea CHECK (context_sha256 IS NULL OR octet_length(context_sha256) = 32),
    context_classification text CHECK (context_classification IS NULL OR length(context_classification) BETWEEN 1 AND 64),
    context_retention text CHECK (context_retention IS NULL OR length(context_retention) BETWEEN 1 AND 128),
    resume_event_name text CHECK (resume_event_name IS NULL OR length(resume_event_name) BETWEEN 1 AND 200),
    resume_event_is_timeout boolean NOT NULL DEFAULT false,
    resume_event_contract_id text,
    resume_event_schema_version text,
    resume_event_codec_id text,
    resume_event_payload bytea,
    resume_event_sha256 bytea CHECK (resume_event_sha256 IS NULL OR octet_length(resume_event_sha256) = 32),
    resume_event_classification text,
    resume_event_retention text,
    activity_callsite_id text CHECK (activity_callsite_id IS NULL OR length(activity_callsite_id) BETWEEN 1 AND 200),
    activity_result_contract_id text,
    activity_result_schema_version text,
    activity_result_codec_id text,
    activity_result_payload bytea,
    activity_result_sha256 bytea CHECK (activity_result_sha256 IS NULL OR octet_length(activity_result_sha256) = 32),
    activity_result_classification text,
    activity_result_retention text,
    lease_generation bigint NOT NULL DEFAULT 0 CHECK (lease_generation >= 0),
    lease_owner text CHECK (lease_owner IS NULL OR length(lease_owner) BETWEEN 1 AND 200),
    lease_started_at timestamp with time zone,
    lease_expires_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    cancellation_requested_at timestamp with time zone,
    terminal_at timestamp with time zone,
    terminal_code text CHECK (terminal_code IS NULL OR length(terminal_code) BETWEEN 1 AND 120),
    suspension_descriptor jsonb,
    suspended_from_state text CHECK (suspended_from_state IS NULL OR suspended_from_state IN
    (
        'ready', 'evaluating', 'waiting_event', 'waiting_timer', 'waiting_activity', 'cancel_pending'
    )),
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    PRIMARY KEY (scope_id, flow_instance_id),
    CHECK
    (
        (context_payload IS NULL AND context_contract_id IS NULL AND context_schema_version IS NULL
            AND context_codec_id IS NULL AND context_classification IS NULL AND context_retention IS NULL
            AND context_sha256 IS NULL)
        OR
        (context_payload IS NOT NULL AND context_contract_id IS NOT NULL AND context_schema_version IS NOT NULL
            AND context_codec_id IS NOT NULL AND context_classification IS NOT NULL AND context_retention IS NOT NULL
            AND context_sha256 IS NOT NULL AND octet_length(context_sha256) = 32)
    ),
    CHECK
    (
        (state = 'evaluating' AND lease_owner IS NOT NULL AND lease_started_at IS NOT NULL AND lease_expires_at IS NOT NULL)
        OR
        (state <> 'evaluating' AND lease_owner IS NULL AND lease_started_at IS NULL AND lease_expires_at IS NULL)
    ),
    CHECK
    (
        (state IN ('completed', 'faulted', 'canceled') AND terminal_at IS NOT NULL AND terminal_code IS NOT NULL)
        OR
        (state NOT IN ('completed', 'faulted', 'canceled') AND terminal_at IS NULL AND terminal_code IS NULL)
    ),
    CHECK
    (
        (state = 'suspended' AND suspension_descriptor IS NOT NULL AND suspended_from_state IS NOT NULL)
        OR
        (state <> 'suspended' AND suspension_descriptor IS NULL AND suspended_from_state IS NULL)
    ),
    CHECK (state <> 'cancel_pending' OR cancellation_requested_at IS NOT NULL),
    CHECK (suspension_descriptor IS NULL OR (jsonb_typeof(suspension_descriptor) = 'object' AND octet_length(suspension_descriptor::text) <= 16384)),
    CHECK
    (
        (resume_event_payload IS NULL AND resume_event_contract_id IS NULL AND resume_event_schema_version IS NULL
            AND resume_event_codec_id IS NULL AND resume_event_sha256 IS NULL
            AND resume_event_classification IS NULL AND resume_event_retention IS NULL)
        OR
        (resume_event_payload IS NOT NULL AND resume_event_contract_id IS NOT NULL AND resume_event_schema_version IS NOT NULL
            AND resume_event_codec_id IS NOT NULL AND resume_event_sha256 IS NOT NULL
            AND resume_event_classification IS NOT NULL AND resume_event_retention IS NOT NULL)
    ),
    CHECK
    (
        (activity_result_payload IS NULL AND activity_callsite_id IS NULL AND activity_result_contract_id IS NULL
            AND activity_result_schema_version IS NULL AND activity_result_codec_id IS NULL
            AND activity_result_sha256 IS NULL AND activity_result_classification IS NULL
            AND activity_result_retention IS NULL)
        OR
        (activity_result_payload IS NOT NULL AND activity_callsite_id IS NOT NULL
            AND activity_result_contract_id IS NOT NULL AND activity_result_schema_version IS NOT NULL
            AND activity_result_codec_id IS NOT NULL AND activity_result_sha256 IS NOT NULL
            AND activity_result_classification IS NOT NULL AND activity_result_retention IS NOT NULL)
    ),
    CHECK (NOT (resume_event_name IS NOT NULL AND activity_callsite_id IS NOT NULL))
);

CREATE TABLE appsurface_durable.flow_command
(
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    command_type text NOT NULL CHECK (command_type IN ('start', 'event', 'cancel', 'release')),
    start_idempotency_key text CHECK (start_idempotency_key IS NULL OR length(start_idempotency_key) BETWEEN 1 AND 200),
    event_id text CHECK (event_id IS NULL OR length(event_id) BETWEEN 1 AND 200),
    actor_id text CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    reason_code text CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    fingerprint_schema text NOT NULL CHECK (length(fingerprint_schema) BETWEEN 1 AND 200),
    fingerprint_sha256 char(64) NOT NULL CHECK (fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
    outcome text NOT NULL CHECK (outcome IN ('accepted', 'race_lost', 'already_terminal')),
    resulting_state text NOT NULL CHECK (resulting_state IN
    (
        'ready', 'evaluating', 'waiting_event', 'waiting_timer', 'waiting_activity',
        'suspended', 'cancel_pending', 'completed', 'faulted', 'canceled'
    )),
    resulting_revision bigint NOT NULL CHECK (resulting_revision > 0),
    accepted_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, command_id),
    FOREIGN KEY (scope_id, flow_instance_id) REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    CHECK
    (
        (command_type = 'start' AND start_idempotency_key IS NOT NULL)
        OR
        (command_type <> 'start' AND start_idempotency_key IS NULL)
    ),
    CHECK
    (
        (command_type = 'event' AND event_id IS NOT NULL)
        OR
        (command_type <> 'event' AND event_id IS NULL)
    ),
    CHECK
    (
        (command_type IN ('cancel', 'release') AND actor_id IS NOT NULL AND reason_code IS NOT NULL)
        OR
        (command_type IN ('start', 'event') AND actor_id IS NULL AND reason_code IS NULL)
    )
);

CREATE INDEX ix_flow_instance_list
    ON appsurface_durable.flow_instance (scope_id, updated_at, flow_instance_id);

CREATE UNIQUE INDEX ix_flow_command_start_idempotency
    ON appsurface_durable.flow_command (scope_id, start_idempotency_key)
    WHERE start_idempotency_key IS NOT NULL;

CREATE UNIQUE INDEX ix_flow_command_event
    ON appsurface_durable.flow_command (scope_id, event_id)
    WHERE event_id IS NOT NULL;

CREATE INDEX ix_flow_command_instance
    ON appsurface_durable.flow_command (scope_id, flow_instance_id, accepted_at);

CREATE TABLE appsurface_durable.flow_history
(
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    aggregate_revision bigint NOT NULL CHECK (aggregate_revision > 0),
    command_id text CHECK (command_id IS NULL OR length(command_id) BETWEEN 1 AND 200),
    node_id text CHECK (node_id IS NULL OR length(node_id) BETWEEN 1 AND 200),
    transition_kind text NOT NULL CHECK (length(transition_kind) BETWEEN 1 AND 96),
    input_contract_id text CHECK (input_contract_id IS NULL OR length(input_contract_id) BETWEEN 1 AND 256),
    input_schema_version text CHECK (input_schema_version IS NULL OR length(input_schema_version) BETWEEN 1 AND 100),
    input_codec_id text CHECK (input_codec_id IS NULL OR length(input_codec_id) BETWEEN 1 AND 320),
    input_payload bytea,
    input_sha256 bytea CHECK (input_sha256 IS NULL OR octet_length(input_sha256) = 32),
    input_classification text CHECK (input_classification IS NULL OR length(input_classification) BETWEEN 1 AND 64),
    input_retention text CHECK (input_retention IS NULL OR length(input_retention) BETWEEN 1 AND 128),
    output_contract_id text CHECK (output_contract_id IS NULL OR length(output_contract_id) BETWEEN 1 AND 256),
    output_schema_version text CHECK (output_schema_version IS NULL OR length(output_schema_version) BETWEEN 1 AND 100),
    output_codec_id text CHECK (output_codec_id IS NULL OR length(output_codec_id) BETWEEN 1 AND 320),
    output_payload bytea,
    output_sha256 bytea CHECK (output_sha256 IS NULL OR octet_length(output_sha256) = 32),
    output_classification text CHECK (output_classification IS NULL OR length(output_classification) BETWEEN 1 AND 64),
    output_retention text CHECK (output_retention IS NULL OR length(output_retention) BETWEEN 1 AND 128),
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (scope_id, flow_instance_id) REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    CHECK (jsonb_typeof(details) = 'object'),
    CHECK (octet_length(details::text) <= 16384),
    CHECK
    (
        (input_payload IS NULL AND input_contract_id IS NULL AND input_schema_version IS NULL
            AND input_codec_id IS NULL AND input_classification IS NULL AND input_retention IS NULL AND input_sha256 IS NULL)
        OR
        (input_payload IS NOT NULL AND input_contract_id IS NOT NULL AND input_schema_version IS NOT NULL
            AND input_codec_id IS NOT NULL AND input_classification IS NOT NULL AND input_retention IS NOT NULL
            AND input_sha256 IS NOT NULL AND octet_length(input_sha256) = 32)
    ),
    CHECK
    (
        (output_payload IS NULL AND output_contract_id IS NULL AND output_schema_version IS NULL
            AND output_codec_id IS NULL AND output_classification IS NULL AND output_retention IS NULL AND output_sha256 IS NULL)
        OR
        (output_payload IS NOT NULL AND output_contract_id IS NOT NULL AND output_schema_version IS NOT NULL
            AND output_codec_id IS NOT NULL AND output_classification IS NOT NULL AND output_retention IS NOT NULL
            AND output_sha256 IS NOT NULL AND octet_length(output_sha256) = 32)
    )
);

CREATE INDEX ix_flow_history_flow
    ON appsurface_durable.flow_history (scope_id, flow_instance_id, aggregate_revision, event_id);

CREATE TABLE appsurface_durable.flow_wait
(
    wait_id uuid PRIMARY KEY,
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('event', 'activity')),
    state text NOT NULL CHECK (state IN
    (
        'active', 'suspended', 'event_won', 'timer_won', 'activity_completed', 'superseded', 'canceled'
    )),
    registered_revision bigint NOT NULL CHECK (registered_revision > 0),
    resolved_revision bigint CHECK (resolved_revision IS NULL OR resolved_revision >= registered_revision),
    event_name text CHECK (event_name IS NULL OR length(event_name) BETWEEN 1 AND 200),
    event_payload_required boolean NOT NULL DEFAULT false,
    event_contract_id text CHECK (event_contract_id IS NULL OR length(event_contract_id) BETWEEN 1 AND 256),
    event_schema_version text CHECK (event_schema_version IS NULL OR length(event_schema_version) BETWEEN 1 AND 100),
    event_classification text CHECK (event_classification IS NULL OR length(event_classification) BETWEEN 1 AND 64),
    event_retention text CHECK (event_retention IS NULL OR length(event_retention) BETWEEN 1 AND 128),
    callsite_id text CHECK (callsite_id IS NULL OR length(callsite_id) BETWEEN 1 AND 200),
    child_work_id text CHECK (child_work_id IS NULL OR length(child_work_id) BETWEEN 1 AND 200),
    result_contract_version text CHECK (result_contract_version IS NULL OR length(result_contract_version) BETWEEN 1 AND 100),
    suspension_descriptor jsonb,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    resolved_at timestamp with time zone,
    UNIQUE (scope_id, flow_instance_id, wait_id),
    FOREIGN KEY (scope_id, flow_instance_id) REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    FOREIGN KEY (scope_id, child_work_id) REFERENCES appsurface_durable.work(scope_id, work_id),
    CHECK
    (
        (kind = 'event' AND event_name IS NOT NULL AND callsite_id IS NULL AND child_work_id IS NULL AND result_contract_version IS NULL)
        OR
        (kind = 'activity' AND event_name IS NULL AND NOT event_payload_required AND event_contract_id IS NULL
            AND event_schema_version IS NULL AND event_classification IS NULL AND event_retention IS NULL
            AND callsite_id IS NOT NULL AND child_work_id IS NOT NULL AND result_contract_version IS NOT NULL)
    ),
    CHECK
    (
        (kind = 'event' AND state IN ('active', 'suspended', 'event_won', 'timer_won', 'superseded', 'canceled'))
        OR
        (kind = 'activity' AND state IN ('active', 'suspended', 'activity_completed', 'superseded', 'canceled'))
    ),
    CHECK
    (
        (event_payload_required AND event_contract_id IS NOT NULL AND event_schema_version IS NOT NULL
            AND event_classification IS NOT NULL AND event_retention IS NOT NULL)
        OR
        (NOT event_payload_required AND event_contract_id IS NULL AND event_schema_version IS NULL
            AND event_classification IS NULL AND event_retention IS NULL)
    ),
    CHECK
    (
        (state IN ('active', 'suspended') AND resolved_revision IS NULL AND resolved_at IS NULL)
        OR
        (state NOT IN ('active', 'suspended') AND resolved_revision IS NOT NULL AND resolved_at IS NOT NULL)
    ),
    CHECK (suspension_descriptor IS NULL OR (jsonb_typeof(suspension_descriptor) = 'object' AND octet_length(suspension_descriptor::text) <= 16384))
);

CREATE UNIQUE INDEX ix_flow_wait_active_suspended
    ON appsurface_durable.flow_wait (scope_id, flow_instance_id)
    WHERE state IN ('active', 'suspended');

CREATE INDEX ix_flow_wait_event_lookup
    ON appsurface_durable.flow_wait (scope_id, flow_instance_id, event_name, created_at DESC)
    WHERE kind = 'event';

CREATE UNIQUE INDEX ix_flow_wait_child_work
    ON appsurface_durable.flow_wait (scope_id, child_work_id)
    WHERE child_work_id IS NOT NULL;

CREATE TABLE appsurface_durable.flow_timer
(
    timer_id uuid PRIMARY KEY,
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    wait_id uuid NOT NULL,
    registered_revision bigint NOT NULL CHECK (registered_revision > 0),
    due_at timestamp with time zone NOT NULL,
    state text NOT NULL CHECK (state IN ('scheduled', 'fired', 'superseded', 'canceled')),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    resolved_at timestamp with time zone,
    UNIQUE (scope_id, flow_instance_id, timer_id),
    FOREIGN KEY (scope_id, flow_instance_id) REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    FOREIGN KEY (scope_id, flow_instance_id, wait_id) REFERENCES appsurface_durable.flow_wait(scope_id, flow_instance_id, wait_id),
    CHECK
    (
        (state = 'scheduled' AND resolved_at IS NULL)
        OR
        (state <> 'scheduled' AND resolved_at IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ix_flow_timer_scheduled_wait
    ON appsurface_durable.flow_timer (scope_id, flow_instance_id, wait_id)
    WHERE state = 'scheduled';

CREATE TABLE appsurface_durable.flow_dispatch
(
    dispatch_id uuid PRIMARY KEY,
    scope_id text NOT NULL CHECK (length(scope_id) BETWEEN 1 AND 200),
    kind text NOT NULL CHECK (kind IN ('flow', 'timer')),
    flow_instance_id text NOT NULL CHECK (length(flow_instance_id) BETWEEN 1 AND 200),
    timer_id uuid,
    due_at timestamp with time zone NOT NULL,
    state text NOT NULL CHECK (state IN ('available', 'leased', 'suspended', 'terminal')),
    expected_revision bigint NOT NULL CHECK (expected_revision >= 0),
    priority smallint NOT NULL DEFAULT 0,
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (scope_id, flow_instance_id) REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    FOREIGN KEY (scope_id, flow_instance_id, timer_id) REFERENCES appsurface_durable.flow_timer(scope_id, flow_instance_id, timer_id),
    CHECK
    (
        (kind = 'flow' AND timer_id IS NULL)
        OR
        (kind = 'timer' AND timer_id IS NOT NULL)
    )
);

CREATE INDEX ix_flow_dispatch_instance
    ON appsurface_durable.flow_dispatch (scope_id, flow_instance_id);

CREATE UNIQUE INDEX ix_flow_dispatch_flow
    ON appsurface_durable.flow_dispatch (scope_id, flow_instance_id)
    WHERE kind = 'flow';

CREATE UNIQUE INDEX ix_flow_dispatch_timer
    ON appsurface_durable.flow_dispatch (scope_id, timer_id)
    WHERE kind = 'timer';

CREATE INDEX ix_flow_dispatch_due
    ON appsurface_durable.flow_dispatch (due_at, priority DESC, dispatch_id)
    INCLUDE (scope_id, kind, flow_instance_id, timer_id, expected_revision)
    WHERE state IN ('available', 'leased');

ALTER TABLE appsurface_durable.flow_instance ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_instance FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_instance_scope_isolation ON appsurface_durable.flow_instance
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_command FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_command_scope_isolation ON appsurface_durable.flow_command
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_history FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_history_scope_isolation ON appsurface_durable.flow_history
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_wait ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_wait FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_wait_scope_isolation ON appsurface_durable.flow_wait
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_timer ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_timer FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_timer_scope_isolation ON appsurface_durable.flow_timer
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_dispatch ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_dispatch FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_dispatch_global_discovery ON appsurface_durable.flow_dispatch
    FOR SELECT
    USING (true);
CREATE POLICY flow_dispatch_scope_insert ON appsurface_durable.flow_dispatch
    FOR INSERT
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
CREATE POLICY flow_dispatch_scope_update ON appsurface_durable.flow_dispatch
    FOR UPDATE
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
