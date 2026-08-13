\set ON_ERROR_STOP on

-- Required psql variables contain role names as data, never raw SQL identifiers:
--   migration_owner_role, dispatcher_role, runtime_role, retention_operator_role
SELECT :'migration_owner_role' <> :'dispatcher_role'
   AND :'migration_owner_role' <> :'runtime_role'
   AND :'migration_owner_role' <> :'retention_operator_role'
   AND :'dispatcher_role' <> :'runtime_role'
   AND :'dispatcher_role' <> :'retention_operator_role'
   AND :'runtime_role' <> :'retention_operator_role' AS roles_are_distinct \gset
\if :roles_are_distinct
\else
  \echo 'Migration owner, dispatcher, scoped runtime, and retention operator roles must be distinct.'
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

SELECT EXISTS
(
  SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'retention_operator_role'
) AS role_exists \gset
\if :role_exists
\else
  \echo 'Required scoped retention operator role does not exist:' :'retention_operator_role'
  SELECT 1 / 0;
\endif

SELECT bool_and(role_value.rolcanlogin)
   AND bool_and(NOT role_value.rolsuper)
   AND bool_and(NOT role_value.rolcreatedb)
   AND bool_and(NOT role_value.rolcreaterole)
   AND bool_and(NOT role_value.rolreplication)
   AND bool_and(NOT role_value.rolbypassrls)
  AS service_roles_are_restricted_login_leaves
FROM pg_catalog.pg_roles AS role_value
WHERE role_value.rolname IN (:'dispatcher_role', :'runtime_role', :'retention_operator_role') \gset
\if :service_roles_are_restricted_login_leaves
\else
  \echo 'Dispatcher, scoped runtime, and retention operator roles must be LOGIN roles without SUPERUSER, CREATEDB, CREATEROLE, REPLICATION, or BYPASSRLS.'
  SELECT 1 / 0;
\endif

WITH service_role AS
(
  SELECT role_value.oid
  FROM pg_catalog.pg_roles AS role_value
  WHERE role_value.rolname IN (:'dispatcher_role', :'runtime_role', :'retention_operator_role')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_auth_members AS membership
  JOIN service_role AS service
    ON service.oid = membership.member
    OR service.oid = membership.roleid
) AS service_roles_are_membership_free \gset
\if :service_roles_are_membership_free
\else
  \echo 'Dispatcher, scoped runtime, and retention operator roles must be exact login leaves with no role memberships in either direction.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_database AS database_value
  JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = database_value.datdba
  WHERE owner_role.rolname IN (:'dispatcher_role', :'runtime_role', :'retention_operator_role')
) AS service_roles_do_not_own_database \gset
\if :service_roles_do_not_own_database
\else
  \echo 'Dispatcher, scoped runtime, and retention operator roles must not own any database.'
  SELECT 1 / 0;
\endif

BEGIN;

SELECT pg_catalog.pg_advisory_xact_lock(4707181168775217740);

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

SELECT format(
    'ALTER FUNCTION %I.%I(%s) OWNER TO %I',
    namespace.nspname,
    routine.proname,
    pg_catalog.pg_get_function_identity_arguments(routine.oid),
    :'migration_owner_role')
FROM pg_catalog.pg_proc AS routine
JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
WHERE namespace.nspname = 'appsurface_durable'
  AND routine.prokind = 'f' \gexec

SELECT format(
    'ALTER POLICY flow_dispatch_global_discovery ON appsurface_durable.flow_dispatch TO %I, %I',
    :'dispatcher_role',
    :'migration_owner_role') \gexec
SELECT format(
    'ALTER POLICY work_contract_discovery_owner ON appsurface_durable.work TO %I',
    :'migration_owner_role') \gexec
SELECT format(
    'ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat TO %I',
    :'runtime_role') \gexec
DROP POLICY IF EXISTS flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch;
SELECT format(
    'CREATE POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch FOR SELECT TO %I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))',
    :'runtime_role') \gexec
DROP POLICY IF EXISTS flow_dispatch_retention_scope_select ON appsurface_durable.flow_dispatch;
SELECT format(
    'CREATE POLICY flow_dispatch_retention_scope_select ON appsurface_durable.flow_dispatch FOR SELECT TO %I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))',
    :'retention_operator_role') \gexec
SELECT format(
    'ALTER POLICY schedule_dispatch_global_discovery ON appsurface_durable.schedule_dispatch TO %I, %I',
    :'dispatcher_role',
    :'migration_owner_role') \gexec
SELECT format(
    'ALTER POLICY schedule_dispatch_global_lease ON appsurface_durable.schedule_dispatch TO %I, %I',
    :'dispatcher_role',
    :'migration_owner_role') \gexec
DROP POLICY IF EXISTS schedule_dispatch_runtime_scope_select ON appsurface_durable.schedule_dispatch;
SELECT format(
    'CREATE POLICY schedule_dispatch_runtime_scope_select ON appsurface_durable.schedule_dispatch FOR SELECT TO %I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))',
    :'runtime_role') \gexec
DROP POLICY IF EXISTS schedule_dispatch_scope_update ON appsurface_durable.schedule_dispatch;
SELECT format(
    'CREATE POLICY schedule_dispatch_scope_update ON appsurface_durable.schedule_dispatch FOR UPDATE TO %I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), '''')) WITH CHECK (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))',
    :'runtime_role') \gexec
SELECT format('REVOKE ALL ON TABLE appsurface_durable.schedule_dispatch FROM %I', :'dispatcher_role') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) FROM %I', :'dispatcher_role') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) FROM %I', :'runtime_role') \gexec
SELECT format('REVOKE ALL ON TABLE appsurface_durable.dispatch FROM %I', :'dispatcher_role') \gexec

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

SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_proc AS routine
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
  JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = routine.proowner
  WHERE namespace.nspname = 'appsurface_durable'
    AND routine.prokind = 'f'
    AND owner_role.rolname <> :'migration_owner_role'
) AS durable_functions_owned_by_migration_role \gset
\if :durable_functions_owned_by_migration_role
\else
  \echo 'Every durable function must be owned by the migration owner.'
  SELECT 1 / 0;
\endif

SELECT bool_and(
    object.relrowsecurity =
      (object.relname IN
        ('scope', 'scope_history', 'work', 'work_history', 'dispatch', 'work_operator_command', 'effect_permit', 'runtime_heartbeat',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item',
         'flow_retention_manifest_summary', 'flow_retention_manifest_event', 'flow_retention_command')
        OR object.relname LIKE 'schedule_history_%')
    AND object.relforcerowsecurity =
      (object.relname IN
        ('scope', 'scope_history', 'work', 'work_history', 'dispatch', 'work_operator_command', 'effect_permit', 'runtime_heartbeat',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item',
         'flow_retention_manifest_summary', 'flow_retention_manifest_event', 'flow_retention_command')
        OR object.relname LIKE 'schedule_history_%'))
  AS durable_rls_flags_are_exact
FROM pg_catalog.pg_class AS object
JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
WHERE namespace.nspname = 'appsurface_durable'
  AND object.relkind IN ('r', 'p') \gset
\if :durable_rls_flags_are_exact
\else
  \echo 'Durable row-level security flags must exactly match the package migration.'
  SELECT 1 / 0;
\endif

WITH schedule_history_child AS
(
  SELECT child.oid, child.relrowsecurity, child.relforcerowsecurity
  FROM pg_catalog.pg_inherits AS inheritance
  JOIN pg_catalog.pg_class AS parent ON parent.oid = inheritance.inhparent
  JOIN pg_catalog.pg_namespace AS parent_namespace ON parent_namespace.oid = parent.relnamespace
  JOIN pg_catalog.pg_class AS child ON child.oid = inheritance.inhrelid
  JOIN pg_catalog.pg_namespace AS child_namespace ON child_namespace.oid = child.relnamespace
  WHERE parent_namespace.nspname = 'appsurface_durable'
    AND parent.relname = 'schedule_history'
    AND child_namespace.nspname = 'appsurface_durable'
),
actual_child_policy AS
(
  SELECT child.relrowsecurity,
         child.relforcerowsecurity,
         policy.polname AS policy_name,
         policy.polcmd::text AS command_name,
         policy.polpermissive,
         policy.polroles,
         pg_catalog.pg_get_expr(policy.polqual, policy.polrelid) AS using_expression,
         pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid) AS check_expression
  FROM schedule_history_child AS child
  LEFT JOIN pg_catalog.pg_policy AS policy ON policy.polrelid = child.oid
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM actual_child_policy
  WHERE NOT relrowsecurity
    OR NOT relforcerowsecurity
    OR policy_name IS DISTINCT FROM 'schedule_history_scope_isolation'
    OR command_name IS DISTINCT FROM '*'
    OR NOT polpermissive
    OR polroles IS DISTINCT FROM ARRAY[0]::oid[]
    OR using_expression IS DISTINCT FROM
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'
    OR check_expression IS DISTINCT FROM
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'
) AS schedule_history_child_policies_are_exact \gset
\if :schedule_history_child_policies_are_exact
\else
  \echo 'Every schedule_history partition must have forced RLS and the exact scope-isolation policy.'
  SELECT 1 / 0;
\endif

WITH expected_policy(relation_name, policy_name, command_name, using_expression, check_expression) AS
(
  VALUES
    ('dispatch', 'dispatch_global_discovery', 'r', 'true', NULL::text),
    ('dispatch', 'dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('dispatch', 'dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_command', 'flow_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_dispatch', 'flow_dispatch_global_discovery', 'r', 'true', NULL::text),
    ('flow_dispatch', 'flow_dispatch_runtime_scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('flow_dispatch', 'flow_dispatch_retention_scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('flow_dispatch', 'flow_dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_dispatch', 'flow_dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_history', 'flow_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_repair_collision', 'flow_repair_collision_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_repair_command', 'flow_repair_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_trace_context', 'flow_trace_context_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_command', 'flow_retention_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest', 'flow_retention_manifest_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest_event', 'flow_retention_manifest_event_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest_item', 'flow_retention_manifest_item_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest_summary', 'flow_retention_manifest_summary_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_instance', 'flow_instance_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('runtime_heartbeat', 'runtime_heartbeat_runtime_role', '*', 'true', 'true'),
    ('flow_timer', 'flow_timer_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_wait', 'flow_wait_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_command', 'schedule_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_definition', 'schedule_definition_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_dispatch', 'schedule_dispatch_global_discovery', 'r', 'true', NULL::text),
    ('schedule_dispatch', 'schedule_dispatch_global_lease', 'w', 'true', 'true'),
    ('schedule_dispatch', 'schedule_dispatch_runtime_scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('schedule_dispatch', 'schedule_dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_dispatch', 'schedule_dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_generation', 'schedule_generation_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_history', 'schedule_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_occurrence', 'schedule_occurrence_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('effect_permit', 'effect_permit_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('scope', 'scope_disable', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '((scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text)) AND (state = ''disabled''::text))'),
    ('scope', 'scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('scope', 'scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('scope_history', 'scope_history_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('work', 'work_contract_discovery_owner', 'r', 'true', NULL::text),
    ('work', 'work_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('work_history', 'work_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('work_operator_command', 'work_operator_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))')
),
actual_policy AS
(
  SELECT
    object.relname AS relation_name,
    policy.polname AS policy_name,
    policy.polcmd::text AS command_name,
    policy.polpermissive,
    policy.polroles,
    pg_catalog.pg_get_expr(policy.polqual, policy.polrelid) AS using_expression,
    pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid) AS check_expression
  FROM pg_catalog.pg_policy AS policy
  JOIN pg_catalog.pg_class AS object ON object.oid = policy.polrelid
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relname NOT LIKE 'schedule_history_%'
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM expected_policy AS expected
  FULL OUTER JOIN actual_policy AS actual
    ON actual.relation_name = expected.relation_name
    AND actual.policy_name = expected.policy_name
  WHERE expected.policy_name IS NULL
    OR actual.policy_name IS NULL
    OR NOT actual.polpermissive
    OR NOT
    (
        CASE
            WHEN actual.policy_name = 'work_contract_discovery_owner' THEN actual.polroles = ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
            WHEN actual.policy_name = 'flow_dispatch_global_discovery' THEN actual.polroles @> ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'dispatcher_role'),
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
                AND actual.polroles <@ ARRAY[
                    (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'dispatcher_role'),
                    (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
            WHEN actual.policy_name IN ('schedule_dispatch_global_discovery', 'schedule_dispatch_global_lease') THEN actual.polroles @> ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'dispatcher_role'),
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
                AND actual.polroles <@ ARRAY[
                    (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'dispatcher_role'),
                    (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
            WHEN actual.policy_name IN ('flow_dispatch_runtime_scope_select', 'schedule_dispatch_runtime_scope_select', 'schedule_dispatch_scope_update') THEN actual.polroles @> ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'runtime_role')]
                AND actual.polroles <@ ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'runtime_role')]
            WHEN actual.policy_name = 'flow_dispatch_retention_scope_select' THEN actual.polroles @> ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'retention_operator_role')]
                AND actual.polroles <@ ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'retention_operator_role')]
            WHEN actual.policy_name = 'runtime_heartbeat_runtime_role' THEN actual.polroles @> ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'runtime_role')]
                AND actual.polroles <@ ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'runtime_role')]
            ELSE actual.polroles = ARRAY[0]::oid[]
        END
    )
    OR actual.command_name <> expected.command_name
    OR actual.using_expression IS DISTINCT FROM expected.using_expression
    OR actual.check_expression IS DISTINCT FROM expected.check_expression
) AS durable_rls_policies_are_exact \gset
\if :durable_rls_policies_are_exact
\else
  \echo 'Durable row-level security policies must exactly match the package migration.'
  SELECT 1 / 0;
\endif

SELECT NOT
(
  pg_catalog.has_schema_privilege(:'dispatcher_role', 'appsurface_durable', 'CREATE')
  OR pg_catalog.has_schema_privilege(:'runtime_role', 'appsurface_durable', 'CREATE')
  OR pg_catalog.has_schema_privilege(:'retention_operator_role', 'appsurface_durable', 'CREATE')
  OR pg_catalog.has_schema_privilege(:'dispatcher_role', 'appsurface_durable', 'USAGE WITH GRANT OPTION')
  OR pg_catalog.has_schema_privilege(:'runtime_role', 'appsurface_durable', 'USAGE WITH GRANT OPTION')
  OR pg_catalog.has_schema_privilege(:'retention_operator_role', 'appsurface_durable', 'USAGE WITH GRANT OPTION')
) AS service_roles_have_safe_schema_privileges \gset
\if :service_roles_have_safe_schema_privileges
\else
  \echo 'Dispatcher, scoped runtime, and retention operator roles must not have schema CREATE or grant options.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role'), (:'retention_operator_role')
),
durable_relation AS
(
  SELECT object.oid, object.relname
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'v', 'm', 'f')
),
relation_privilege(privilege_name) AS
(
  SELECT privilege_name
  FROM
  (
    VALUES
      ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER'),
      ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION'),
      ('DELETE WITH GRANT OPTION'), ('TRUNCATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION'),
      ('TRIGGER WITH GRANT OPTION'),
      ('MAINTAIN'), ('MAINTAIN WITH GRANT OPTION')
  ) AS candidate(privilege_name)
  WHERE pg_catalog.current_setting('server_version_num')::integer >= 170000
     OR candidate.privilege_name NOT LIKE 'MAINTAIN%'
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_relation AS relation
  CROSS JOIN relation_privilege AS privilege
  WHERE pg_catalog.has_table_privilege(
      service.role_name::pg_catalog.name,
      relation.oid,
      privilege.privilege_name)
   AND NOT
   (
     service.role_name = :'dispatcher_role'
     AND relation.relname = 'flow_dispatch'
     AND privilege.privilege_name = 'SELECT'
     OR service.role_name = :'runtime_role'
     AND
     (
       privilege.privilege_name = 'SELECT'
       AND relation.relname IN
       (
         'store_metadata', 'schema_migration', 'runtime_heartbeat', 'scope', 'work', 'dispatch',
         'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
       OR privilege.privilege_name = 'INSERT'
       AND relation.relname IN
       (
         'runtime_heartbeat', 'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
         'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
     )
     OR service.role_name = :'retention_operator_role'
     AND privilege.privilege_name = 'SELECT'
     AND relation.relname IN
     (
       'scope', 'work', 'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch',
       'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item', 'flow_retention_manifest_summary',
       'flow_retention_manifest_event', 'flow_retention_command'
     )
   )
) AS service_roles_have_safe_relation_privileges \gset
\if :service_roles_have_safe_relation_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-table privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role'), (:'retention_operator_role')
),
durable_column AS
(
  SELECT object.oid, object.relname, attribute.attnum, attribute.attname
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = object.oid
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'v', 'm', 'f')
    AND attribute.attnum > 0
    AND NOT attribute.attisdropped
),
column_privilege(privilege_name) AS
(
  VALUES
    ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES'),
    ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'),
    ('UPDATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_column AS column_value
  CROSS JOIN column_privilege AS privilege
  WHERE pg_catalog.has_column_privilege(
      service.role_name::pg_catalog.name,
      column_value.oid,
      column_value.attnum,
      privilege.privilege_name)
   AND NOT
   (
     service.role_name = :'dispatcher_role'
     AND column_value.relname = 'flow_dispatch'
     AND privilege.privilege_name = 'SELECT'
     OR service.role_name = :'runtime_role'
     AND
     (
       privilege.privilege_name = 'SELECT'
       AND column_value.relname IN
       (
         'store_metadata', 'schema_migration', 'runtime_heartbeat', 'scope', 'work', 'dispatch',
         'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
       OR privilege.privilege_name = 'INSERT'
       AND column_value.relname IN
       (
         'runtime_heartbeat', 'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
         'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
       OR privilege.privilege_name = 'UPDATE'
       AND
       (
         column_value.relname = 'scope'
         AND column_value.attname IN ('generation', 'state', 'updated_at')
         OR column_value.relname = 'runtime_heartbeat'
         AND column_value.attname IN
         (
           'worker_instance_id', 'runtime_epoch', 'hosted_surfaces', 'started_at', 'last_heartbeat_at', 'last_successful_sweep_at', 'draining', 'pass_active',
           'pass_started_at', 'last_discovered', 'last_claimed', 'last_processed', 'last_deferred',
           'last_failed', 'last_pass_elapsed_ms', 'updated_at'
         )
         OR column_value.relname = 'work'
         AND column_value.attname IN
         (
           'state', 'due_at', 'updated_at', 'terminal_at', 'cancellation_requested_at', 'attempt_number',
           'lease_generation', 'lease_owner', 'lease_started_at', 'lease_expires_at', 'runtime_epoch', 'revision',
           'result_contract_id', 'result_schema_version', 'result_codec_id', 'result_classification',
           'result_retention_policy_id', 'result_payload', 'result_sha256', 'terminal_code', 'trace_context_id'
         )
         OR column_value.relname = 'dispatch'
         AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
         OR column_value.relname = 'work_operator_command'
         AND column_value.attname IN ('status', 'resulting_state', 'resulting_revision', 'resolution_kind', 'completed_at')
         OR column_value.relname = 'effect_permit'
         AND column_value.attname IN ('status', 'observed_at', 'details', 'runtime_epoch')
         OR column_value.relname = 'flow_instance'
         AND column_value.attname IN
         (
           'state', 'current_node_id', 'context_contract_id', 'context_schema_version',
           'context_codec_id', 'context_payload', 'context_sha256', 'context_classification',
           'context_retention', 'resume_event_name', 'resume_event_is_timeout',
           'resume_event_contract_id', 'resume_event_schema_version', 'resume_event_codec_id',
           'resume_event_payload', 'resume_event_sha256', 'resume_event_classification',
           'resume_event_retention', 'activity_callsite_id', 'activity_result_contract_id',
           'activity_result_schema_version', 'activity_result_codec_id', 'activity_result_payload',
           'activity_result_sha256', 'activity_result_classification', 'activity_result_retention',
           'lease_generation', 'lease_owner', 'lease_started_at', 'lease_expires_at',
           'updated_at', 'cancellation_requested_at', 'terminal_at', 'terminal_code', 'trace_context_id',
           'suspension_descriptor', 'suspended_from_state', 'suspension_descriptor_schema',
           'suspension_descriptor_sha256', 'revision', 'scope_generation', 'runtime_epoch'
         )
         OR column_value.relname IN ('flow_command', 'flow_history')
         AND column_value.attname = 'trace_context_id'
         OR column_value.relname = 'flow_wait'
         AND column_value.attname IN ('state', 'resolved_revision', 'resolved_at', 'suspension_descriptor', 'updated_at', 'trace_context_id')
         OR column_value.relname = 'flow_timer'
         AND column_value.attname IN ('state', 'resolved_at', 'updated_at', 'trace_context_id')
         OR column_value.relname = 'flow_dispatch'
         AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
         OR column_value.relname = 'schedule_definition'
         AND column_value.attname IN
         (
           'display_name', 'state', 'active_generation', 'revision', 'accepted_at_utc', 'cursor_utc', 'next_due_utc',
           'scope_generation', 'runtime_epoch', 'suspension_code', 'updated_at'
         )
         OR column_value.relname = 'schedule_occurrence'
         AND column_value.attname IN
         (
           'last_nominal_utc', 'state', 'target_kind', 'target_id', 'target_command_id', 'target_idempotency_key',
           'claimed_by', 'lease_expires_at', 'updated_at'
         )
         OR column_value.relname = 'schedule_dispatch'
         AND column_value.attname IN
         (
           'dispatch_revision', 'due_at', 'state', 'lease_owner', 'lease_generation', 'lease_expires_at', 'updated_at'
         )
       )
     )
     OR service.role_name = :'retention_operator_role'
     AND privilege.privilege_name = 'SELECT'
     AND column_value.relname IN
     (
       'scope', 'work', 'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch',
       'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item', 'flow_retention_manifest_summary',
       'flow_retention_manifest_event', 'flow_retention_command'
     )
   )
) AS service_roles_have_safe_column_privileges \gset
\if :service_roles_have_safe_column_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-column privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role'), (:'retention_operator_role')
),
durable_sequence AS
(
  SELECT object.oid
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind = 'S'
),
sequence_privilege(privilege_name) AS
(
  VALUES
    ('USAGE'), ('SELECT'), ('UPDATE'),
    ('USAGE WITH GRANT OPTION'), ('SELECT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_sequence AS sequence_value
  CROSS JOIN sequence_privilege AS privilege
  WHERE pg_catalog.has_sequence_privilege(
      service.role_name::pg_catalog.name,
      sequence_value.oid,
      privilege.privilege_name)
    AND NOT
    (
      service.role_name = :'runtime_role'
      AND privilege.privilege_name IN ('USAGE', 'SELECT')
    )
) AS service_roles_have_safe_sequence_privileges \gset
\if :service_roles_have_safe_sequence_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-sequence privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role'), (:'retention_operator_role')
),
durable_function AS
(
  SELECT routine.oid, routine.proname
  FROM pg_catalog.pg_proc AS routine
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND routine.prokind = 'f'
),
function_privilege(privilege_name) AS
(
  VALUES ('EXECUTE'), ('EXECUTE WITH GRANT OPTION')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_function AS routine
  CROSS JOIN function_privilege AS privilege
  WHERE pg_catalog.has_function_privilege(
      service.role_name::pg_catalog.name,
      routine.oid,
      privilege.privilege_name)
    AND NOT
    (
      service.role_name = :'dispatcher_role'
      AND routine.oid IN
      (
        'appsurface_durable.claim_schedule_dispatch(text, interval)'::pg_catalog.regprocedure,
        'appsurface_durable.discover_work_dispatch(text[], text[], integer)'::pg_catalog.regprocedure
      )
      AND privilege.privilege_name = 'EXECUTE'
      OR service.role_name = :'runtime_role'
      AND routine.oid = 'appsurface_durable.runtime_due_dispatch_health(integer)'::pg_catalog.regprocedure
      AND privilege.privilege_name = 'EXECUTE'
      OR service.role_name = :'retention_operator_role'
      AND routine.oid IN
      (
        'appsurface_durable.create_flow_retention_manifest(text, text, text, text, char(64), text, char(64), integer, bigint, jsonb, text, text, char(64))'::pg_catalog.regprocedure,
        'appsurface_durable.apply_flow_retention_lifecycle(text, text, text, text, text, char(64), text, text, bigint, text, text, char(64), text, char(64), integer, boolean)'::pg_catalog.regprocedure
      )
      AND privilege.privilege_name = 'EXECUTE'
    )
) AS service_roles_have_safe_function_privileges \gset
\if :service_roles_have_safe_function_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-function privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT SELECT ON appsurface_durable.flow_dispatch TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer) TO %I', :'dispatcher_role') \gexec
SELECT format('REVOKE ALL ON SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'retention_operator_role') \gexec
SELECT format(
    'GRANT SELECT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.flow_trace_context, appsurface_durable.flow_retention_manifest, appsurface_durable.flow_retention_manifest_item, appsurface_durable.flow_retention_manifest_summary, appsurface_durable.flow_retention_manifest_event, appsurface_durable.flow_retention_command TO %I',
    :'retention_operator_role') \gexec
SELECT format(
    'GRANT EXECUTE ON FUNCTION appsurface_durable.create_flow_retention_manifest(text, text, text, text, char(64), text, char(64), integer, bigint, jsonb, text, text, char(64)) TO %I',
    :'retention_operator_role') \gexec
SELECT format(
    'GRANT EXECUTE ON FUNCTION appsurface_durable.apply_flow_retention_lifecycle(text, text, text, text, text, char(64), text, text, bigint, text, text, char(64), text, char(64), integer, boolean) TO %I',
    :'retention_operator_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'runtime_role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO %I', :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT ON appsurface_durable.store_metadata, appsurface_durable.schema_migration, appsurface_durable.runtime_heartbeat TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.flow_repair_command, appsurface_durable.flow_repair_collision, appsurface_durable.flow_trace_context, appsurface_durable.schedule_definition, appsurface_durable.schedule_generation, appsurface_durable.schedule_command, appsurface_durable.schedule_occurrence, appsurface_durable.schedule_dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'REVOKE UPDATE ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.schedule_definition, appsurface_durable.schedule_generation, appsurface_durable.schedule_command, appsurface_durable.schedule_occurrence, appsurface_durable.schedule_dispatch FROM %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (generation, state, updated_at) ON appsurface_durable.scope TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, due_at, updated_at, terminal_at, cancellation_requested_at, attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at, runtime_epoch, revision, result_contract_id, result_schema_version, result_codec_id, result_classification, result_retention_policy_id, result_payload, result_sha256, terminal_code, trace_context_id) ON appsurface_durable.work TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, current_node_id, context_contract_id, context_schema_version, context_codec_id, context_payload, context_sha256, context_classification, context_retention, resume_event_name, resume_event_is_timeout, resume_event_contract_id, resume_event_schema_version, resume_event_codec_id, resume_event_payload, resume_event_sha256, resume_event_classification, resume_event_retention, activity_callsite_id, activity_result_contract_id, activity_result_schema_version, activity_result_codec_id, activity_result_payload, activity_result_sha256, activity_result_classification, activity_result_retention, lease_generation, lease_owner, lease_started_at, lease_expires_at, updated_at, cancellation_requested_at, terminal_at, terminal_code, suspension_descriptor, suspended_from_state, suspension_descriptor_schema, suspension_descriptor_sha256, revision, scope_generation, runtime_epoch, trace_context_id) ON appsurface_durable.flow_instance TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (trace_context_id) ON appsurface_durable.flow_command, appsurface_durable.flow_history TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, resolved_revision, resolved_at, suspension_descriptor, updated_at, trace_context_id) ON appsurface_durable.flow_wait TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, resolved_at, updated_at, trace_context_id) ON appsurface_durable.flow_timer TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.flow_dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (display_name, state, active_generation, revision, accepted_at_utc, cursor_utc, next_due_utc, scope_generation, runtime_epoch, suspension_code, updated_at) ON appsurface_durable.schedule_definition TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (last_nominal_utc, state, target_kind, target_id, target_command_id, target_idempotency_key, claimed_by, lease_expires_at, updated_at) ON appsurface_durable.schedule_occurrence TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (dispatch_revision, due_at, state, lease_owner, lease_generation, lease_expires_at, updated_at) ON appsurface_durable.schedule_dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT INSERT, UPDATE (worker_instance_id, runtime_epoch, hosted_surfaces, started_at, last_heartbeat_at, last_successful_sweep_at, draining, pass_active, pass_started_at, last_discovered, last_claimed, last_processed, last_deferred, last_failed, last_pass_elapsed_ms, updated_at) ON appsurface_durable.runtime_heartbeat TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.work_operator_command, appsurface_durable.effect_permit TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (status, resulting_state, resulting_revision, resolution_kind, completed_at) ON appsurface_durable.work_operator_command TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (status, observed_at, details, runtime_epoch) ON appsurface_durable.effect_permit TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope_history, appsurface_durable.work_history TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.schedule_history TO %I',
    :'runtime_role') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO %I', :'runtime_role') \gexec

COMMIT;

-- The host creates roles and assigns membership. This recipe transfers every package relation to the migration owner,
-- never grants DDL or BYPASSRLS to service roles, and does not treat runtime credentials as application authorization.
