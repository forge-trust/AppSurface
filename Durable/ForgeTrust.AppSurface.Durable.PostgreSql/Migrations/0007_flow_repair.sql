-- Flow repair is additive. Existing suspended rows without a V1 descriptor digest remain intentionally unsupported.

ALTER TABLE appsurface_durable.flow_instance
    ADD COLUMN suspension_descriptor_schema text,
    ADD COLUMN suspension_descriptor_sha256 char(64);

ALTER TABLE appsurface_durable.flow_instance
    ADD CONSTRAINT ck_flow_instance_suspension_descriptor_identity
    CHECK
    (
        (suspension_descriptor_schema IS NULL AND suspension_descriptor_sha256 IS NULL)
        OR
        (
            suspension_descriptor_schema = 'appsurface.durable.flow.child-suspension.v1'
            AND suspension_descriptor_sha256 ~ '^[0-9a-f]{64}$'
        )
    ) NOT VALID;

ALTER TABLE appsurface_durable.flow_instance
    VALIDATE CONSTRAINT ck_flow_instance_suspension_descriptor_identity;

ALTER TABLE appsurface_durable.flow_wait
    ADD COLUMN result_contract_id text,
    ADD COLUMN result_schema_version text,
    ADD COLUMN result_codec_id text,
    ADD COLUMN result_classification text,
    ADD COLUMN result_retention text,
    ADD CONSTRAINT ck_flow_wait_result_contract_id
    CHECK (result_contract_id IS NULL OR length(result_contract_id) BETWEEN 1 AND 256) NOT VALID,
    ADD CONSTRAINT ck_flow_wait_result_schema_version
    CHECK (result_schema_version IS NULL OR length(result_schema_version) BETWEEN 1 AND 100) NOT VALID,
    ADD CONSTRAINT ck_flow_wait_result_codec_id
    CHECK (result_codec_id IS NULL OR length(result_codec_id) BETWEEN 1 AND 320) NOT VALID,
    ADD CONSTRAINT ck_flow_wait_result_classification
    CHECK (result_classification IS NULL OR length(result_classification) BETWEEN 1 AND 64) NOT VALID,
    ADD CONSTRAINT ck_flow_wait_result_retention
    CHECK (result_retention IS NULL OR length(result_retention) BETWEEN 1 AND 128) NOT VALID,
    ADD CONSTRAINT ck_flow_wait_activity_result_identity
    CHECK
    (
        (kind = 'event'
            AND result_contract_id IS NULL AND result_schema_version IS NULL AND result_codec_id IS NULL
            AND result_classification IS NULL AND result_retention IS NULL)
        OR
        (kind = 'activity'
            AND
            (
                (result_contract_id IS NULL AND result_schema_version IS NULL AND result_codec_id IS NULL
                    AND result_classification IS NULL AND result_retention IS NULL)
                OR
                (result_contract_id IS NOT NULL AND result_schema_version IS NOT NULL AND result_codec_id IS NOT NULL
                    AND result_classification IS NOT NULL AND result_retention IS NOT NULL)
            ))
    ) NOT VALID;

ALTER TABLE appsurface_durable.flow_wait
    VALIDATE CONSTRAINT ck_flow_wait_result_contract_id,
    VALIDATE CONSTRAINT ck_flow_wait_result_schema_version,
    VALIDATE CONSTRAINT ck_flow_wait_result_codec_id,
    VALIDATE CONSTRAINT ck_flow_wait_result_classification,
    VALIDATE CONSTRAINT ck_flow_wait_result_retention,
    VALIDATE CONSTRAINT ck_flow_wait_activity_result_identity;

ALTER TABLE appsurface_durable.work_operator_command
    ADD COLUMN resolution_kind text,
    ADD CONSTRAINT ck_work_operator_command_resolution_kind_value CHECK
    (
        resolution_kind IS NULL
        OR resolution_kind IN ('applied', 'proven_not_applied')
    ) NOT VALID,
    ADD CONSTRAINT ck_work_operator_command_resolution_kind
    CHECK
    (
        (command_type = 'manual_resolve'
            AND
            (
                resolution_kind IS NULL
                OR (status = 'completed' AND resolution_kind IN ('applied', 'proven_not_applied'))
            ))
        OR
        (command_type <> 'manual_resolve' AND resolution_kind IS NULL)
    ) NOT VALID;

ALTER TABLE appsurface_durable.work_operator_command
    VALIDATE CONSTRAINT ck_work_operator_command_resolution_kind_value,
    VALIDATE CONSTRAINT ck_work_operator_command_resolution_kind;

CREATE TABLE appsurface_durable.flow_repair_command
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    flow_instance_id text NOT NULL CHECK (length(flow_instance_id) BETWEEN 1 AND 200),
    action text NOT NULL CHECK (action IN ('assert_child_effect_completed', 'assert_child_effect_not_applied')),
    request_schema text NOT NULL CHECK (length(request_schema) BETWEEN 1 AND 200),
    request_sha256 char(64) NOT NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
    expected_flow_revision bigint NOT NULL CHECK (expected_flow_revision > 0),
    observed_flow_state text,
    observed_flow_revision bigint CHECK (observed_flow_revision IS NULL OR observed_flow_revision > 0),
    suspension_descriptor_sha256 char(64) NOT NULL CHECK (suspension_descriptor_sha256 ~ '^[0-9a-f]{64}$'),
    child_work_id text NOT NULL CHECK (length(child_work_id) BETWEEN 1 AND 200),
    expected_child_work_revision bigint NOT NULL CHECK (expected_child_work_revision > 0),
    child_work_history_event_id bigint NOT NULL CHECK (child_work_history_event_id > 0),
    expected_child_result_sha256 char(64) CHECK
    (
        expected_child_result_sha256 IS NULL OR expected_child_result_sha256 ~ '^[0-9a-f]{64}$'
    ),
    required_work_operator_command_id text CHECK
    (
        required_work_operator_command_id IS NULL OR length(required_work_operator_command_id) BETWEEN 1 AND 200
    ),
    actor_id text NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 200),
    reason_code text NOT NULL CHECK (length(reason_code) BETWEEN 1 AND 120),
    outcome text NOT NULL CHECK (outcome IN ('applied', 'refused', 'race_lost')),
    problem_code text CHECK (problem_code IS NULL OR length(problem_code) BETWEEN 1 AND 120),
    resulting_state text,
    resulting_revision bigint CHECK (resulting_revision IS NULL OR resulting_revision > 0),
    resulting_flow_history_event_id bigint,
    receipt_sha256 char(64) CHECK (receipt_sha256 IS NULL OR receipt_sha256 ~ '^[0-9a-f]{64}$'),
    accepted_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, command_id),
    CHECK
    (
        (action = 'assert_child_effect_completed'
            AND expected_child_result_sha256 IS NOT NULL AND required_work_operator_command_id IS NULL)
        OR
        (action = 'assert_child_effect_not_applied'
            AND expected_child_result_sha256 IS NULL AND required_work_operator_command_id IS NOT NULL)
    ),
    CHECK
    (
        (outcome = 'applied'
            AND resulting_state IS NOT NULL AND resulting_revision IS NOT NULL
            AND resulting_flow_history_event_id IS NOT NULL AND receipt_sha256 IS NOT NULL AND problem_code IS NULL)
        OR
        (outcome IN ('refused', 'race_lost')
            AND receipt_sha256 IS NULL AND resulting_flow_history_event_id IS NULL)
    )
);

CREATE INDEX ix_flow_repair_command_instance
    ON appsurface_durable.flow_repair_command (scope_id, flow_instance_id, accepted_at, command_id);

CREATE TABLE appsurface_durable.flow_repair_collision
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    conflicting_request_schema text NOT NULL CHECK (length(conflicting_request_schema) BETWEEN 1 AND 200),
    conflicting_request_sha256 char(64) NOT NULL CHECK (conflicting_request_sha256 ~ '^[0-9a-f]{64}$'),
    recorded_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, command_id, conflicting_request_schema, conflicting_request_sha256)
);

ALTER TABLE appsurface_durable.flow_repair_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_repair_command FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_repair_command_scope_isolation ON appsurface_durable.flow_repair_command
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_repair_collision ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_repair_collision FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_repair_collision_scope_isolation ON appsurface_durable.flow_repair_collision
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

REVOKE ALL ON appsurface_durable.flow_repair_command FROM PUBLIC;
REVOKE ALL ON appsurface_durable.flow_repair_collision FROM PUBLIC;
