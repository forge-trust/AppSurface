-- Test-owned, schema-10 projection of the public role boundary for issue #845.
-- This fixture is deliberately not the current packaged recipe. It installs
-- only the schema-10 ownership, policy, grants, and function access needed for
-- low-level Work completion, Flow discovery, and Schedule claim probes before
-- applying migration 0011. The controller retires these unique SQL probes under
-- the restricted runtime and verifies their persisted state before upgrading.
-- Actual registered provider Work/Flow/Schedule lanes run after the exact CLI
-- upgrade and current packaged recipe; this fixture is not old-binary proof.

\if :{?migration_owner_role}
\else
  \echo 'Missing migration_owner_role'
  SELECT 1 / 0;
\endif
\if :{?full_dispatcher_role}
\else
  \echo 'Missing full_dispatcher_role'
  SELECT 1 / 0;
\endif
\if :{?full_runtime_role}
\else
  \echo 'Missing full_runtime_role'
  SELECT 1 / 0;
\endif
\if :{?source_dispatcher_role}
\else
  \echo 'Missing source_dispatcher_role'
  SELECT 1 / 0;
\endif
\if :{?source_runtime_role}
\else
  \echo 'Missing source_runtime_role'
  SELECT 1 / 0;
\endif

SELECT format('%I, %I', :'full_dispatcher_role', :'source_dispatcher_role') AS dispatcher_roles_sql,
       format('%I, %I', :'full_runtime_role', :'source_runtime_role') AS runtime_roles_sql,
       format('%I', :'full_dispatcher_role') AS full_dispatcher_roles_sql,
       format('%I', :'migration_owner_role') AS migration_owner_sql
\gset

SELECT schema_version = 10
   AND (SELECT count(*) FROM appsurface_durable.schema_migration) = 10
   AND (SELECT max(version) FROM appsurface_durable.schema_migration) = 10
   AND to_regprocedure('appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)') IS NULL
   AND (SELECT count(*) = 4 FROM pg_catalog.pg_roles
        WHERE rolname IN (:'full_dispatcher_role', :'full_runtime_role',
                          :'source_dispatcher_role', :'source_runtime_role')
          AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls)
   AND (SELECT r.rolname = :'migration_owner_role'
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_roles r ON r.oid = c.relowner
        WHERE n.nspname = 'appsurface_durable' AND c.relname = 'store_metadata')
   AS valid_schema10_owner_and_role_boundary
FROM appsurface_durable.store_metadata WHERE singleton
\gset
\if :valid_schema10_owner_and_role_boundary
\else
  \echo 'Schema-10 baseline, migration owner, or four restricted pair roles are invalid'
  SELECT 1 / 0;
\endif

-- Narrow the schema-10 global discovery policies to the reviewed full pair.
SELECT format('ALTER POLICY flow_dispatch_global_discovery ON appsurface_durable.flow_dispatch TO %s, %s',
              :'full_dispatcher_roles_sql', :'migration_owner_sql')
\gexec
SELECT format('ALTER POLICY schedule_dispatch_global_discovery ON appsurface_durable.schedule_dispatch TO %s, %s',
              :'full_dispatcher_roles_sql', :'migration_owner_sql')
\gexec
SELECT format('ALTER POLICY schedule_dispatch_global_lease ON appsurface_durable.schedule_dispatch TO %s, %s',
              :'full_dispatcher_roles_sql', :'migration_owner_sql')
\gexec
SELECT format('ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat TO %s', :'runtime_roles_sql')
\gexec

CREATE POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch
    FOR SELECT TO :"full_runtime_role", :"source_runtime_role"
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
CREATE POLICY schedule_dispatch_runtime_scope_select ON appsurface_durable.schedule_dispatch
    FOR SELECT TO :"full_runtime_role", :"source_runtime_role"
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
CREATE POLICY schedule_dispatch_scope_update ON appsurface_durable.schedule_dispatch
    FOR UPDATE TO :"full_runtime_role", :"source_runtime_role"
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %s', :'dispatcher_roles_sql') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer) TO %s', :'dispatcher_roles_sql') \gexec
SELECT format('GRANT SELECT ON appsurface_durable.flow_dispatch TO %s', :'full_dispatcher_roles_sql') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) TO %s', :'full_dispatcher_roles_sql') \gexec
SELECT format('GRANT SELECT ON appsurface_durable.store_metadata, appsurface_durable.schema_migration, appsurface_durable.runtime_heartbeat TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT SELECT, INSERT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.flow_repair_command, appsurface_durable.flow_repair_collision, appsurface_durable.flow_trace_context, appsurface_durable.schedule_definition, appsurface_durable.schedule_generation, appsurface_durable.schedule_command, appsurface_durable.schedule_occurrence, appsurface_durable.schedule_dispatch TO %s', :'runtime_roles_sql') \gexec
SELECT format('REVOKE UPDATE ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.schedule_definition, appsurface_durable.schedule_generation, appsurface_durable.schedule_command, appsurface_durable.schedule_occurrence, appsurface_durable.schedule_dispatch FROM %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (generation, state, updated_at) ON appsurface_durable.scope TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (state, due_at, updated_at, terminal_at, cancellation_requested_at, attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at, runtime_epoch, revision, result_contract_id, result_schema_version, result_codec_id, result_classification, result_retention_policy_id, result_payload, result_sha256, terminal_code, trace_context_id) ON appsurface_durable.work TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.dispatch TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (state, current_node_id, context_contract_id, context_schema_version, context_codec_id, context_payload, context_sha256, context_classification, context_retention, resume_event_name, resume_event_is_timeout, resume_event_contract_id, resume_event_schema_version, resume_event_codec_id, resume_event_payload, resume_event_sha256, resume_event_classification, resume_event_retention, activity_callsite_id, activity_result_contract_id, activity_result_schema_version, activity_result_codec_id, activity_result_payload, activity_result_sha256, activity_result_classification, activity_result_retention, lease_generation, lease_owner, lease_started_at, lease_expires_at, updated_at, cancellation_requested_at, terminal_at, terminal_code, suspension_descriptor, suspended_from_state, suspension_descriptor_schema, suspension_descriptor_sha256, revision, scope_generation, runtime_epoch, trace_context_id) ON appsurface_durable.flow_instance TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (trace_context_id) ON appsurface_durable.flow_command, appsurface_durable.flow_history TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (state, resolved_revision, resolved_at, suspension_descriptor, updated_at, trace_context_id) ON appsurface_durable.flow_wait TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (state, resolved_at, updated_at, trace_context_id) ON appsurface_durable.flow_timer TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.flow_dispatch TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (display_name, state, active_generation, revision, accepted_at_utc, cursor_utc, next_due_utc, scope_generation, runtime_epoch, suspension_code, updated_at) ON appsurface_durable.schedule_definition TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (last_nominal_utc, state, target_kind, target_id, target_command_id, target_idempotency_key, claimed_by, lease_expires_at, updated_at) ON appsurface_durable.schedule_occurrence TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (dispatch_revision, due_at, state, lease_owner, lease_generation, lease_expires_at, updated_at) ON appsurface_durable.schedule_dispatch TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT INSERT, UPDATE (worker_instance_id, runtime_epoch, hosted_surfaces, started_at, last_heartbeat_at, last_successful_sweep_at, draining, pass_active, pass_started_at, last_discovered, last_claimed, last_processed, last_deferred, last_failed, last_pass_elapsed_ms, updated_at) ON appsurface_durable.runtime_heartbeat TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT SELECT, INSERT ON appsurface_durable.work_operator_command, appsurface_durable.effect_permit TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (status, resulting_state, resulting_revision, resolution_kind, completed_at) ON appsurface_durable.work_operator_command TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT UPDATE (status, observed_at, details, runtime_epoch) ON appsurface_durable.effect_permit TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT SELECT, INSERT ON appsurface_durable.scope_history, appsurface_durable.work_history TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT SELECT, INSERT ON appsurface_durable.schedule_history TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO %s', :'runtime_roles_sql') \gexec

-- Check the effective ownership and key pre-0011 execution capabilities before
-- any lane is attempted. Migration 0011's prune function must remain absent.
SELECT current_schema_owner.rolname = :'migration_owner_role'
   AND has_function_privilege(:'full_dispatcher_role', 'appsurface_durable.discover_work_dispatch(text[], text[], integer)', 'EXECUTE')
   AND has_function_privilege(:'full_dispatcher_role', 'appsurface_durable.claim_schedule_dispatch(text, interval)', 'EXECUTE')
   AND has_table_privilege(:'full_dispatcher_role', 'appsurface_durable.flow_dispatch', 'SELECT')
   AND (SELECT count(*) = 2 FROM pg_catalog.pg_proc routine
        WHERE routine.oid IN (
          'appsurface_durable.discover_work_dispatch(text[],text[],integer)'::regprocedure,
          'appsurface_durable.claim_schedule_dispatch(text,interval)'::regprocedure)
          AND routine.proowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role'))
   AND NOT has_function_privilege(:'source_dispatcher_role', 'appsurface_durable.claim_schedule_dispatch(text, interval)', 'EXECUTE')
   AND NOT has_table_privilege(:'source_dispatcher_role', 'appsurface_durable.flow_dispatch', 'SELECT')
   AND to_regprocedure('appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)') IS NULL
   AS schema10_role_boundary_valid
FROM pg_catalog.pg_namespace namespace_value
JOIN pg_catalog.pg_roles current_schema_owner ON current_schema_owner.oid = namespace_value.nspowner
WHERE namespace_value.nspname = 'appsurface_durable'
\gset
\if :schema10_role_boundary_valid
\else
  \echo 'Schema-10 role boundary or pre-0011 function state is invalid'
  SELECT 1 / 0;
\endif
