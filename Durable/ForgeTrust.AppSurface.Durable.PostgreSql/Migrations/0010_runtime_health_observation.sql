-- Durable runtime health observation correction. This migration is forward-only.
-- The function signature remains unchanged so schema-9 readers remain callable after migration 10.

DO $$
DECLARE
    function_owner name;
BEGIN
    SELECT owner_role.rolname
    INTO function_owner
    FROM pg_catalog.pg_proc AS routine
    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
    JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = routine.proowner
    WHERE namespace.nspname = 'appsurface_durable'
      AND routine.oid =
          pg_catalog.to_regprocedure('appsurface_durable.runtime_due_dispatch_health(integer)');

    IF function_owner IS NULL THEN
        RAISE EXCEPTION
            'runtime_due_dispatch_health(integer) ownership preflight failed: the schema-9 function is missing.'
            USING ERRCODE = '42704',
                  HINT = 'Restore schema 9 or run the canonical PostgreSQL role recipe before retrying migration 0010.';
    END IF;

    IF function_owner <> CURRENT_USER THEN
        RAISE EXCEPTION
            'runtime_due_dispatch_health(integer) ownership preflight failed: current migration role does not own the function.'
            USING ERRCODE = '42501',
                  HINT = 'Run Durable/configure-postgresql-roles.sql with the configured migration owner, then retry migration 0010.';
    END IF;
END;
$$;

-- A separate lease-expiry key is required for the reclaimable Schedule branch. The partial predicate keeps
-- terminal, suspended, and available rows out of this index while retaining the existing due index for available rows.
-- The package migration protocol is transactional, so CREATE INDEX CONCURRENTLY is unavailable. Fail immediately
-- when a writer still owns a conflicting lock, and bound the index build so an incorrectly prepared maintenance
-- window cannot block Schedule writers indefinitely.
SET LOCAL statement_timeout = '5min';
LOCK TABLE appsurface_durable.schedule_dispatch IN SHARE MODE NOWAIT;
CREATE INDEX ix_schedule_dispatch_lease_expiry_due
    ON appsurface_durable.schedule_dispatch (lease_expires_at)
    WHERE state = 'leased';

CREATE OR REPLACE FUNCTION appsurface_durable.runtime_due_dispatch_health(p_surfaces integer)
RETURNS TABLE
(
    due_count bigint,
    oldest_due_at timestamp with time zone
)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, appsurface_durable, pg_temp
AS $$
DECLARE
    observed_at_utc timestamp with time zone := pg_catalog.statement_timestamp();
BEGIN
    IF p_surfaces IS NULL OR p_surfaces NOT BETWEEN 1 AND 7 THEN
        RAISE EXCEPTION 'p_surfaces must select one or more known runtime surfaces.'
            USING ERRCODE = '22023';
    END IF;

    RETURN QUERY
    SELECT count(*), min(due.due_at)
    FROM
    (
        SELECT dispatch.due_at
        FROM appsurface_durable.dispatch AS dispatch
        WHERE (p_surfaces & 1) <> 0
          AND dispatch.aggregate_kind = 'work'
          AND dispatch.state IN ('available', 'leased')
          AND dispatch.due_at <= observed_at_utc
        UNION ALL
        SELECT dispatch.due_at
        FROM appsurface_durable.flow_dispatch AS dispatch
        WHERE (p_surfaces & 2) <> 0
          AND dispatch.state IN ('available', 'leased')
          AND dispatch.due_at <= observed_at_utc
        UNION ALL
        SELECT dispatch.due_at
        FROM appsurface_durable.schedule_dispatch AS dispatch
        WHERE (p_surfaces & 4) <> 0
          AND dispatch.state = 'available'
          AND dispatch.due_at <= observed_at_utc
        UNION ALL
        SELECT dispatch.lease_expires_at
        FROM appsurface_durable.schedule_dispatch AS dispatch
        WHERE (p_surfaces & 4) <> 0
          AND dispatch.state = 'leased'
          AND dispatch.lease_expires_at <= observed_at_utc
    ) AS due;
END;
$$;

-- CREATE OR REPLACE preserves the function owner, but it does not repair an ACL widened by an operator or
-- an earlier deployment. Keep the aggregate health boundary explicit and let the role recipe grant runtime only.
REVOKE ALL ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM PUBLIC;
