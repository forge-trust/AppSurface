CREATE TABLE appsurface_durable.schedule
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    schedule_id text NOT NULL CHECK (length(schedule_id) BETWEEN 1 AND 200),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, schedule_id)
);

CREATE TABLE appsurface_durable.schedule_current
(
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    display_name text CHECK (display_name IS NULL OR length(display_name) BETWEEN 1 AND 200),
    state text NOT NULL CHECK (state IN ('active', 'paused', 'deleted', 'suspended')),
    generation bigint NOT NULL CHECK (generation > 0),
    revision bigint NOT NULL CHECK (revision > 0),
    schedule_kind text NOT NULL CHECK (schedule_kind IN ('at', 'after', 'every', 'cron')),
    at_utc timestamp with time zone,
    delay interval,
    every_interval interval,
    anchor_utc timestamp with time zone,
    overlap_kind text NOT NULL CHECK (overlap_kind IN ('queue_one', 'skip', 'allow_concurrent')),
    overlap_maximum integer NOT NULL CHECK (overlap_maximum > 0),
    misfire_kind text NOT NULL CHECK (misfire_kind IN ('run_once', 'skip', 'catch_up')),
    misfire_maximum integer NOT NULL CHECK (misfire_maximum >= 0),
    cron_expression text,
    cron_dialect text,
    cron_grammar text,
    iana_time_zone_id text,
    cron_evaluator_version text,
    cron_jitter_seed integer,
    time_zone_rules_fingerprint char(64),
    target_kind text NOT NULL CHECK (target_kind IN ('work', 'flow')),
    target_name text NOT NULL CHECK (length(target_name) BETWEEN 1 AND 200),
    target_version text NOT NULL CHECK (length(target_version) BETWEEN 1 AND 100),
    target_provider_safety text CHECK (target_provider_safety IS NULL OR target_provider_safety IN
    (
        'idempotent',
        'provider_keyed',
        'reconcile_before_retry',
        'manual_resolution'
    )),
    target_contract_name text NOT NULL CHECK (length(target_contract_name) BETWEEN 1 AND 200),
    target_contract_version text NOT NULL CHECK (length(target_contract_version) BETWEEN 1 AND 100),
    target_classification smallint NOT NULL CHECK (target_classification IN (0, 1)),
    target_retention_policy_id text NOT NULL CHECK (length(target_retention_policy_id) BETWEEN 1 AND 128),
    target_payload bytea NOT NULL,
    target_payload_sha256 bytea NOT NULL CHECK (octet_length(target_payload_sha256) = 32),
    accepted_at timestamp with time zone NOT NULL,
    next_nominal_due_utc timestamp with time zone,
    pending_generation bigint,
    pending_nominal_due_utc timestamp with time zone,
    pending_covered_through_utc timestamp with time zone,
    pending_covered_occurrence_count bigint,
    catch_up_remaining integer CHECK (catch_up_remaining IS NULL OR catch_up_remaining > 0),
    suspended_from_state text CHECK (suspended_from_state IS NULL OR suspended_from_state IN ('active', 'paused')),
    suspension_code text CHECK (suspension_code IS NULL OR length(suspension_code) BETWEEN 1 AND 120),
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    deleted_at timestamp with time zone,
    PRIMARY KEY (scope_id, schedule_id),
    FOREIGN KEY (scope_id, schedule_id)
        REFERENCES appsurface_durable.schedule(scope_id, schedule_id),
    CHECK
    (
        (schedule_kind = 'at' AND at_utc IS NOT NULL AND delay IS NULL
            AND every_interval IS NULL AND anchor_utc IS NULL)
        OR
        (schedule_kind = 'after' AND at_utc IS NULL AND delay > interval '0 seconds'
            AND every_interval IS NULL AND anchor_utc IS NOT NULL)
        OR
        (schedule_kind = 'every' AND at_utc IS NULL AND delay IS NULL
            AND every_interval > interval '0 seconds' AND anchor_utc IS NOT NULL)
        OR
        (schedule_kind = 'cron' AND at_utc IS NULL AND delay IS NULL
            AND every_interval IS NULL AND anchor_utc IS NULL)
    ),
    CHECK
    (
        (target_kind = 'work' AND target_provider_safety IS NOT NULL)
        OR
        (target_kind = 'flow' AND target_provider_safety IS NULL)
    ),
    CHECK
    (
        (schedule_kind = 'cron'
            AND cron_expression IS NOT NULL
            AND cron_dialect IS NOT NULL
            AND cron_grammar IS NOT NULL
            AND iana_time_zone_id IS NOT NULL
            AND cron_evaluator_version IS NOT NULL
            AND cron_jitter_seed IS NOT NULL
            AND time_zone_rules_fingerprint IS NOT NULL)
        OR
        (schedule_kind <> 'cron'
            AND cron_expression IS NULL
            AND cron_dialect IS NULL
            AND cron_grammar IS NULL
            AND iana_time_zone_id IS NULL
            AND cron_evaluator_version IS NULL
            AND cron_jitter_seed IS NULL
            AND time_zone_rules_fingerprint IS NULL)
    ),
    CHECK
    (
        (pending_generation IS NULL
            AND pending_nominal_due_utc IS NULL
            AND pending_covered_through_utc IS NULL
            AND pending_covered_occurrence_count IS NULL)
        OR
        (pending_generation > 0
            AND pending_nominal_due_utc IS NOT NULL
            AND pending_covered_through_utc >= pending_nominal_due_utc
            AND (pending_covered_occurrence_count IS NULL OR pending_covered_occurrence_count > 0))
    ),
    CHECK (suspended_from_state IS NULL OR suspension_code IS NOT NULL)
);

CREATE INDEX ix_schedule_current_state
    ON appsurface_durable.schedule_current (scope_id, state, next_nominal_due_utc, schedule_id);

CREATE TABLE appsurface_durable.schedule_history
(
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    aggregate_revision bigint NOT NULL CHECK (aggregate_revision > 0),
    schedule_generation bigint NOT NULL CHECK (schedule_generation > 0),
    event_type text NOT NULL CHECK (length(event_type) BETWEEN 1 AND 96),
    command_id text,
    actor_id text CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    reason_code text CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    nominal_due_utc timestamp with time zone,
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    FOREIGN KEY (scope_id, schedule_id)
        REFERENCES appsurface_durable.schedule(scope_id, schedule_id),
    UNIQUE (scope_id, schedule_id, aggregate_revision),
    CHECK (jsonb_typeof(details) = 'object'),
    CHECK (octet_length(details::text) <= 16384),
    CHECK ((actor_id IS NULL) = (reason_code IS NULL))
);

CREATE INDEX ix_schedule_history_schedule
    ON appsurface_durable.schedule_history (scope_id, schedule_id, event_id);

CREATE TABLE appsurface_durable.schedule_command
(
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    idempotency_key text CHECK (idempotency_key IS NULL OR length(idempotency_key) BETWEEN 1 AND 200),
    command_type text NOT NULL CHECK (command_type IN ('create', 'update', 'pause', 'resume', 'delete', 'recovery_release')),
    actor_id text CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    reason_code text CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    request_sha256 bytea NOT NULL CHECK (octet_length(request_sha256) = 32),
    result_code text NOT NULL CHECK (result_code IN
        ('created', 'updated', 'paused', 'resumed', 'deleted', 'unchanged', 'recovery_released')),
    resulting_generation bigint NOT NULL CHECK (resulting_generation > 0),
    resulting_revision bigint NOT NULL CHECK (resulting_revision > 0),
    committed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, command_id),
    FOREIGN KEY (scope_id, schedule_id)
        REFERENCES appsurface_durable.schedule(scope_id, schedule_id),
    CHECK
    (
        (command_type IN ('pause', 'resume', 'delete', 'recovery_release')
            AND actor_id IS NOT NULL AND reason_code IS NOT NULL)
        OR
        (command_type IN ('create', 'update') AND actor_id IS NULL AND reason_code IS NULL)
    )
);

CREATE UNIQUE INDEX ux_schedule_command_idempotency
    ON appsurface_durable.schedule_command (scope_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE INDEX ix_schedule_command_schedule
    ON appsurface_durable.schedule_command (scope_id, schedule_id, committed_at);

CREATE TABLE appsurface_durable.schedule_occurrence
(
    occurrence_id uuid PRIMARY KEY,
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    schedule_generation bigint NOT NULL CHECK (schedule_generation > 0),
    nominal_due_utc timestamp with time zone NOT NULL,
    covered_through_utc timestamp with time zone NOT NULL,
    covered_occurrence_count bigint CHECK (covered_occurrence_count IS NULL OR covered_occurrence_count > 0),
    is_recovery boolean NOT NULL,
    state text NOT NULL CHECK (state IN ('ready', 'queued', 'coalesced', 'started', 'terminal', 'skipped', 'invalidated')),
    target_kind text CHECK (target_kind IS NULL OR target_kind IN ('work', 'flow')),
    target_id text CHECK (target_id IS NULL OR length(target_id) BETWEEN 1 AND 200),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    started_at timestamp with time zone,
    resolved_at timestamp with time zone,
    FOREIGN KEY (scope_id, schedule_id)
        REFERENCES appsurface_durable.schedule(scope_id, schedule_id),
    UNIQUE (scope_id, schedule_id, schedule_generation, nominal_due_utc),
    CHECK (covered_through_utc >= nominal_due_utc),
    CHECK
    (
        (state IN ('started', 'terminal') AND target_kind IS NOT NULL AND target_id IS NOT NULL AND started_at IS NOT NULL)
        OR
        (state NOT IN ('started', 'terminal') AND target_kind IS NULL AND target_id IS NULL AND started_at IS NULL)
    )
);

CREATE INDEX ix_schedule_occurrence_schedule
    ON appsurface_durable.schedule_occurrence
        (scope_id, schedule_id, schedule_generation, nominal_due_utc);

CREATE INDEX ix_schedule_occurrence_pending
    ON appsurface_durable.schedule_occurrence (scope_id, schedule_id, state, nominal_due_utc)
    WHERE state IN ('ready', 'queued');

CREATE TABLE appsurface_durable.schedule_run_slot
(
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    occurrence_id uuid NOT NULL,
    schedule_generation bigint NOT NULL CHECK (schedule_generation > 0),
    target_kind text NOT NULL CHECK (target_kind IN ('work', 'flow')),
    target_id text NOT NULL CHECK (length(target_id) BETWEEN 1 AND 200),
    state text NOT NULL CHECK (state IN ('active', 'released')),
    acquired_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    released_at timestamp with time zone,
    terminal_code text,
    PRIMARY KEY (scope_id, schedule_id, occurrence_id),
    FOREIGN KEY (scope_id, schedule_id)
        REFERENCES appsurface_durable.schedule(scope_id, schedule_id),
    FOREIGN KEY (occurrence_id)
        REFERENCES appsurface_durable.schedule_occurrence(occurrence_id),
    UNIQUE (scope_id, target_kind, target_id),
    CHECK
    (
        (state = 'active' AND released_at IS NULL)
        OR
        (state = 'released' AND released_at IS NOT NULL)
    )
);

CREATE INDEX ix_schedule_run_slot_active
    ON appsurface_durable.schedule_run_slot (scope_id, schedule_id, acquired_at)
    WHERE state = 'active';

ALTER TABLE appsurface_durable.schedule ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_scope_isolation ON appsurface_durable.schedule
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_current ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_current FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_current_scope_isolation ON appsurface_durable.schedule_current
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_history FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_history_scope_isolation ON appsurface_durable.schedule_history
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_command FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_command_scope_isolation ON appsurface_durable.schedule_command
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_occurrence ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_occurrence FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_occurrence_scope_isolation ON appsurface_durable.schedule_occurrence
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_run_slot ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_run_slot FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_run_slot_scope_isolation ON appsurface_durable.schedule_run_slot
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
