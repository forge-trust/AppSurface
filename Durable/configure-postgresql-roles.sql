\set ON_ERROR_STOP on

-- Required psql variables contain role names as data, never raw SQL identifiers:
--   migration_owner_role, dispatcher_role, runtime_role
SELECT :'migration_owner_role' <> :'dispatcher_role'
   AND :'migration_owner_role' <> :'runtime_role'
   AND :'dispatcher_role' <> :'runtime_role' AS roles_are_distinct \gset
\if :roles_are_distinct
\else
  \echo 'Migration owner, dispatcher, and scoped runtime roles must be distinct.'
  SELECT 1 / 0;
\endif

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required migration owner role does not exist:' :'migration_owner_role'
  SELECT 1 / 0;
\endif

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'dispatcher_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required dispatcher role does not exist:' :'dispatcher_role'
  SELECT 1 / 0;
\endif

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'runtime_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required scoped runtime role does not exist:' :'runtime_role'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_roles AS privileged_role
  WHERE (privileged_role.rolsuper OR privileged_role.rolbypassrls)
    AND
    (
      pg_catalog.pg_has_role(:'dispatcher_role', privileged_role.oid, 'MEMBER')
      OR pg_catalog.pg_has_role(:'runtime_role', privileged_role.oid, 'MEMBER')
    )
) AS service_roles_are_unprivileged \gset
\if :service_roles_are_unprivileged
\else
  \echo 'Dispatcher and scoped runtime roles must not inherit SUPERUSER or BYPASSRLS.'
  SELECT 1 / 0;
\endif

SELECT NOT
(
  pg_catalog.pg_has_role(:'dispatcher_role', :'migration_owner_role', 'MEMBER')
  OR pg_catalog.pg_has_role(:'runtime_role', :'migration_owner_role', 'MEMBER')
  OR pg_catalog.pg_has_role(:'dispatcher_role', :'runtime_role', 'MEMBER')
  OR pg_catalog.pg_has_role(:'runtime_role', :'dispatcher_role', 'MEMBER')
) AS service_roles_are_separated \gset
\if :service_roles_are_separated
\else
  \echo 'Dispatcher and scoped runtime roles must not inherit each other or the migration owner.'
  SELECT 1 / 0;
\endif

SELECT format('ALTER SCHEMA appsurface_durable OWNER TO %I', :'migration_owner_role') \gexec
SELECT format(
    'ALTER %s %I.%I OWNER TO %I',
    CASE object.relkind
      WHEN 'r' THEN 'TABLE'
      WHEN 'p' THEN 'TABLE'
      WHEN 'S' THEN 'SEQUENCE'
      WHEN 'v' THEN 'VIEW'
      WHEN 'm' THEN 'MATERIALIZED VIEW'
      WHEN 'f' THEN 'FOREIGN TABLE'
    END,
    namespace.nspname,
    object.relname,
    :'migration_owner_role')
FROM pg_catalog.pg_class AS object
JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
WHERE namespace.nspname = 'appsurface_durable'
  AND object.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
  AND
  (
    object.relkind <> 'S'
    OR NOT EXISTS
    (
      SELECT 1
      FROM pg_catalog.pg_depend AS dependency
      WHERE dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
        AND dependency.objid = object.oid
        AND dependency.deptype IN ('a', 'i')
    )
  )
ORDER BY CASE WHEN object.relkind = 'S' THEN 2 ELSE 1 END, object.relname \gexec
SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = object.relowner
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
    AND owner_role.rolname <> :'migration_owner_role'
) AS durable_objects_owned_by_migration_role \gset
\if :durable_objects_owned_by_migration_role
\else
  \echo 'Every durable table, sequence, view, and foreign table must be owned by the migration owner.'
  SELECT 1 / 0;
\endif
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT SELECT ON appsurface_durable.dispatch TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT ON appsurface_durable.store_metadata, appsurface_durable.schema_migration TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'REVOKE UPDATE ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch FROM %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (generation, state, updated_at) ON appsurface_durable.scope TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, due_at, updated_at, terminal_at, cancellation_requested_at, attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at, runtime_epoch, revision, result_contract_id, result_schema_version, result_codec_id, result_classification, result_retention_policy_id, result_payload, result_sha256, terminal_code) ON appsurface_durable.work TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.work_operator_command, appsurface_durable.effect_permit TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (status, resulting_state, resulting_revision, completed_at) ON appsurface_durable.work_operator_command TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (status, observed_at, details, runtime_epoch) ON appsurface_durable.effect_permit TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope_history, appsurface_durable.work_history TO %I',
    :'runtime_role') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO %I', :'runtime_role') \gexec

-- The host creates roles and assigns membership. This recipe transfers every package relation to the migration owner,
-- never grants DDL or BYPASSRLS to service roles, and does not treat runtime credentials as application authorization.
