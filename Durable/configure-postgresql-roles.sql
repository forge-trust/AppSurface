\set ON_ERROR_STOP on

-- Required psql variables contain role names as data, never raw SQL identifiers:
--   migration_owner_role, dispatcher_role, runtime_role
SELECT :'migration_owner_role' <> :'dispatcher_role'
   AND :'migration_owner_role' <> :'runtime_role'
   AND :'dispatcher_role' <> :'runtime_role' AS roles_are_distinct \gset
\if :roles_are_distinct
\else
  \echo 'Migration owner, dispatcher, and scoped runtime roles must be distinct.'
  \quit 3
\endif

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required migration owner role does not exist:' :'migration_owner_role'
  \quit 3
\endif

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'dispatcher_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required dispatcher role does not exist:' :'dispatcher_role'
  \quit 3
\endif

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'runtime_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required scoped runtime role does not exist:' :'runtime_role'
  \quit 3
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
  \quit 3
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
  \quit 3
\endif

SELECT format('ALTER SCHEMA appsurface_durable OWNER TO %I', :'migration_owner_role') \gexec
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

-- The host creates roles and assigns membership. This recipe never creates a principal, grants DDL,
-- grants BYPASSRLS, or treats runtime credentials as independent application authorization.
