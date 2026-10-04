-- Opt-in Work timing fields and the payload-free expired-deadline discovery hint.
-- Apply during the coordinated #765 stop/drain window; no automatic runtime DDL is performed.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE appsurface_durable.work
    ADD COLUMN execution_policy_schema text,
    ADD COLUMN attempt_plan_version text,
    ADD COLUMN attempt_plan_offsets bigint[],
    ADD COLUMN maximum_circuit_microseconds bigint,
    ADD COLUMN execution_not_after timestamp with time zone,
    ADD COLUMN execution_deadline_reached_at timestamp with time zone,
    ADD COLUMN execution_admission_closed_at timestamp with time zone,
    ADD COLUMN execution_admission_closed_reason text;

CREATE FUNCTION appsurface_durable.attempt_plan_offsets_are_valid(p_offsets bigint[])
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
SECURITY INVOKER
SET search_path = pg_catalog
AS $$
DECLARE
    v_index integer;
BEGIN
    IF p_offsets IS NULL
       OR pg_catalog.array_ndims(p_offsets) <> 1
       OR pg_catalog.array_lower(p_offsets, 1) <> 1
       OR pg_catalog.cardinality(p_offsets) NOT BETWEEN 1 AND 256
       OR p_offsets[1] IS DISTINCT FROM 0 THEN
        RETURN false;
    END IF;

    FOR v_index IN 1..pg_catalog.array_upper(p_offsets, 1) LOOP
        IF p_offsets[v_index] IS NULL
           OR (v_index > 1 AND p_offsets[v_index] <= p_offsets[v_index - 1]) THEN
            RETURN false;
        END IF;
    END LOOP;

    RETURN true;
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.attempt_plan_offsets_are_valid(bigint[]) FROM PUBLIC;

ALTER TABLE appsurface_durable.work
    ADD CONSTRAINT ck_work_execution_policy_shape
    CHECK
    (
        (
            execution_policy_schema IS NULL
            AND attempt_plan_version IS NULL
            AND attempt_plan_offsets IS NULL
            AND maximum_circuit_microseconds IS NULL
            AND execution_not_after IS NULL
            AND execution_deadline_reached_at IS NULL
            AND execution_admission_closed_at IS NULL
            AND execution_admission_closed_reason IS NULL
        )
        OR
        (
            execution_policy_schema IS NOT NULL
            AND
            execution_policy_schema = 'work-execution-v1'
            AND (attempt_plan_version IS NOT NULL OR execution_not_after IS NOT NULL)
            AND (execution_deadline_reached_at IS NULL
                OR (execution_not_after IS NOT NULL AND execution_deadline_reached_at >= execution_not_after))
            AND
            (
                (
                    attempt_plan_version IS NULL
                    AND attempt_plan_offsets IS NULL
                    AND maximum_circuit_microseconds IS NULL
                )
                OR
                (
                    attempt_plan_version IS NOT NULL
                    AND length(attempt_plan_version) BETWEEN 1 AND 100
                    AND attempt_plan_version ~ '^[A-Za-z0-9._:-]+$'
                    AND attempt_plan_offsets IS NOT NULL
                    AND appsurface_durable.attempt_plan_offsets_are_valid(attempt_plan_offsets)
                    AND maximum_circuit_microseconds IS NOT NULL
                    AND maximum_circuit_microseconds > 0
                    AND maximum_circuit_microseconds <= 922337203685477580
                    AND attempt_plan_offsets[cardinality(attempt_plan_offsets)] < maximum_circuit_microseconds
                    AND maximum_attempts = cardinality(attempt_plan_offsets)
                    AND maximum_elapsed >=
                        (maximum_circuit_microseconds::text || ' microseconds')::interval
                )
            )
            AND
            (
                (execution_admission_closed_at IS NULL AND execution_admission_closed_reason IS NULL)
                OR
                (
                    execution_admission_closed_at IS NOT NULL
                    AND execution_admission_closed_reason IS NOT NULL
                    AND length(execution_admission_closed_reason) BETWEEN 1 AND 120
                    AND execution_admission_closed_reason ~ '^[a-z0-9._:-]+$'
                )
            )
        )
    );

ALTER TABLE appsurface_durable.effect_permit
    ADD COLUMN invocation_admitted_at timestamp with time zone;

ALTER TABLE appsurface_durable.work_history
    ADD COLUMN observation_retention_policy_id text,
    ADD CONSTRAINT ck_work_history_observation_retention_policy_id
        CHECK
        (
            observation_retention_policy_id IS NULL
            OR
            (
                length(observation_retention_policy_id) BETWEEN 1 AND 128
                AND observation_payload IS NOT NULL
            )
        );

ALTER TABLE appsurface_durable.dispatch
    ADD COLUMN execution_discovery_at timestamp with time zone;

-- The key expression and ordering match both Work discovery paths and the scoped
-- contract-selection function below. INCLUDE columns keep the dispatcher payload-free.
CREATE INDEX ix_dispatch_execution_discovery
    ON appsurface_durable.dispatch
    ((COALESCE(execution_discovery_at, due_at)), priority DESC, dispatch_id)
    INCLUDE (scope_id, aggregate_id, due_at, expected_revision)
    WHERE aggregate_kind = 'work' AND state IN ('available', 'leased');

CREATE FUNCTION appsurface_durable.work_execution_now()
RETURNS timestamp with time zone
LANGUAGE sql
VOLATILE
SECURITY INVOKER
SET search_path = pg_catalog
AS $$
    SELECT pg_catalog.clock_timestamp();
$$;

REVOKE ALL ON FUNCTION appsurface_durable.work_execution_now() FROM PUBLIC;

-- Preserve the established bounded, selected-contract, payload-free result contract
-- while making an opt-in expiry hint discoverable before its execution due time.
CREATE OR REPLACE FUNCTION appsurface_durable.discover_work_dispatch(
    p_work_names text[],
    p_work_versions text[],
    p_maximum_candidates integer)
RETURNS TABLE
(
    dispatch_id uuid,
    scope_id text,
    aggregate_id text,
    due_at timestamp with time zone,
    expected_revision bigint,
    priority smallint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
BEGIN
    IF p_work_names IS NULL
       OR p_work_versions IS NULL
       OR cardinality(p_work_names) <> cardinality(p_work_versions)
       OR cardinality(p_work_names) NOT BETWEEN 1 AND 10000
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Work discovery requires matching non-empty name and version arrays containing at most 10000 pairs.';
    END IF;

    IF p_maximum_candidates IS NULL
       OR p_maximum_candidates NOT BETWEEN 1 AND 1000
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Work discovery maximum candidates must be between 1 and 1000.';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM unnest(p_work_names, p_work_versions) AS requested(work_name, work_version)
        WHERE requested.work_name IS NULL
           OR requested.work_version IS NULL
           OR btrim(requested.work_name) = ''
           OR btrim(requested.work_version) = ''
           OR length(requested.work_name) > 200
           OR length(requested.work_version) > 100
           OR requested.work_name ~ '[[:cntrl:]]'
           OR requested.work_version ~ '[[:cntrl:]]'
           OR requested.work_name !~ '^[A-Za-z0-9._:-]+$'
           OR requested.work_version !~ '^[A-Za-z0-9._:-]+$'
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Work discovery names and versions must use the Durable identifier rules.';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM unnest(p_work_names, p_work_versions) AS requested(work_name, work_version)
        GROUP BY requested.work_name COLLATE "C", requested.work_version COLLATE "C"
        HAVING count(*) > 1
    )
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Work discovery name and version pairs must be distinct.';
    END IF;

    RETURN QUERY
    WITH requested AS
    (
        SELECT requested.work_name, requested.work_version
        FROM unnest(p_work_names, p_work_versions) AS requested(work_name, work_version)
    )
    SELECT dispatch.dispatch_id,
           dispatch.scope_id,
           dispatch.aggregate_id,
           dispatch.due_at,
           dispatch.expected_revision,
           dispatch.priority
    FROM requested
    JOIN appsurface_durable.work AS work
      ON work.work_name COLLATE "C" = requested.work_name COLLATE "C"
     AND work.work_version COLLATE "C" = requested.work_version COLLATE "C"
    JOIN appsurface_durable.dispatch AS dispatch
      ON dispatch.scope_id = work.scope_id
     AND dispatch.aggregate_kind = 'work'
     AND dispatch.aggregate_id = work.work_id
    WHERE dispatch.state IN ('available', 'leased')
      AND COALESCE(dispatch.execution_discovery_at, dispatch.due_at)
            <= CASE
                WHEN dispatch.execution_discovery_at IS NULL THEN pg_catalog.clock_timestamp()
                ELSE appsurface_durable.work_execution_now()
            END
    ORDER BY COALESCE(dispatch.execution_discovery_at, dispatch.due_at),
             dispatch.priority DESC,
             dispatch.dispatch_id
    LIMIT p_maximum_candidates;
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer) FROM PUBLIC;
