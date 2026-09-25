-- Retention is forward-only and transactional; deploy during a maintenance window because index creation
-- takes a table lock. The bounded lock timeout keeps an unprepared migration from blocking writers indefinitely.
SET LOCAL lock_timeout = '5s';

CREATE INDEX ix_runtime_heartbeat_retention
    ON appsurface_durable.runtime_heartbeat (last_heartbeat_at, worker_id);

-- SECURITY DEFINER does not bypass FORCE ROW LEVEL SECURITY. Give only the migration/function owner
-- table-wide visibility so pruning can select and delete candidates; the runtime retains its separate policy.
DO $$
BEGIN
    EXECUTE format(
        'CREATE POLICY runtime_heartbeat_migration_owner ON appsurface_durable.runtime_heartbeat FOR ALL TO %I USING (true) WITH CHECK (true)',
        CURRENT_USER);
END;
$$;

CREATE FUNCTION appsurface_durable.prune_runtime_heartbeats(
    p_retention interval,
    p_maximum_rows integer,
    p_current_worker_id text,
    p_current_worker_instance_id uuid)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
DECLARE
    v_cutoff timestamp with time zone;
    v_deleted integer;
BEGIN
    IF p_retention IS NULL
       OR p_retention < interval '24 hours'
       OR p_retention > interval '3650 days' THEN
        RAISE EXCEPTION 'p_retention must be between 24 hours and 3650 days.'
            USING ERRCODE = '22023';
    END IF;
    IF p_maximum_rows IS NULL OR p_maximum_rows NOT BETWEEN 1 AND 5000 THEN
        RAISE EXCEPTION 'p_maximum_rows must be between 1 and 5000.'
            USING ERRCODE = '22023';
    END IF;
    IF p_current_worker_id IS NULL
       OR length(p_current_worker_id) NOT BETWEEN 1 AND 200
       OR p_current_worker_id !~ '^[A-Za-z0-9._:-]+$' THEN
        RAISE EXCEPTION 'p_current_worker_id is invalid.'
            USING ERRCODE = '22023';
    END IF;
    IF p_current_worker_instance_id IS NULL
       OR p_current_worker_instance_id =
          '00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'p_current_worker_instance_id is invalid.'
            USING ERRCODE = '22023';
    END IF;

    -- Two-int advisory namespace "ASDU"/"RHBR", separate from the schema migration and per-scope locks.
    IF NOT pg_try_advisory_xact_lock(X'41534455'::bit(32)::integer, X'52484252'::bit(32)::integer) THEN
        RETURN 0;
    END IF;

    v_cutoff := clock_timestamp() - p_retention;
    WITH candidates AS
    (
        SELECT heartbeat.worker_id, heartbeat.worker_instance_id
        FROM appsurface_durable.runtime_heartbeat AS heartbeat
        WHERE heartbeat.last_heartbeat_at < v_cutoff
          AND NOT
          (
              heartbeat.worker_id = p_current_worker_id
              AND heartbeat.worker_instance_id = p_current_worker_instance_id
          )
        ORDER BY heartbeat.last_heartbeat_at, heartbeat.worker_id
        FOR UPDATE SKIP LOCKED
        LIMIT p_maximum_rows
    )
    DELETE FROM appsurface_durable.runtime_heartbeat AS heartbeat
    USING candidates
    WHERE heartbeat.worker_id = candidates.worker_id
      AND heartbeat.worker_instance_id = candidates.worker_instance_id
      AND heartbeat.last_heartbeat_at < v_cutoff;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    RETURN v_deleted;
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.prune_runtime_heartbeats(
    interval, integer, text, uuid) FROM PUBLIC;
