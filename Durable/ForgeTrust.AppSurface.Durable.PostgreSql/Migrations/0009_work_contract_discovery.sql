CREATE POLICY work_contract_discovery_owner ON appsurface_durable.work
    FOR SELECT
    TO CURRENT_USER
    USING (true);

-- This package migration is checksum-verified and transaction-bound. Plain CREATE INDEX blocks concurrent Work writes,
-- so apply it in a reviewed maintenance window after draining runtime and Work-writer hosts; do not rewrite generated SQL
-- to CREATE INDEX CONCURRENTLY because that command cannot run inside the package migration transaction.
CREATE INDEX ix_work_contract_dispatch_lookup
    ON appsurface_durable.work
    (work_name COLLATE "C", work_version COLLATE "C", scope_id, work_id);

CREATE FUNCTION appsurface_durable.discover_work_dispatch(
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
      AND dispatch.due_at <= clock_timestamp()
    ORDER BY dispatch.due_at, dispatch.priority DESC, dispatch.dispatch_id
    LIMIT p_maximum_candidates;
END;
$$;

REVOKE ALL ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer) FROM PUBLIC;
