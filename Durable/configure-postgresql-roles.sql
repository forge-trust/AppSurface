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

SELECT bool_and(role_value.rolcanlogin)
   AND bool_and(NOT role_value.rolsuper)
   AND bool_and(NOT role_value.rolcreatedb)
   AND bool_and(NOT role_value.rolcreaterole)
   AND bool_and(NOT role_value.rolreplication)
   AND bool_and(NOT role_value.rolbypassrls)
  AS service_roles_are_restricted_login_leaves
FROM pg_catalog.pg_roles AS role_value
WHERE role_value.rolname IN (:'dispatcher_role', :'runtime_role') \gset
\if :service_roles_are_restricted_login_leaves
\else
  \echo 'Dispatcher and scoped runtime roles must be LOGIN roles without SUPERUSER, CREATEDB, CREATEROLE, REPLICATION, or BYPASSRLS.'
  SELECT 1 / 0;
\endif

WITH service_role AS
(
  SELECT role_value.oid
  FROM pg_catalog.pg_roles AS role_value
  WHERE role_value.rolname IN (:'dispatcher_role', :'runtime_role')
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
  \echo 'Dispatcher and scoped runtime roles must be exact login leaves with no role memberships in either direction.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_database AS database_value
  JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = database_value.datdba
  WHERE owner_role.rolname IN (:'dispatcher_role', :'runtime_role')
) AS service_roles_do_not_own_database \gset
\if :service_roles_do_not_own_database
\else
  \echo 'Dispatcher and scoped runtime roles must not own any database.'
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
    'ALTER POLICY flow_dispatch_global_discovery ON appsurface_durable.flow_dispatch TO %I',
    :'dispatcher_role') \gexec
DROP POLICY IF EXISTS flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch;
SELECT format(
    'CREATE POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch FOR SELECT TO %I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))',
    :'runtime_role') \gexec

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

SELECT bool_and(
    object.relrowsecurity =
      (object.relname IN
        ('scope', 'scope_history', 'work', 'work_history', 'dispatch', 'work_operator_command', 'effect_permit',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch'))
    AND object.relforcerowsecurity =
      (object.relname IN
        ('scope', 'scope_history', 'work', 'work_history', 'dispatch', 'work_operator_command', 'effect_permit',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch')))
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
    ('flow_dispatch', 'flow_dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_dispatch', 'flow_dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_history', 'flow_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_instance', 'flow_instance_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_timer', 'flow_timer_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_wait', 'flow_wait_scope_isolation', '*',
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
    OR actual.polroles <> CASE
        WHEN actual.policy_name = 'flow_dispatch_global_discovery' THEN ARRAY[
            (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'dispatcher_role')]
        WHEN actual.policy_name = 'flow_dispatch_runtime_scope_select' THEN ARRAY[
            (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'runtime_role')]
        ELSE ARRAY[0]::oid[]
    END
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
  OR pg_catalog.has_schema_privilege(:'dispatcher_role', 'appsurface_durable', 'USAGE WITH GRANT OPTION')
  OR pg_catalog.has_schema_privilege(:'runtime_role', 'appsurface_durable', 'USAGE WITH GRANT OPTION')
) AS service_roles_have_safe_schema_privileges \gset
\if :service_roles_have_safe_schema_privileges
\else
  \echo 'Dispatcher and scoped runtime roles must not have schema CREATE or grant options.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role')
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
  VALUES
    ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER'), ('MAINTAIN'),
    ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION'),
    ('DELETE WITH GRANT OPTION'), ('TRUNCATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION'),
    ('TRIGGER WITH GRANT OPTION'), ('MAINTAIN WITH GRANT OPTION')
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
     AND relation.relname IN ('dispatch', 'flow_dispatch')
     AND privilege.privilege_name = 'SELECT'
     OR service.role_name = :'runtime_role'
     AND
     (
       privilege.privilege_name = 'SELECT'
       AND relation.relname IN
       (
         'store_metadata', 'schema_migration', 'scope', 'work', 'dispatch',
         'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch'
       )
       OR privilege.privilege_name = 'INSERT'
       AND relation.relname IN
       (
         'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
         'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch'
       )
     )
   )
) AS service_roles_have_safe_relation_privileges \gset
\if :service_roles_have_safe_relation_privileges
\else
  \echo 'Dispatcher or scoped runtime role has an effective durable-table privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role')
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
     AND column_value.relname IN ('dispatch', 'flow_dispatch')
     AND privilege.privilege_name = 'SELECT'
     OR service.role_name = :'runtime_role'
     AND
     (
       privilege.privilege_name = 'SELECT'
       AND column_value.relname IN
       (
         'store_metadata', 'schema_migration', 'scope', 'work', 'dispatch',
         'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch'
       )
       OR privilege.privilege_name = 'INSERT'
       AND column_value.relname IN
       (
         'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
         'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch'
       )
       OR privilege.privilege_name = 'UPDATE'
       AND
       (
         column_value.relname = 'scope'
         AND column_value.attname IN ('generation', 'state', 'updated_at')
         OR column_value.relname = 'work'
         AND column_value.attname IN
         (
           'state', 'due_at', 'updated_at', 'terminal_at', 'cancellation_requested_at', 'attempt_number',
           'lease_generation', 'lease_owner', 'lease_started_at', 'lease_expires_at', 'runtime_epoch', 'revision',
           'result_contract_id', 'result_schema_version', 'result_codec_id', 'result_classification',
           'result_retention_policy_id', 'result_payload', 'result_sha256', 'terminal_code'
         )
         OR column_value.relname = 'dispatch'
         AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
         OR column_value.relname = 'work_operator_command'
         AND column_value.attname IN ('status', 'resulting_state', 'resulting_revision', 'completed_at')
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
           'updated_at', 'cancellation_requested_at', 'terminal_at', 'terminal_code',
           'suspension_descriptor', 'suspended_from_state', 'revision', 'scope_generation', 'runtime_epoch'
         )
         OR column_value.relname = 'flow_wait'
         AND column_value.attname IN ('state', 'resolved_revision', 'resolved_at', 'suspension_descriptor', 'updated_at')
         OR column_value.relname = 'flow_timer'
         AND column_value.attname IN ('state', 'resolved_at', 'updated_at')
         OR column_value.relname = 'flow_dispatch'
         AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
       )
     )
   )
) AS service_roles_have_safe_column_privileges \gset
\if :service_roles_have_safe_column_privileges
\else
  \echo 'Dispatcher or scoped runtime role has an effective durable-column privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name) AS
(
  VALUES (:'dispatcher_role'), (:'runtime_role')
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
  \echo 'Dispatcher or scoped runtime role has an effective durable-sequence privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT SELECT ON appsurface_durable.dispatch TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT SELECT ON appsurface_durable.flow_dispatch TO %I', :'dispatcher_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT ON appsurface_durable.store_metadata, appsurface_durable.schema_migration TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch TO %I',
    :'runtime_role') \gexec
SELECT format(
    'REVOKE UPDATE ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch FROM %I',
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
    'GRANT UPDATE (state, current_node_id, context_contract_id, context_schema_version, context_codec_id, context_payload, context_sha256, context_classification, context_retention, resume_event_name, resume_event_is_timeout, resume_event_contract_id, resume_event_schema_version, resume_event_codec_id, resume_event_payload, resume_event_sha256, resume_event_classification, resume_event_retention, activity_callsite_id, activity_result_contract_id, activity_result_schema_version, activity_result_codec_id, activity_result_payload, activity_result_sha256, activity_result_classification, activity_result_retention, lease_generation, lease_owner, lease_started_at, lease_expires_at, updated_at, cancellation_requested_at, terminal_at, terminal_code, suspension_descriptor, suspended_from_state, revision, scope_generation, runtime_epoch) ON appsurface_durable.flow_instance TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, resolved_revision, resolved_at, suspension_descriptor, updated_at) ON appsurface_durable.flow_wait TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (state, resolved_at, updated_at) ON appsurface_durable.flow_timer TO %I',
    :'runtime_role') \gexec
SELECT format(
    'GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.flow_dispatch TO %I',
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

COMMIT;

-- The host creates roles and assigns membership. This recipe transfers every package relation to the migration owner,
-- never grants DDL or BYPASSRLS to service roles, and does not treat runtime credentials as application authorization.
