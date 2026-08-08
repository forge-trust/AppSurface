-- Verified one-Flow history retention. This migration deliberately creates no schedule, age predicate, partition,
-- or autonomous deletion job. Applications own authorization and policy cadence; the provider records the bounded
-- evidence that later gates a separately authorized purge.

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_catalog.pg_extension AS extension_value
        JOIN pg_catalog.pg_namespace AS schema_value ON schema_value.oid = extension_value.extnamespace
        WHERE extension_value.extname = 'pgcrypto' AND schema_value.nspname = 'public'
    ) THEN
        RAISE EXCEPTION 'The pgcrypto extension must be installed in the public schema for Flow retention.'
            USING HINT = 'Move the existing pgcrypto extension to public or install it there before applying this migration.';
    END IF;
END;
$$;

CREATE TABLE appsurface_durable.flow_retention_manifest
(
    scope_id text NOT NULL,
    retention_manifest_id text NOT NULL CHECK (length(retention_manifest_id) BETWEEN 1 AND 200),
    flow_instance_id text NOT NULL CHECK (length(flow_instance_id) BETWEEN 1 AND 200),
    closure_schema text NOT NULL CHECK (length(closure_schema) BETWEEN 1 AND 200),
    closure_sha256 char(64) NOT NULL CHECK (closure_sha256 ~ '^[0-9a-f]{64}$'),
    source_watermark_schema text NOT NULL CHECK (length(source_watermark_schema) BETWEEN 1 AND 200),
    source_watermark_sha256 char(64) NOT NULL CHECK (source_watermark_sha256 ~ '^[0-9a-f]{64}$'),
    closure_item_count integer NOT NULL CHECK (closure_item_count BETWEEN 1 AND 10000),
    archive_byte_count bigint NOT NULL CHECK (archive_byte_count BETWEEN 0 AND 67108864),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, retention_manifest_id),
    FOREIGN KEY (scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id)
);

CREATE UNIQUE INDEX ux_flow_retention_manifest_flow
    ON appsurface_durable.flow_retention_manifest (scope_id, flow_instance_id);

CREATE TABLE appsurface_durable.flow_retention_manifest_item
(
    scope_id text NOT NULL,
    retention_manifest_id text NOT NULL CHECK (length(retention_manifest_id) BETWEEN 1 AND 200),
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    item_rank smallint NOT NULL CHECK (item_rank BETWEEN 1 AND 100),
    item_kind text NOT NULL CHECK (length(item_kind) BETWEEN 1 AND 64),
    primary_key text NOT NULL CHECK (length(primary_key) BETWEEN 1 AND 512),
    canonical_sha256 char(64) NOT NULL CHECK (canonical_sha256 ~ '^[0-9a-f]{64}$'),
    archiveable boolean NOT NULL,
    PRIMARY KEY (scope_id, retention_manifest_id, ordinal),
    UNIQUE (scope_id, retention_manifest_id, item_rank, primary_key),
    FOREIGN KEY (scope_id, retention_manifest_id)
        REFERENCES appsurface_durable.flow_retention_manifest(scope_id, retention_manifest_id)
);

CREATE TABLE appsurface_durable.flow_retention_manifest_summary
(
    scope_id text NOT NULL,
    retention_manifest_id text NOT NULL CHECK (length(retention_manifest_id) BETWEEN 1 AND 200),
    lifecycle_state text NOT NULL CHECK (lifecycle_state IN ('frozen', 'archive_receipt_recorded', 'verified', 'held', 'purged')),
    lifecycle_sequence bigint NOT NULL CHECK (lifecycle_sequence > 0),
    receipt_id text CHECK (receipt_id IS NULL OR length(receipt_id) BETWEEN 1 AND 200),
    receipt_package_schema text CHECK (receipt_package_schema IS NULL OR length(receipt_package_schema) BETWEEN 1 AND 200),
    receipt_package_sha256 char(64) CHECK (receipt_package_sha256 IS NULL OR receipt_package_sha256 ~ '^[0-9a-f]{64}$'),
    receipt_closure_schema text CHECK (receipt_closure_schema IS NULL OR length(receipt_closure_schema) BETWEEN 1 AND 200),
    receipt_closure_sha256 char(64) CHECK (receipt_closure_sha256 IS NULL OR receipt_closure_sha256 ~ '^[0-9a-f]{64}$'),
    receipt_record_count integer CHECK (receipt_record_count IS NULL OR receipt_record_count BETWEEN 1 AND 10000),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, retention_manifest_id),
    FOREIGN KEY (scope_id, retention_manifest_id)
        REFERENCES appsurface_durable.flow_retention_manifest(scope_id, retention_manifest_id),
    CHECK
    (
        (lifecycle_state = 'frozen' AND receipt_id IS NULL AND receipt_package_schema IS NULL
            AND receipt_package_sha256 IS NULL AND receipt_closure_schema IS NULL
            AND receipt_closure_sha256 IS NULL AND receipt_record_count IS NULL)
        OR
        (lifecycle_state <> 'frozen' AND receipt_id IS NOT NULL AND receipt_package_schema IS NOT NULL
            AND receipt_package_sha256 IS NOT NULL AND receipt_closure_schema IS NOT NULL
            AND receipt_closure_sha256 IS NOT NULL AND receipt_record_count IS NOT NULL)
    )
);

CREATE TABLE appsurface_durable.flow_retention_manifest_event
(
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope_id text NOT NULL,
    retention_manifest_id text NOT NULL CHECK (length(retention_manifest_id) BETWEEN 1 AND 200),
    lifecycle_sequence bigint NOT NULL CHECK (lifecycle_sequence > 0),
    event_type text NOT NULL CHECK (event_type IN
    (
        'manifest_created', 'archive_receipt_recorded', 'source_correspondence_verified',
        'hold_placed', 'hold_released', 'purge_authorized', 'purged'
    )),
    command_id text CHECK (command_id IS NULL OR length(command_id) BETWEEN 1 AND 200),
    actor_id text CHECK (actor_id IS NULL OR length(actor_id) BETWEEN 1 AND 200),
    reason_code text CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 120),
    observed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (scope_id, retention_manifest_id, lifecycle_sequence),
    FOREIGN KEY (scope_id, retention_manifest_id)
        REFERENCES appsurface_durable.flow_retention_manifest(scope_id, retention_manifest_id)
);

CREATE TABLE appsurface_durable.flow_retention_command
(
    scope_id text NOT NULL,
    command_id text NOT NULL CHECK (length(command_id) BETWEEN 1 AND 200),
    retention_manifest_id text NOT NULL CHECK (length(retention_manifest_id) BETWEEN 1 AND 200),
    command_type text NOT NULL CHECK (command_type IN ('manifest_create', 'archive_receipt', 'verify', 'hold', 'purge')),
    fingerprint_schema text NOT NULL CHECK (length(fingerprint_schema) BETWEEN 1 AND 200),
    fingerprint_sha256 char(64) NOT NULL CHECK (fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
    outcome text NOT NULL CHECK (outcome IN ('created', 'applied', 'already_purged')),
    resulting_state text NOT NULL CHECK (resulting_state IN ('frozen', 'archive_receipt_recorded', 'verified', 'held', 'purged')),
    resulting_lifecycle_sequence bigint NOT NULL CHECK (resulting_lifecycle_sequence > 0),
    accepted_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_id, command_id),
    FOREIGN KEY (scope_id, retention_manifest_id)
        REFERENCES appsurface_durable.flow_retention_manifest(scope_id, retention_manifest_id)
);

CREATE FUNCTION appsurface_durable.reject_flow_retention_audit_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Flow retention audit evidence is append-only.';
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.reject_flow_retention_audit_mutation() FROM PUBLIC;

CREATE TRIGGER flow_retention_manifest_event_append_only
    BEFORE UPDATE OR DELETE ON appsurface_durable.flow_retention_manifest_event
    FOR EACH ROW EXECUTE FUNCTION appsurface_durable.reject_flow_retention_audit_mutation();

CREATE TRIGGER flow_retention_command_append_only
    BEFORE UPDATE OR DELETE ON appsurface_durable.flow_retention_command
    FOR EACH ROW EXECUTE FUNCTION appsurface_durable.reject_flow_retention_audit_mutation();

ALTER TABLE appsurface_durable.flow_instance
    ADD COLUMN retention_manifest_id text,
    ADD COLUMN retention_lifecycle_sequence bigint,
    ADD CONSTRAINT fk_flow_instance_retention_manifest
        FOREIGN KEY (scope_id, retention_manifest_id)
        REFERENCES appsurface_durable.flow_retention_manifest(scope_id, retention_manifest_id),
    ADD CONSTRAINT ck_flow_instance_retention_lifecycle
        CHECK
        (
            (retention_manifest_id IS NULL AND retention_lifecycle_sequence IS NULL)
            OR
            (retention_manifest_id IS NOT NULL AND retention_lifecycle_sequence > 0)
        );

-- Retention mutations run as owner-defined capabilities. The retention operator retains only scoped reads and
-- EXECUTE on the two functions below; direct lifecycle/source DML is deliberately unavailable to that login role.
CREATE FUNCTION appsurface_durable.flow_retention_scope_is_current(p_scope_id text)
RETURNS boolean
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
    SELECT p_scope_id IS NOT NULL
       AND p_scope_id = COALESCE(nullif(current_setting('appsurface_durable.scope_id', true), ''), '');
$$;

CREATE FUNCTION appsurface_durable.flow_retention_source_is_safe(p_scope_id text, p_flow_instance_id text)
RETURNS boolean
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
SET TimeZone = 'UTC'
SET DateStyle = 'ISO, MDY'
SET IntervalStyle = 'postgres'
SET extra_float_digits = '3'
AS $$
    SELECT EXISTS
    (
        SELECT 1
        FROM appsurface_durable.flow_instance AS instance
        WHERE instance.scope_id = p_scope_id
          AND instance.flow_instance_id = p_flow_instance_id
          AND instance.state IN ('completed', 'faulted', 'canceled')
    )
    AND NOT EXISTS
    (
        SELECT 1 FROM appsurface_durable.flow_wait
        WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id
          AND state IN ('active', 'suspended')
        UNION ALL
        SELECT 1 FROM appsurface_durable.flow_timer
        WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id AND state = 'scheduled'
        UNION ALL
        SELECT 1 FROM appsurface_durable.flow_dispatch
        WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id
          AND state IN ('available', 'leased', 'suspended')
        UNION ALL
        SELECT 1
        FROM appsurface_durable.flow_wait AS wait
        JOIN appsurface_durable.work AS work
          ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
        WHERE wait.scope_id = p_scope_id AND wait.flow_instance_id = p_flow_instance_id
          AND wait.child_work_id IS NOT NULL
          AND work.state NOT IN ('succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect')
    );
$$;

CREATE FUNCTION appsurface_durable.flow_retention_source_items(p_scope_id text, p_flow_instance_id text)
RETURNS TABLE(item_rank smallint, item_kind text, primary_key text, canonical_sha256 char(64), archiveable boolean)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
SET TimeZone = 'UTC'
SET DateStyle = 'ISO, MDY'
SET IntervalStyle = 'postgres'
SET extra_float_digits = '3'
AS $$
    SELECT 10::smallint, 'flow_instance'::text, instance.flow_instance_id,
           encode(public.digest(convert_to(to_jsonb(instance)::text, 'UTF8'), 'sha256'), 'hex')::char(64), true
    FROM appsurface_durable.flow_instance AS instance
    WHERE instance.scope_id = p_scope_id AND instance.flow_instance_id = p_flow_instance_id
    UNION ALL
    SELECT 20::smallint, 'flow_command'::text, command.command_id,
           encode(public.digest(convert_to(to_jsonb(command)::text, 'UTF8'), 'sha256'), 'hex')::char(64), false
    FROM appsurface_durable.flow_command AS command
    WHERE command.scope_id = p_scope_id AND command.flow_instance_id = p_flow_instance_id
    UNION ALL
    SELECT 30::smallint, 'flow_history'::text, history.event_id::text,
           encode(public.digest(convert_to(to_jsonb(history)::text, 'UTF8'), 'sha256'), 'hex')::char(64), true
    FROM appsurface_durable.flow_history AS history
    WHERE history.scope_id = p_scope_id AND history.flow_instance_id = p_flow_instance_id
    UNION ALL
    SELECT 40::smallint, 'flow_wait'::text, wait.wait_id::text,
           encode(public.digest(convert_to(to_jsonb(wait)::text, 'UTF8'), 'sha256'), 'hex')::char(64), true
    FROM appsurface_durable.flow_wait AS wait
    WHERE wait.scope_id = p_scope_id AND wait.flow_instance_id = p_flow_instance_id
    UNION ALL
    SELECT 50::smallint, 'flow_timer'::text, timer.timer_id::text,
           encode(public.digest(convert_to(to_jsonb(timer)::text, 'UTF8'), 'sha256'), 'hex')::char(64), true
    FROM appsurface_durable.flow_timer AS timer
    WHERE timer.scope_id = p_scope_id AND timer.flow_instance_id = p_flow_instance_id
    UNION ALL
    SELECT 60::smallint, 'flow_dispatch'::text, dispatch.dispatch_id::text,
           encode(public.digest(convert_to(to_jsonb(dispatch)::text, 'UTF8'), 'sha256'), 'hex')::char(64), true
    FROM appsurface_durable.flow_dispatch AS dispatch
    WHERE dispatch.scope_id = p_scope_id AND dispatch.flow_instance_id = p_flow_instance_id
    UNION ALL
    SELECT 70::smallint, 'work_reference'::text, work.work_id,
           encode(public.digest(convert_to(jsonb_build_object('scope_id', work.scope_id, 'work_id', work.work_id,
               'state', work.state, 'revision', work.revision, 'terminal_at', work.terminal_at, 'terminal_code', work.terminal_code)::text, 'UTF8'), 'sha256'), 'hex')::char(64), false
    FROM appsurface_durable.flow_wait AS wait
    JOIN appsurface_durable.work AS work
      ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
    WHERE wait.scope_id = p_scope_id AND wait.flow_instance_id = p_flow_instance_id AND wait.child_work_id IS NOT NULL
    UNION ALL
    SELECT 80::smallint, 'flow_trace_context'::text, trace.trace_context_id::text,
           encode(public.digest(convert_to(to_jsonb(trace)::text, 'UTF8'), 'sha256'), 'hex')::char(64), false
    FROM appsurface_durable.flow_trace_context AS trace
    WHERE trace.scope_id = p_scope_id AND trace.flow_instance_id = p_flow_instance_id;
$$;

CREATE FUNCTION appsurface_durable.flow_retention_source_matches_items(p_scope_id text, p_flow_instance_id text, p_items jsonb)
RETURNS boolean
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
SET TimeZone = 'UTC'
SET DateStyle = 'ISO, MDY'
SET IntervalStyle = 'postgres'
SET extra_float_digits = '3'
AS $$
    WITH supplied AS
    (
        SELECT (source.value ->> 'rank')::smallint AS item_rank,
               source.value ->> 'kind' AS item_kind,
               source.value ->> 'key' AS primary_key,
               (source.value ->> 'sha256')::char(64) AS canonical_sha256,
               (source.value ->> 'archiveable')::boolean AS archiveable
        FROM jsonb_array_elements(p_items) AS source(value)
    ),
    actual AS
    (
        SELECT * FROM appsurface_durable.flow_retention_source_items(p_scope_id, p_flow_instance_id)
    )
    SELECT jsonb_typeof(p_items) = 'array'
       AND NOT EXISTS (SELECT item_rank, item_kind, primary_key, canonical_sha256, archiveable FROM actual EXCEPT SELECT item_rank, item_kind, primary_key, canonical_sha256, archiveable FROM supplied)
       AND NOT EXISTS (SELECT item_rank, item_kind, primary_key, canonical_sha256, archiveable FROM supplied EXCEPT SELECT item_rank, item_kind, primary_key, canonical_sha256, archiveable FROM actual)
       AND (SELECT count(*) FROM actual) = (SELECT count(*) FROM supplied);
$$;

CREATE FUNCTION appsurface_durable.flow_retention_manifest_source_matches(p_scope_id text, p_retention_manifest_id text, p_flow_instance_id text)
RETURNS boolean
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
SET TimeZone = 'UTC'
SET DateStyle = 'ISO, MDY'
SET IntervalStyle = 'postgres'
SET extra_float_digits = '3'
AS $$
    SELECT appsurface_durable.flow_retention_source_is_safe(p_scope_id, p_flow_instance_id)
       AND appsurface_durable.flow_retention_source_matches_items(
            p_scope_id,
            p_flow_instance_id,
            COALESCE(
                (
                    SELECT jsonb_agg(
                        jsonb_build_object(
                            'rank', item.item_rank,
                            'kind', item.item_kind,
                            'key', item.primary_key,
                            'sha256', item.canonical_sha256,
                            'archiveable', item.archiveable)
                        ORDER BY item.ordinal)
                    FROM appsurface_durable.flow_retention_manifest_item AS item
                    WHERE item.scope_id = p_scope_id AND item.retention_manifest_id = p_retention_manifest_id
                ),
                '[]'::jsonb));
$$;

CREATE FUNCTION appsurface_durable.flow_retention_lock_source(p_scope_id text, p_flow_instance_id text)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
BEGIN
    PERFORM 1 FROM appsurface_durable.scope WHERE scope_id = p_scope_id FOR UPDATE;
    PERFORM 1 FROM appsurface_durable.flow_instance
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    IF NOT FOUND THEN
        RETURN false;
    END IF;

    PERFORM 1
    FROM appsurface_durable.flow_wait AS wait
    JOIN appsurface_durable.work AS work
      ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
    WHERE wait.scope_id = p_scope_id AND wait.flow_instance_id = p_flow_instance_id
      AND wait.child_work_id IS NOT NULL
    FOR UPDATE OF wait, work;
    PERFORM 1 FROM appsurface_durable.flow_command
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    PERFORM 1 FROM appsurface_durable.flow_history
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    PERFORM 1 FROM appsurface_durable.flow_wait
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    PERFORM 1 FROM appsurface_durable.flow_timer
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    PERFORM 1 FROM appsurface_durable.flow_dispatch
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    PERFORM 1 FROM appsurface_durable.flow_trace_context
    WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id FOR UPDATE;
    RETURN true;
END;
$$;

CREATE FUNCTION appsurface_durable.create_flow_retention_manifest(
    p_scope_id text,
    p_retention_manifest_id text,
    p_flow_instance_id text,
    p_closure_schema text,
    p_closure_sha256 char(64),
    p_source_watermark_schema text,
    p_source_watermark_sha256 char(64),
    p_closure_item_count integer,
    p_archive_byte_count bigint,
    p_items jsonb,
    p_command_id text,
    p_fingerprint_schema text,
    p_fingerprint_sha256 char(64))
RETURNS TABLE(outcome text, lifecycle_state text, lifecycle_sequence bigint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
DECLARE
    existing_command record;
BEGIN
    IF NOT appsurface_durable.flow_retention_scope_is_current(p_scope_id) THEN
        RETURN QUERY SELECT 'scope_rejected'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(p_scope_id || ':' || p_command_id, 7331));
    SELECT fingerprint_schema, fingerprint_sha256, resulting_state, resulting_lifecycle_sequence
    INTO existing_command
    FROM appsurface_durable.flow_retention_command
    WHERE scope_id = p_scope_id AND command_id = p_command_id;
    IF FOUND THEN
        IF existing_command.fingerprint_schema <> p_fingerprint_schema
            OR existing_command.fingerprint_sha256 <> p_fingerprint_sha256 THEN
            RETURN QUERY SELECT 'command_conflict'::text, NULL::text, NULL::bigint;
        ELSE
            RETURN QUERY SELECT 'duplicate'::text, existing_command.resulting_state, existing_command.resulting_lifecycle_sequence;
        END IF;
        RETURN;
    END IF;

    IF NOT appsurface_durable.flow_retention_lock_source(p_scope_id, p_flow_instance_id)
        OR NOT appsurface_durable.flow_retention_source_is_safe(p_scope_id, p_flow_instance_id) THEN
        RETURN QUERY SELECT 'source_rejected'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    IF EXISTS
    (
        SELECT 1 FROM appsurface_durable.flow_retention_manifest
        WHERE scope_id = p_scope_id AND flow_instance_id = p_flow_instance_id
    ) THEN
        RETURN QUERY SELECT 'lifecycle_rejected'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    IF jsonb_typeof(p_items) <> 'array'
        OR jsonb_array_length(p_items) <> p_closure_item_count
        OR NOT appsurface_durable.flow_retention_source_matches_items(p_scope_id, p_flow_instance_id, p_items) THEN
        RETURN QUERY SELECT 'source_rejected'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    INSERT INTO appsurface_durable.flow_retention_manifest
        (scope_id, retention_manifest_id, flow_instance_id, closure_schema, closure_sha256,
         source_watermark_schema, source_watermark_sha256, closure_item_count, archive_byte_count)
    VALUES
        (p_scope_id, p_retention_manifest_id, p_flow_instance_id, p_closure_schema, p_closure_sha256,
         p_source_watermark_schema, p_source_watermark_sha256, p_closure_item_count, p_archive_byte_count);
    INSERT INTO appsurface_durable.flow_retention_manifest_item
        (scope_id, retention_manifest_id, ordinal, item_rank, item_kind, primary_key, canonical_sha256, archiveable)
    SELECT p_scope_id, p_retention_manifest_id, source.ordinal - 1,
           (source.value ->> 'rank')::smallint, source.value ->> 'kind', source.value ->> 'key',
           (source.value ->> 'sha256')::char(64), (source.value ->> 'archiveable')::boolean
    FROM jsonb_array_elements(p_items) WITH ORDINALITY AS source(value, ordinal)
    ORDER BY source.ordinal;
    INSERT INTO appsurface_durable.flow_retention_manifest_summary
        (scope_id, retention_manifest_id, lifecycle_state, lifecycle_sequence)
    VALUES (p_scope_id, p_retention_manifest_id, 'frozen', 1);
    INSERT INTO appsurface_durable.flow_retention_manifest_event
        (scope_id, retention_manifest_id, lifecycle_sequence, event_type, command_id)
    VALUES (p_scope_id, p_retention_manifest_id, 1, 'manifest_created', p_command_id);
    INSERT INTO appsurface_durable.flow_retention_command
        (scope_id, command_id, retention_manifest_id, command_type, fingerprint_schema, fingerprint_sha256,
         outcome, resulting_state, resulting_lifecycle_sequence)
    VALUES
        (p_scope_id, p_command_id, p_retention_manifest_id, 'manifest_create', p_fingerprint_schema, p_fingerprint_sha256,
         'created', 'frozen', 1);
    RETURN QUERY SELECT 'created'::text, 'frozen'::text, 1::bigint;
END;
$$;

CREATE FUNCTION appsurface_durable.apply_flow_retention_lifecycle(
    p_scope_id text,
    p_retention_manifest_id text,
    p_operation text,
    p_command_id text,
    p_fingerprint_schema text,
    p_fingerprint_sha256 char(64),
    p_actor_id text,
    p_reason_code text,
    p_expected_lifecycle_sequence bigint,
    p_receipt_id text,
    p_receipt_package_schema text,
    p_receipt_package_sha256 char(64),
    p_receipt_closure_schema text,
    p_receipt_closure_sha256 char(64),
    p_receipt_record_count integer,
    p_place_hold boolean)
RETURNS TABLE(outcome text, lifecycle_state text, lifecycle_sequence bigint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
DECLARE
    existing_command record;
    manifest_value record;
    next_sequence bigint;
    purged_sequence bigint;
BEGIN
    IF NOT appsurface_durable.flow_retention_scope_is_current(p_scope_id) THEN
        RETURN QUERY SELECT 'scope_rejected'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    IF p_operation IS NULL OR p_operation NOT IN ('archive_receipt', 'verify', 'hold', 'purge') THEN
        RETURN QUERY SELECT 'lifecycle_rejected'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(p_scope_id || ':' || p_command_id, 7331));
    SELECT command_value.fingerprint_schema, command_value.fingerprint_sha256, command_value.outcome,
           command_value.resulting_state, command_value.resulting_lifecycle_sequence
    INTO existing_command
    FROM appsurface_durable.flow_retention_command AS command_value
    WHERE command_value.scope_id = p_scope_id AND command_value.command_id = p_command_id;
    IF FOUND THEN
        IF existing_command.fingerprint_schema <> p_fingerprint_schema
            OR existing_command.fingerprint_sha256 <> p_fingerprint_sha256 THEN
            RETURN QUERY SELECT 'command_conflict'::text, NULL::text, NULL::bigint;
        ELSE
            RETURN QUERY SELECT CASE WHEN existing_command.outcome = 'already_purged' THEN 'already_purged' ELSE 'duplicate' END,
                existing_command.resulting_state, existing_command.resulting_lifecycle_sequence;
        END IF;
        RETURN;
    END IF;

    SELECT manifest.flow_instance_id, manifest.closure_schema, manifest.closure_sha256, manifest.closure_item_count,
           summary.lifecycle_state, summary.lifecycle_sequence
    INTO manifest_value
    FROM appsurface_durable.flow_retention_manifest AS manifest
    JOIN appsurface_durable.flow_retention_manifest_summary AS summary
      ON summary.scope_id = manifest.scope_id AND summary.retention_manifest_id = manifest.retention_manifest_id
    WHERE manifest.scope_id = p_scope_id AND manifest.retention_manifest_id = p_retention_manifest_id
    FOR UPDATE OF summary;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'manifest_not_found'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    IF manifest_value.lifecycle_sequence <> p_expected_lifecycle_sequence THEN
        RETURN QUERY SELECT 'lifecycle_conflict'::text, manifest_value.lifecycle_state, manifest_value.lifecycle_sequence;
        RETURN;
    END IF;

    IF manifest_value.lifecycle_state = 'purged' THEN
        INSERT INTO appsurface_durable.flow_retention_command
            (scope_id, command_id, retention_manifest_id, command_type, fingerprint_schema, fingerprint_sha256,
             outcome, resulting_state, resulting_lifecycle_sequence)
        VALUES
            (p_scope_id, p_command_id, p_retention_manifest_id, p_operation, p_fingerprint_schema, p_fingerprint_sha256,
             'already_purged', 'purged', manifest_value.lifecycle_sequence);
        RETURN QUERY SELECT 'already_purged'::text, 'purged'::text, manifest_value.lifecycle_sequence;
        RETURN;
    END IF;

    IF p_operation = 'archive_receipt' THEN
        IF manifest_value.lifecycle_state <> 'frozen'
            OR p_receipt_id IS NULL
            OR p_receipt_package_schema IS NULL
            OR p_receipt_package_sha256 IS NULL
            OR p_receipt_closure_schema IS NULL
            OR p_receipt_closure_sha256 IS NULL
            OR p_receipt_record_count IS NULL
            OR p_receipt_closure_schema <> manifest_value.closure_schema
            OR p_receipt_closure_sha256 <> manifest_value.closure_sha256
            OR p_receipt_record_count <> manifest_value.closure_item_count THEN
            RETURN QUERY SELECT 'lifecycle_rejected'::text, manifest_value.lifecycle_state, manifest_value.lifecycle_sequence;
            RETURN;
        END IF;
        next_sequence := manifest_value.lifecycle_sequence + 1;
        UPDATE appsurface_durable.flow_retention_manifest_summary
        SET lifecycle_state = 'archive_receipt_recorded', lifecycle_sequence = next_sequence,
            receipt_id = p_receipt_id, receipt_package_schema = p_receipt_package_schema,
            receipt_package_sha256 = p_receipt_package_sha256, receipt_closure_schema = p_receipt_closure_schema,
            receipt_closure_sha256 = p_receipt_closure_sha256, receipt_record_count = p_receipt_record_count,
            updated_at = clock_timestamp()
        WHERE scope_id = p_scope_id AND retention_manifest_id = p_retention_manifest_id;
    ELSIF p_operation = 'verify' THEN
        IF manifest_value.lifecycle_state <> 'archive_receipt_recorded'
            OR NOT appsurface_durable.flow_retention_lock_source(p_scope_id, manifest_value.flow_instance_id)
            OR NOT appsurface_durable.flow_retention_manifest_source_matches(p_scope_id, p_retention_manifest_id, manifest_value.flow_instance_id) THEN
            RETURN QUERY SELECT 'lifecycle_rejected'::text, manifest_value.lifecycle_state, manifest_value.lifecycle_sequence;
            RETURN;
        END IF;
        next_sequence := manifest_value.lifecycle_sequence + 1;
        UPDATE appsurface_durable.flow_retention_manifest_summary
        SET lifecycle_state = 'verified', lifecycle_sequence = next_sequence, updated_at = clock_timestamp()
        WHERE scope_id = p_scope_id AND retention_manifest_id = p_retention_manifest_id;
    ELSIF p_operation = 'hold' THEN
        IF p_place_hold IS NULL
            OR (p_place_hold AND manifest_value.lifecycle_state <> 'verified')
            OR (NOT p_place_hold AND manifest_value.lifecycle_state <> 'held') THEN
            RETURN QUERY SELECT 'lifecycle_rejected'::text, manifest_value.lifecycle_state, manifest_value.lifecycle_sequence;
            RETURN;
        END IF;
        next_sequence := manifest_value.lifecycle_sequence + 1;
        UPDATE appsurface_durable.flow_retention_manifest_summary
        SET lifecycle_state = CASE WHEN p_place_hold THEN 'held' ELSE 'verified' END,
            lifecycle_sequence = next_sequence, updated_at = clock_timestamp()
        WHERE scope_id = p_scope_id AND retention_manifest_id = p_retention_manifest_id;
    ELSIF p_operation = 'purge' THEN
        IF manifest_value.lifecycle_state <> 'verified'
            OR NOT appsurface_durable.flow_retention_lock_source(p_scope_id, manifest_value.flow_instance_id)
            OR NOT appsurface_durable.flow_retention_manifest_source_matches(p_scope_id, p_retention_manifest_id, manifest_value.flow_instance_id) THEN
            RETURN QUERY SELECT 'lifecycle_rejected'::text, manifest_value.lifecycle_state, manifest_value.lifecycle_sequence;
            RETURN;
        END IF;
        next_sequence := manifest_value.lifecycle_sequence + 1;
        purged_sequence := next_sequence + 1;
        INSERT INTO appsurface_durable.flow_retention_manifest_event
            (scope_id, retention_manifest_id, lifecycle_sequence, event_type, command_id, actor_id, reason_code)
        VALUES (p_scope_id, p_retention_manifest_id, next_sequence, 'purge_authorized', p_command_id, p_actor_id, p_reason_code);
        DELETE FROM appsurface_durable.flow_dispatch
        WHERE scope_id = p_scope_id AND flow_instance_id = manifest_value.flow_instance_id AND state = 'terminal';
        DELETE FROM appsurface_durable.flow_timer
        WHERE scope_id = p_scope_id AND flow_instance_id = manifest_value.flow_instance_id AND state <> 'scheduled';
        DELETE FROM appsurface_durable.flow_wait
        WHERE scope_id = p_scope_id AND flow_instance_id = manifest_value.flow_instance_id AND state NOT IN ('active', 'suspended');
        DELETE FROM appsurface_durable.flow_history
        WHERE scope_id = p_scope_id AND flow_instance_id = manifest_value.flow_instance_id;
        UPDATE appsurface_durable.flow_instance
        SET context_contract_id = NULL, context_schema_version = NULL, context_codec_id = NULL, context_payload = NULL,
            context_sha256 = NULL, context_classification = NULL, context_retention = NULL,
            resume_event_name = NULL, resume_event_is_timeout = false, resume_event_contract_id = NULL,
            resume_event_schema_version = NULL, resume_event_codec_id = NULL, resume_event_payload = NULL,
            resume_event_sha256 = NULL, resume_event_classification = NULL, resume_event_retention = NULL,
            activity_callsite_id = NULL, activity_result_contract_id = NULL, activity_result_schema_version = NULL,
            activity_result_codec_id = NULL, activity_result_payload = NULL, activity_result_sha256 = NULL,
            activity_result_classification = NULL, activity_result_retention = NULL,
            retention_manifest_id = p_retention_manifest_id, retention_lifecycle_sequence = purged_sequence,
            updated_at = clock_timestamp()
        WHERE scope_id = p_scope_id AND flow_instance_id = manifest_value.flow_instance_id;
        UPDATE appsurface_durable.flow_retention_manifest_summary
        SET lifecycle_state = 'purged', lifecycle_sequence = purged_sequence, updated_at = clock_timestamp()
        WHERE scope_id = p_scope_id AND retention_manifest_id = p_retention_manifest_id;
        INSERT INTO appsurface_durable.flow_retention_manifest_event
            (scope_id, retention_manifest_id, lifecycle_sequence, event_type, command_id, actor_id, reason_code)
        VALUES (p_scope_id, p_retention_manifest_id, purged_sequence, 'purged', p_command_id, p_actor_id, p_reason_code);
        next_sequence := purged_sequence;
    ELSE
        RETURN QUERY SELECT 'lifecycle_rejected'::text, manifest_value.lifecycle_state, manifest_value.lifecycle_sequence;
        RETURN;
    END IF;

    INSERT INTO appsurface_durable.flow_retention_manifest_event
        (scope_id, retention_manifest_id, lifecycle_sequence, event_type, command_id, actor_id, reason_code)
    SELECT p_scope_id, p_retention_manifest_id, next_sequence,
           CASE p_operation WHEN 'archive_receipt' THEN 'archive_receipt_recorded' WHEN 'verify' THEN 'source_correspondence_verified'
                WHEN 'hold' THEN CASE WHEN p_place_hold THEN 'hold_placed' ELSE 'hold_released' END END,
           p_command_id, p_actor_id, p_reason_code
    WHERE p_operation <> 'purge';
    INSERT INTO appsurface_durable.flow_retention_command
        (scope_id, command_id, retention_manifest_id, command_type, fingerprint_schema, fingerprint_sha256,
         outcome, resulting_state, resulting_lifecycle_sequence)
    SELECT p_scope_id, p_command_id, p_retention_manifest_id, p_operation, p_fingerprint_schema, p_fingerprint_sha256,
           'applied', CASE p_operation WHEN 'archive_receipt' THEN 'archive_receipt_recorded' WHEN 'verify' THEN 'verified'
                WHEN 'hold' THEN CASE WHEN p_place_hold THEN 'held' ELSE 'verified' END WHEN 'purge' THEN 'purged' END, next_sequence;
    RETURN QUERY SELECT 'applied'::text,
        CASE p_operation WHEN 'archive_receipt' THEN 'archive_receipt_recorded' WHEN 'verify' THEN 'verified'
             WHEN 'hold' THEN CASE WHEN p_place_hold THEN 'held' ELSE 'verified' END WHEN 'purge' THEN 'purged' END,
        next_sequence;
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.flow_retention_scope_is_current(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.flow_retention_source_is_safe(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.flow_retention_source_items(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.flow_retention_source_matches_items(text, text, jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.flow_retention_manifest_source_matches(text, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.flow_retention_lock_source(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.create_flow_retention_manifest(text, text, text, text, char(64), text, char(64), integer, bigint, jsonb, text, text, char(64)) FROM PUBLIC;
REVOKE ALL ON FUNCTION appsurface_durable.apply_flow_retention_lifecycle(text, text, text, text, text, char(64), text, text, bigint, text, text, char(64), text, char(64), integer, boolean) FROM PUBLIC;

ALTER TABLE appsurface_durable.flow_retention_manifest ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_retention_manifest FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_retention_manifest_scope_isolation ON appsurface_durable.flow_retention_manifest
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_retention_manifest_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_retention_manifest_item FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_retention_manifest_item_scope_isolation ON appsurface_durable.flow_retention_manifest_item
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_retention_manifest_summary ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_retention_manifest_summary FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_retention_manifest_summary_scope_isolation ON appsurface_durable.flow_retention_manifest_summary
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_retention_manifest_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_retention_manifest_event FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_retention_manifest_event_scope_isolation ON appsurface_durable.flow_retention_manifest_event
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.flow_retention_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_retention_command FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_retention_command_scope_isolation ON appsurface_durable.flow_retention_command
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
