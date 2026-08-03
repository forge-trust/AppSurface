-- Runtime operation state is intentionally payload-free and unscoped. The runtime role is the only service
-- principal granted access by configure-postgresql-roles.sql; dispatcher and public clients receive no access.
CREATE TABLE appsurface_durable.runtime_heartbeat
(
    worker_id text PRIMARY KEY CHECK (length(worker_id) BETWEEN 1 AND 200),
    worker_instance_id uuid NOT NULL,
    runtime_epoch uuid NOT NULL,
    hosted_surfaces smallint NOT NULL CHECK (hosted_surfaces BETWEEN 1 AND 7),
    started_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    last_heartbeat_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    last_successful_sweep_at timestamp with time zone,
    draining boolean NOT NULL DEFAULT false,
    pass_active boolean NOT NULL DEFAULT false,
    pass_started_at timestamp with time zone,
    last_discovered integer CHECK (last_discovered IS NULL OR last_discovered >= 0),
    last_claimed integer CHECK (last_claimed IS NULL OR last_claimed >= 0),
    last_processed integer CHECK (last_processed IS NULL OR last_processed >= 0),
    last_deferred integer CHECK (last_deferred IS NULL OR last_deferred >= 0),
    last_failed integer CHECK (last_failed IS NULL OR last_failed >= 0),
    last_pass_elapsed_ms double precision CHECK (last_pass_elapsed_ms IS NULL OR last_pass_elapsed_ms >= 0),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CHECK ((pass_active AND pass_started_at IS NOT NULL) OR (NOT pass_active AND pass_started_at IS NULL))
);

CREATE INDEX ix_runtime_heartbeat_epoch_liveness
    ON appsurface_durable.runtime_heartbeat (runtime_epoch, last_heartbeat_at, worker_id);

ALTER TABLE appsurface_durable.runtime_heartbeat ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.runtime_heartbeat FORCE ROW LEVEL SECURITY;
CREATE POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat
    USING (true)
    WITH CHECK (true);

-- The runtime health snapshot needs selected-surface due lag without giving the scoped runtime credential a global
-- Schedule-dispatch read. The function exposes aggregate time/count data only and remains owned by the migration role.
CREATE FUNCTION appsurface_durable.runtime_due_dispatch_health(p_surfaces integer)
RETURNS TABLE
(
    due_count bigint,
    oldest_due_at timestamp with time zone
)
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable
AS $$
    SELECT count(*), min(due_at)
    FROM
    (
        SELECT due_at
        FROM appsurface_durable.dispatch
        WHERE (p_surfaces & 1) <> 0
          AND aggregate_kind = 'work'
          AND state IN ('available', 'leased')
          AND due_at <= clock_timestamp()
        UNION ALL
        SELECT due_at
        FROM appsurface_durable.flow_dispatch
        WHERE (p_surfaces & 2) <> 0
          AND state IN ('available', 'leased')
          AND due_at <= clock_timestamp()
        UNION ALL
        SELECT due_at
        FROM appsurface_durable.schedule_dispatch
        WHERE (p_surfaces & 4) <> 0
          AND state IN ('available', 'leased')
          AND due_at <= clock_timestamp()
    ) AS due;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM PUBLIC;
