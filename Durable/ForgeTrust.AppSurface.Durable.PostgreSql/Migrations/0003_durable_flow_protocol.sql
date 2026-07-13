CREATE TABLE appsurface_durable.flow_instance
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    flow_instance_id text NOT NULL CHECK (length(flow_instance_id) BETWEEN 1 AND 200),
    start_idempotency_key text NOT NULL CHECK (length(start_idempotency_key) BETWEEN 1 AND 200),
    flow_id text NOT NULL CHECK (length(flow_id) BETWEEN 1 AND 200),
    flow_version text NOT NULL CHECK (length(flow_version) BETWEEN 1 AND 100),
    authoring_model text NOT NULL CHECK (length(authoring_model) BETWEEN 1 AND 120),
    command_schema_version text NOT NULL CHECK (length(command_schema_version) BETWEEN 1 AND 64),
    definition_fingerprint bytea NOT NULL CHECK (octet_length(definition_fingerprint) = 32),
    current_node_id text NOT NULL CHECK (length(current_node_id) BETWEEN 1 AND 200),
    state text NOT NULL CHECK (state IN
    (
        'ready',
        'evaluating',
        'waiting_event',
        'waiting_timer',
        'waiting_activity',
        'cancel_pending',
        'completed',
        'faulted',
        'canceled',
        'suspended'
    )),
    suspended_from_state text CHECK (suspended_from_state IN
    (
        'ready',
        'evaluating',
        'waiting_event',
        'waiting_timer',
        'waiting_activity',
        'cancel_pending',
        'canceled'
    )),
    suspended_from_terminal_code text CHECK
        (suspended_from_terminal_code IS NULL OR length(suspended_from_terminal_code) BETWEEN 1 AND 120),
    context_contract_id text NOT NULL CHECK (length(context_contract_id) BETWEEN 1 AND 256),
    context_schema_version text NOT NULL CHECK (length(context_schema_version) BETWEEN 1 AND 100),
    context_codec_id text NOT NULL CHECK (length(context_codec_id) BETWEEN 1 AND 320),
    context_payload bytea NOT NULL,
    context_sha256 bytea NOT NULL CHECK (octet_length(context_sha256) = 32),
    context_classification text NOT NULL CHECK (length(context_classification) BETWEEN 1 AND 64),
    context_retention_policy_id text NOT NULL CHECK (length(context_retention_policy_id) BETWEEN 1 AND 128),
    resume_event_name text CHECK (resume_event_name IS NULL OR length(resume_event_name) BETWEEN 1 AND 200),
    resume_event_is_timeout boolean NOT NULL DEFAULT false,
    resume_event_contract_id text,
    resume_event_schema_version text,
    resume_event_codec_id text,
    resume_event_payload bytea,
    resume_event_sha256 bytea,
    resume_event_classification text,
    resume_event_retention_policy_id text,
    activity_callsite_id text CHECK (activity_callsite_id IS NULL OR length(activity_callsite_id) BETWEEN 1 AND 200),
    activity_result_contract_id text,
    activity_result_schema_version text,
    activity_result_codec_id text,
    activity_result_payload bytea,
    activity_result_sha256 bytea,
    activity_result_classification text,
    activity_result_retention_policy_id text,
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    lease_generation bigint NOT NULL DEFAULT 0 CHECK (lease_generation >= 0),
    lease_owner text,
    lease_started_at timestamp with time zone,
    lease_expires_at timestamp with time zone,
    cancellation_requested_at timestamp with time zone,
    terminal_at timestamp with time zone,
    terminal_code text,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, flow_instance_id),
    UNIQUE (scope_id, start_idempotency_key),
    CHECK
    (
        (resume_event_payload IS NULL
            AND resume_event_contract_id IS NULL
            AND resume_event_schema_version IS NULL
            AND resume_event_codec_id IS NULL
            AND resume_event_sha256 IS NULL
            AND resume_event_classification IS NULL
            AND resume_event_retention_policy_id IS NULL)
        OR
        (resume_event_payload IS NOT NULL
            AND resume_event_contract_id IS NOT NULL
            AND resume_event_schema_version IS NOT NULL
            AND resume_event_codec_id IS NOT NULL
            AND resume_event_sha256 IS NOT NULL
            AND octet_length(resume_event_sha256) = 32
            AND resume_event_classification IS NOT NULL
            AND resume_event_retention_policy_id IS NOT NULL)
    ),
    CHECK
    (
        (activity_result_payload IS NULL
            AND activity_callsite_id IS NULL
            AND activity_result_contract_id IS NULL
            AND activity_result_schema_version IS NULL
            AND activity_result_codec_id IS NULL
            AND activity_result_sha256 IS NULL
            AND activity_result_classification IS NULL
            AND activity_result_retention_policy_id IS NULL)
        OR
        (activity_result_payload IS NOT NULL
            AND activity_callsite_id IS NOT NULL
            AND activity_result_contract_id IS NOT NULL
            AND activity_result_schema_version IS NOT NULL
            AND activity_result_codec_id IS NOT NULL
            AND activity_result_sha256 IS NOT NULL
            AND octet_length(activity_result_sha256) = 32
            AND activity_result_classification IS NOT NULL
            AND activity_result_retention_policy_id IS NOT NULL)
    ),
    CHECK (NOT (resume_event_name IS NOT NULL AND activity_callsite_id IS NOT NULL)),
    CHECK
    (
        (state = 'suspended' AND suspended_from_state IS NOT NULL)
        OR
        suspended_from_state IS NULL
    ),
    CHECK (suspended_from_state IS NOT NULL OR suspended_from_terminal_code IS NULL)
);

CREATE INDEX ix_flow_instance_state
    ON appsurface_durable.flow_instance (scope_id, state, updated_at);

CREATE TABLE appsurface_durable.flow_command
(
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    command_type text NOT NULL CHECK (command_type IN ('start', 'external_event', 'cancel', 'release')),
    command_schema_version text NOT NULL CHECK (length(command_schema_version) BETWEEN 1 AND 64),
    event_id text CHECK (event_id IS NULL OR length(event_id) BETWEEN 1 AND 200),
    actor_id text CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    reason_code text CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    request_sha256 bytea NOT NULL CHECK (octet_length(request_sha256) = 32),
    outcome text NOT NULL CHECK (outcome IN ('accepted', 'race_lost', 'already_terminal')),
    resulting_state text NOT NULL CHECK (length(resulting_state) BETWEEN 1 AND 64),
    resulting_revision bigint NOT NULL CHECK (resulting_revision > 0),
    accepted_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, command_id),
    FOREIGN KEY (scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id)
);

CREATE UNIQUE INDEX ux_flow_command_event_id
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
    event_type text NOT NULL CHECK (length(event_type) BETWEEN 1 AND 96),
    authoring_model text NOT NULL CHECK (length(authoring_model) BETWEEN 1 AND 120),
    command_schema_version text NOT NULL CHECK (length(command_schema_version) BETWEEN 1 AND 64),
    definition_fingerprint bytea NOT NULL CHECK (octet_length(definition_fingerprint) = 32),
    command_id text,
    command_event_id text,
    actor_id text,
    reason_code text,
    command_outcome text,
    node_id text CHECK (node_id IS NULL OR length(node_id) BETWEEN 1 AND 200),
    transition_kind text CHECK (transition_kind IS NULL OR length(transition_kind) BETWEEN 1 AND 64),
    input_context_contract_id text,
    input_context_schema_version text,
    input_context_codec_id text,
    input_context_payload bytea,
    input_context_sha256 bytea,
    input_context_classification text,
    input_context_retention_policy_id text,
    transition_input_kind text CHECK (transition_input_kind IS NULL OR length(transition_input_kind) BETWEEN 1 AND 64),
    transition_input_name text CHECK (transition_input_name IS NULL OR length(transition_input_name) BETWEEN 1 AND 200),
    transition_input_contract_id text,
    transition_input_schema_version text,
    transition_input_codec_id text,
    transition_input_payload bytea,
    transition_input_sha256 bytea,
    transition_input_classification text,
    transition_input_retention_policy_id text,
    output_context_contract_id text,
    output_context_schema_version text,
    output_context_codec_id text,
    output_context_payload bytea,
    output_context_sha256 bytea,
    output_context_classification text,
    output_context_retention_policy_id text,
    transition_output_contract_id text,
    transition_output_schema_version text,
    transition_output_codec_id text,
    transition_output_payload bytea,
    transition_output_sha256 bytea,
    transition_output_classification text,
    transition_output_retention_policy_id text,
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    FOREIGN KEY (scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    CHECK (jsonb_typeof(details) = 'object'),
    CHECK (octet_length(details::text) <= 16384),
    CHECK (command_id IS NULL OR length(command_id) BETWEEN 1 AND 200),
    CHECK (command_event_id IS NULL OR length(command_event_id) BETWEEN 1 AND 200),
    CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    CHECK (command_outcome IS NULL OR length(command_outcome) BETWEEN 1 AND 64),
    CHECK
    (
        (input_context_payload IS NULL AND input_context_contract_id IS NULL
            AND input_context_schema_version IS NULL AND input_context_codec_id IS NULL
            AND input_context_sha256 IS NULL AND input_context_classification IS NULL
            AND input_context_retention_policy_id IS NULL)
        OR
        (input_context_payload IS NOT NULL AND input_context_contract_id IS NOT NULL
            AND input_context_schema_version IS NOT NULL AND input_context_codec_id IS NOT NULL
            AND input_context_sha256 IS NOT NULL AND octet_length(input_context_sha256) = 32
            AND input_context_classification IS NOT NULL AND input_context_retention_policy_id IS NOT NULL)
    ),
    CHECK
    (
        (transition_input_payload IS NULL AND transition_input_contract_id IS NULL
            AND transition_input_schema_version IS NULL AND transition_input_codec_id IS NULL
            AND transition_input_sha256 IS NULL AND transition_input_classification IS NULL
            AND transition_input_retention_policy_id IS NULL)
        OR
        (transition_input_payload IS NOT NULL AND transition_input_contract_id IS NOT NULL
            AND transition_input_schema_version IS NOT NULL AND transition_input_codec_id IS NOT NULL
            AND transition_input_sha256 IS NOT NULL AND octet_length(transition_input_sha256) = 32
            AND transition_input_classification IS NOT NULL AND transition_input_retention_policy_id IS NOT NULL)
    ),
    CHECK
    (
        (output_context_payload IS NULL AND output_context_contract_id IS NULL
            AND output_context_schema_version IS NULL AND output_context_codec_id IS NULL
            AND output_context_sha256 IS NULL AND output_context_classification IS NULL
            AND output_context_retention_policy_id IS NULL)
        OR
        (output_context_payload IS NOT NULL AND output_context_contract_id IS NOT NULL
            AND output_context_schema_version IS NOT NULL AND output_context_codec_id IS NOT NULL
            AND output_context_sha256 IS NOT NULL AND octet_length(output_context_sha256) = 32
            AND output_context_classification IS NOT NULL AND output_context_retention_policy_id IS NOT NULL)
    ),
    CHECK
    (
        (transition_output_payload IS NULL AND transition_output_contract_id IS NULL
            AND transition_output_schema_version IS NULL AND transition_output_codec_id IS NULL
            AND transition_output_sha256 IS NULL AND transition_output_classification IS NULL
            AND transition_output_retention_policy_id IS NULL)
        OR
        (transition_output_payload IS NOT NULL AND transition_output_contract_id IS NOT NULL
            AND transition_output_schema_version IS NOT NULL AND transition_output_codec_id IS NOT NULL
            AND transition_output_sha256 IS NOT NULL AND octet_length(transition_output_sha256) = 32
            AND transition_output_classification IS NOT NULL AND transition_output_retention_policy_id IS NOT NULL)
    )
);

CREATE INDEX ix_flow_history_revision
    ON appsurface_durable.flow_history (scope_id, flow_instance_id, aggregate_revision);

CREATE TABLE appsurface_durable.flow_wait
(
    wait_id uuid PRIMARY KEY,
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    node_id text NOT NULL CHECK (length(node_id) BETWEEN 1 AND 200),
    wait_kind text NOT NULL CHECK (wait_kind IN ('external_event', 'activity')),
    state text NOT NULL CHECK (state IN
    (
        'active',
        'event_won',
        'timer_won',
        'activity_completed',
        'superseded',
        'canceled'
    )),
    event_name text CHECK (event_name IS NULL OR length(event_name) BETWEEN 1 AND 200),
    event_payload_required boolean NOT NULL DEFAULT false,
    event_contract_id text CHECK (event_contract_id IS NULL OR length(event_contract_id) BETWEEN 1 AND 200),
    event_schema_version text CHECK (event_schema_version IS NULL OR length(event_schema_version) BETWEEN 1 AND 100),
    event_classification text CHECK (event_classification IS NULL OR length(event_classification) BETWEEN 1 AND 64),
    event_retention_policy_id text CHECK (event_retention_policy_id IS NULL OR length(event_retention_policy_id) BETWEEN 1 AND 128),
    activity_callsite_id text CHECK (activity_callsite_id IS NULL OR length(activity_callsite_id) BETWEEN 1 AND 200),
    activity_work_id text CHECK (activity_work_id IS NULL OR length(activity_work_id) BETWEEN 1 AND 200),
    result_contract_version integer CHECK (result_contract_version IS NULL OR result_contract_version > 0),
    timeout_at timestamp with time zone,
    registered_revision bigint NOT NULL CHECK (registered_revision > 0),
    resolved_revision bigint CHECK (resolved_revision IS NULL OR resolved_revision > registered_revision),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    resolved_at timestamp with time zone,
    UNIQUE (wait_id, scope_id, flow_instance_id),
    FOREIGN KEY (scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    FOREIGN KEY (scope_id, activity_work_id)
        REFERENCES appsurface_durable.work(scope_id, work_id),
    CHECK
    (
        (wait_kind = 'external_event' AND event_name IS NOT NULL
            AND activity_callsite_id IS NULL AND activity_work_id IS NULL AND result_contract_version IS NULL
            AND
            (
                (event_payload_required AND event_contract_id IS NOT NULL AND event_schema_version IS NOT NULL
                    AND event_classification IS NOT NULL AND event_retention_policy_id IS NOT NULL)
                OR
                (NOT event_payload_required AND event_contract_id IS NULL AND event_schema_version IS NULL
                    AND event_classification IS NULL AND event_retention_policy_id IS NULL)
            ))
        OR
        (wait_kind = 'activity' AND event_name IS NULL
            AND NOT event_payload_required AND event_contract_id IS NULL AND event_schema_version IS NULL
            AND event_classification IS NULL AND event_retention_policy_id IS NULL
            AND activity_callsite_id IS NOT NULL AND activity_work_id IS NOT NULL AND result_contract_version IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_flow_wait_active
    ON appsurface_durable.flow_wait (scope_id, flow_instance_id)
    WHERE state = 'active';

CREATE UNIQUE INDEX ux_flow_wait_activity_work
    ON appsurface_durable.flow_wait (scope_id, activity_work_id)
    WHERE activity_work_id IS NOT NULL;

CREATE TABLE appsurface_durable.flow_timer
(
    timer_id uuid PRIMARY KEY,
    wait_id uuid NOT NULL,
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL,
    due_at timestamp with time zone NOT NULL,
    state text NOT NULL CHECK (state IN ('scheduled', 'fired', 'superseded', 'canceled')),
    expected_flow_revision bigint NOT NULL CHECK (expected_flow_revision > 0),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    resolved_at timestamp with time zone,
    FOREIGN KEY (scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id),
    FOREIGN KEY (wait_id, scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_wait(wait_id, scope_id, flow_instance_id)
);

CREATE INDEX ix_flow_timer_due
    ON appsurface_durable.flow_timer (due_at, timer_id)
    WHERE state = 'scheduled';

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
