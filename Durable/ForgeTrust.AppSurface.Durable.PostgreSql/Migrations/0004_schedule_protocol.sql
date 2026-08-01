-- Durable Schedule protocol v1. This migration is forward-only.
-- The passive processor is deliberately Work-first; Flow and Cron admission remain separately fenced in code.

CREATE TABLE appsurface_durable.schedule_definition
(
    scope_id text NOT NULL REFERENCES appsurface_durable.scope(scope_id),
    schedule_id text NOT NULL CHECK (length(schedule_id) BETWEEN 1 AND 200),
    display_name text CHECK (display_name IS NULL OR length(display_name) BETWEEN 1 AND 200),
    state text NOT NULL CHECK (state IN ('active', 'paused', 'deleted', 'suspended')),
    active_generation bigint NOT NULL CHECK (active_generation > 0),
    revision bigint NOT NULL CHECK (revision > 0),
    accepted_at_utc timestamp with time zone NOT NULL,
    cursor_utc timestamp with time zone NOT NULL,
    next_due_utc timestamp with time zone,
    scope_generation bigint NOT NULL CHECK (scope_generation > 0),
    runtime_epoch uuid NOT NULL,
    suspension_code text CHECK (suspension_code IS NULL OR length(suspension_code) BETWEEN 1 AND 120),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, schedule_id),
    CHECK ((state = 'suspended') = (suspension_code IS NOT NULL))
);

CREATE TABLE appsurface_durable.schedule_generation
(
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    accepted_at_utc timestamp with time zone NOT NULL,
    schedule_kind text NOT NULL CHECK (schedule_kind IN ('at', 'after', 'every', 'cron')),
    at_utc timestamp with time zone,
    delay_interval interval,
    interval_value interval,
    anchor_utc timestamp with time zone,
    cron_expression text CHECK (cron_expression IS NULL OR length(cron_expression) BETWEEN 1 AND 512),
    cron_time_zone text CHECK (cron_time_zone IS NULL OR length(cron_time_zone) BETWEEN 1 AND 128),
    cron_dialect text CHECK (cron_dialect IS NULL OR length(cron_dialect) BETWEEN 1 AND 64),
    cron_grammar text CHECK (cron_grammar IS NULL OR length(cron_grammar) BETWEEN 1 AND 64),
    overlap_kind text NOT NULL CHECK (overlap_kind IN ('queue_one', 'skip', 'allow_concurrent')),
    overlap_limit integer NOT NULL CHECK (overlap_limit > 0),
    misfire_kind text NOT NULL CHECK (misfire_kind IN ('run_once', 'skip', 'catch_up')),
    misfire_limit integer NOT NULL CHECK (misfire_limit >= 0),
    target_kind text NOT NULL CHECK (target_kind IN ('work', 'flow')),
    target_name text NOT NULL CHECK (length(target_name) BETWEEN 1 AND 200),
    target_version text NOT NULL CHECK (length(target_version) BETWEEN 1 AND 100),
    target_contract_id text NOT NULL CHECK (length(target_contract_id) BETWEEN 1 AND 200),
    target_schema_version text NOT NULL CHECK (length(target_schema_version) BETWEEN 1 AND 100),
    target_classification text NOT NULL CHECK (length(target_classification) BETWEEN 1 AND 64),
    target_retention text NOT NULL CHECK (length(target_retention) BETWEEN 1 AND 128),
    target_payload bytea NOT NULL,
    target_sha256 char(64) NOT NULL CHECK (target_sha256 ~ '^[0-9a-f]{64}$'),
    target_provider_safety text CHECK (target_provider_safety IN ('idempotent', 'provider_keyed', 'reconcile_before_retry', 'manual_resolution')),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, schedule_id, generation),
    FOREIGN KEY (scope_id, schedule_id) REFERENCES appsurface_durable.schedule_definition(scope_id, schedule_id),
    CHECK
    (
        (schedule_kind = 'at' AND at_utc IS NOT NULL AND delay_interval IS NULL AND interval_value IS NULL
            AND anchor_utc IS NULL AND cron_expression IS NULL AND cron_time_zone IS NULL)
        OR
        (schedule_kind = 'after' AND at_utc IS NULL AND delay_interval > interval '0 seconds' AND interval_value IS NULL
            AND anchor_utc IS NULL AND cron_expression IS NULL AND cron_time_zone IS NULL)
        OR
        (schedule_kind = 'every' AND at_utc IS NULL AND delay_interval IS NULL AND interval_value > interval '0 seconds'
            AND cron_expression IS NULL AND cron_time_zone IS NULL)
        OR
        (schedule_kind = 'cron' AND at_utc IS NULL AND delay_interval IS NULL AND interval_value IS NULL
            AND cron_expression IS NOT NULL AND cron_time_zone IS NOT NULL AND cron_dialect IS NOT NULL AND cron_grammar IS NOT NULL)
    ),
    CHECK
    (
        (target_kind = 'work' AND target_provider_safety IS NOT NULL)
        OR
        (target_kind = 'flow' AND target_provider_safety IS NULL)
    )
);

CREATE TABLE appsurface_durable.schedule_command
(
    scope_id text NOT NULL,
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    idempotency_key text CHECK (idempotency_key IS NULL OR length(idempotency_key) BETWEEN 1 AND 200),
    schedule_id text NOT NULL CHECK (length(schedule_id) BETWEEN 1 AND 200),
    command_kind text NOT NULL CHECK (command_kind IN ('create', 'update', 'pause', 'resume', 'delete', 'recovery_release')),
    request_fingerprint_schema text NOT NULL CHECK (length(request_fingerprint_schema) BETWEEN 1 AND 200),
    request_fingerprint_sha256 char(64) NOT NULL CHECK (request_fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
    outcome text NOT NULL CHECK (outcome IN ('created', 'updated', 'paused', 'resumed', 'deleted', 'recovery_released', 'unchanged')),
    resulting_generation bigint NOT NULL CHECK (resulting_generation > 0),
    resulting_revision bigint NOT NULL CHECK (resulting_revision > 0),
    accepted_at_utc timestamp with time zone NOT NULL,
    PRIMARY KEY (scope_id, command_id),
    UNIQUE (scope_id, idempotency_key),
    FOREIGN KEY (scope_id, schedule_id) REFERENCES appsurface_durable.schedule_definition(scope_id, schedule_id)
);

CREATE TABLE appsurface_durable.schedule_occurrence
(
    scope_id text NOT NULL,
    schedule_id text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    occurrence_id text NOT NULL CHECK (length(occurrence_id) BETWEEN 1 AND 200),
    occurrence_kind text NOT NULL CHECK (occurrence_kind IN ('nominal', 'recovery', 'coalesced')),
    first_nominal_utc timestamp with time zone NOT NULL,
    last_nominal_utc timestamp with time zone NOT NULL,
    state text NOT NULL CHECK (state IN ('pending', 'claimed', 'materialized', 'skipped', 'superseded', 'canceled', 'suspended')),
    target_kind text CHECK (target_kind IN ('work', 'flow')),
    target_id text CHECK (target_id IS NULL OR length(target_id) BETWEEN 1 AND 200),
    target_command_id text CHECK (target_command_id IS NULL OR length(target_command_id) BETWEEN 1 AND 200),
    target_idempotency_key text CHECK (target_idempotency_key IS NULL OR length(target_idempotency_key) BETWEEN 1 AND 200),
    claimed_by text CHECK (claimed_by IS NULL OR length(claimed_by) BETWEEN 1 AND 200),
    lease_expires_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, schedule_id, generation, occurrence_id),
    UNIQUE (scope_id, schedule_id, generation, occurrence_kind, first_nominal_utc),
    FOREIGN KEY (scope_id, schedule_id, generation)
        REFERENCES appsurface_durable.schedule_generation(scope_id, schedule_id, generation),
    CHECK (last_nominal_utc >= first_nominal_utc),
    CHECK
    (
        (target_kind IS NULL AND target_id IS NULL AND target_command_id IS NULL AND target_idempotency_key IS NULL)
        OR
        (target_kind IS NOT NULL AND target_id IS NOT NULL AND target_command_id IS NOT NULL AND target_idempotency_key IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_schedule_occurrence_target
    ON appsurface_durable.schedule_occurrence (scope_id, target_kind, target_id)
    WHERE target_kind IS NOT NULL;

CREATE UNIQUE INDEX ux_schedule_occurrence_pending_coalesced
    ON appsurface_durable.schedule_occurrence (scope_id, schedule_id, generation)
    WHERE occurrence_kind = 'coalesced' AND state = 'pending';

CREATE TABLE appsurface_durable.schedule_dispatch
(
    dispatch_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_id text NOT NULL CHECK (length(scope_id) BETWEEN 1 AND 200),
    schedule_id text NOT NULL CHECK (length(schedule_id) BETWEEN 1 AND 200),
    dispatch_revision bigint NOT NULL CHECK (dispatch_revision > 0),
    due_at timestamp with time zone NOT NULL,
    state text NOT NULL CHECK (state IN ('available', 'leased', 'terminal', 'suspended')),
    lease_owner text CHECK (lease_owner IS NULL OR length(lease_owner) BETWEEN 1 AND 200),
    lease_generation bigint NOT NULL DEFAULT 0 CHECK (lease_generation >= 0),
    lease_expires_at timestamp with time zone,
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (scope_id, schedule_id),
    FOREIGN KEY (scope_id, schedule_id) REFERENCES appsurface_durable.schedule_definition(scope_id, schedule_id)
);

CREATE INDEX ix_schedule_dispatch_due
    ON appsurface_durable.schedule_dispatch (due_at, dispatch_id)
    INCLUDE (scope_id, schedule_id, dispatch_revision)
    WHERE state IN ('available', 'leased');

CREATE TABLE appsurface_durable.schedule_history
(
    event_id bigint GENERATED ALWAYS AS IDENTITY,
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    scope_id text NOT NULL CHECK (length(scope_id) BETWEEN 1 AND 200),
    schedule_id text NOT NULL CHECK (length(schedule_id) BETWEEN 1 AND 200),
    generation bigint CHECK (generation IS NULL OR generation > 0),
    occurrence_id text CHECK (occurrence_id IS NULL OR length(occurrence_id) BETWEEN 1 AND 200),
    event_type text NOT NULL CHECK (length(event_type) BETWEEN 1 AND 96),
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    CHECK (jsonb_typeof(details) = 'object'),
    CHECK (octet_length(details::text) <= 1024)
) PARTITION BY RANGE (observed_at);

CREATE FUNCTION appsurface_durable.ensure_schedule_history_partitions()
RETURNS void
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, appsurface_durable
AS $$
DECLARE
    current_month date := date_trunc('month', CURRENT_DATE)::date;
    next_month date := (date_trunc('month', CURRENT_DATE) + interval '1 month')::date;
    after_next_month date := (date_trunc('month', CURRENT_DATE) + interval '2 months')::date;
    current_child text := format('schedule_history_%s', to_char(current_month, 'YYYYMM'));
    next_child text := format('schedule_history_%s', to_char(next_month, 'YYYYMM'));
BEGIN
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS appsurface_durable.schedule_history_%s PARTITION OF appsurface_durable.schedule_history FOR VALUES FROM (%L) TO (%L)',
        to_char(current_month, 'YYYYMM'), current_month, next_month);
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS appsurface_durable.schedule_history_%s PARTITION OF appsurface_durable.schedule_history FOR VALUES FROM (%L) TO (%L)',
        to_char(next_month, 'YYYYMM'), next_month, after_next_month);
    FOREACH current_child IN ARRAY ARRAY[current_child, next_child]
    LOOP
        EXECUTE format('ALTER TABLE appsurface_durable.%I ENABLE ROW LEVEL SECURITY', current_child);
        EXECUTE format('ALTER TABLE appsurface_durable.%I FORCE ROW LEVEL SECURITY', current_child);
        EXECUTE format('DROP POLICY IF EXISTS schedule_history_scope_isolation ON appsurface_durable.%I', current_child);
        EXECUTE format(
            'CREATE POLICY schedule_history_scope_isolation ON appsurface_durable.%I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), '''')) WITH CHECK (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))',
            current_child);
    END LOOP;
END;
$$;

SELECT appsurface_durable.ensure_schedule_history_partitions();
REVOKE ALL ON FUNCTION appsurface_durable.ensure_schedule_history_partitions() FROM PUBLIC;

CREATE INDEX ix_schedule_history_schedule
    ON appsurface_durable.schedule_history (scope_id, schedule_id, observed_at, event_id);

ALTER TABLE appsurface_durable.schedule_definition ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_definition FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_definition_scope_isolation ON appsurface_durable.schedule_definition
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_generation ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_generation FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_generation_scope_isolation ON appsurface_durable.schedule_generation
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

ALTER TABLE appsurface_durable.schedule_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_history FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_history_scope_isolation ON appsurface_durable.schedule_history
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.schedule_dispatch ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.schedule_dispatch FORCE ROW LEVEL SECURITY;
-- Role narrowing is completed by Durable/configure-postgresql-roles.sql. The migration must retain a
-- global discovery/lease policy so an authorized dispatcher can claim without setting tenant scope.
CREATE POLICY schedule_dispatch_global_discovery ON appsurface_durable.schedule_dispatch
    FOR SELECT USING (true);
CREATE POLICY schedule_dispatch_global_lease ON appsurface_durable.schedule_dispatch
    FOR UPDATE USING (true) WITH CHECK (true);
CREATE POLICY schedule_dispatch_scope_insert ON appsurface_durable.schedule_dispatch
    FOR INSERT WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

-- The dispatcher needs due-time visibility to choose one queue row, but its role must not
-- receive a raw table read that could disclose scheduling cadence. This constrained claim
-- function returns only the routing IDs and revision needed for the scoped runtime bridge.
CREATE FUNCTION appsurface_durable.claim_schedule_dispatch(
    p_lease_owner text,
    p_lease_duration interval)
RETURNS TABLE
(
    scope_id text,
    schedule_id text,
    dispatch_revision bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable
AS $$
BEGIN
    IF p_lease_owner IS NULL
       OR btrim(p_lease_owner) = ''
       OR length(p_lease_owner) > 200
       OR p_lease_owner ~ '[[:cntrl:]]'
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Schedule dispatcher lease owner must contain 1 to 200 non-control characters.';
    END IF;

    IF p_lease_duration IS NULL
       OR p_lease_duration <= interval '0 seconds'
       OR p_lease_duration > interval '10 minutes'
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Schedule dispatcher lease duration must be greater than zero and no longer than ten minutes.';
    END IF;

    RETURN QUERY
    WITH candidate AS
    (
        SELECT dispatch_id
        FROM appsurface_durable.schedule_dispatch
        WHERE (state = 'available' AND due_at <= clock_timestamp())
           OR (state = 'leased' AND lease_expires_at < clock_timestamp())
        ORDER BY due_at, dispatch_id
        FOR UPDATE SKIP LOCKED
        LIMIT 1
    )
    UPDATE appsurface_durable.schedule_dispatch AS dispatch
    SET state = 'leased',
        lease_owner = p_lease_owner,
        lease_generation = dispatch.lease_generation + 1,
        lease_expires_at = clock_timestamp() + p_lease_duration,
        updated_at = clock_timestamp()
    FROM candidate
    WHERE dispatch.dispatch_id = candidate.dispatch_id
    RETURNING dispatch.scope_id, dispatch.schedule_id, dispatch.dispatch_revision;
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) FROM PUBLIC;

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
