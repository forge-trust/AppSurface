-- Run this template after applying the numbered AppSurface Durable migrations. The tutorial uses one local database
-- administrator; production infrastructure should split NOLOGIN role creation from the schema owner's object grants.
-- Provision LOGIN users and their secrets separately, then grant them one of these NOLOGIN roles.

CREATE ROLE appsurface_durable_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE appsurface_durable_dispatcher NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE appsurface_durable_epoch_operator NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM appsurface_durable_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM appsurface_durable_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM appsurface_durable_epoch_operator;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM appsurface_durable_epoch_operator;

GRANT USAGE ON SCHEMA appsurface_durable TO appsurface_durable_runtime;
GRANT EXECUTE ON FUNCTION appsurface_durable.initialize_runtime_epoch(uuid)
TO appsurface_durable_runtime;

-- Compatibility metadata is read-only to the runtime. Only the migration owner may append migrations or advance it.
GRANT SELECT ON TABLE
    appsurface_durable.schema_migration,
    appsurface_durable.store_metadata
TO appsurface_durable_runtime;

-- Mutable protocol records. The runtime never receives DELETE because protocol v1 has no reviewed purge operation.
GRANT SELECT, INSERT, UPDATE ON TABLE
    appsurface_durable.scope,
    appsurface_durable.work,
    appsurface_durable.work_operator_command,
    appsurface_durable.runtime_heartbeat,
    appsurface_durable.dispatch,
    appsurface_durable.effect_permit,
    appsurface_durable.flow_instance,
    appsurface_durable.flow_wait,
    appsurface_durable.flow_timer,
    appsurface_durable.schedule_current,
    appsurface_durable.schedule_occurrence,
    appsurface_durable.schedule_run_slot
TO appsurface_durable_runtime;

-- Immutable aggregate and command identities are inserted and read, never changed in place.
GRANT SELECT, INSERT ON TABLE
    appsurface_durable.flow_command,
    appsurface_durable.schedule,
    appsurface_durable.schedule_command
TO appsurface_durable_runtime;

-- History is append-only. UPDATE and DELETE are intentionally absent.
GRANT SELECT, INSERT ON TABLE
    appsurface_durable.scope_history,
    appsurface_durable.work_history,
    appsurface_durable.flow_history,
    appsurface_durable.schedule_history
TO appsurface_durable_runtime;

GRANT USAGE, SELECT ON SEQUENCE
    appsurface_durable.scope_history_event_id_seq,
    appsurface_durable.work_history_event_id_seq,
    appsurface_durable.flow_history_event_id_seq,
    appsurface_durable.schedule_history_event_id_seq
TO appsurface_durable_runtime;

GRANT USAGE ON SCHEMA appsurface_durable TO appsurface_durable_dispatcher;
GRANT SELECT ON TABLE appsurface_durable.dispatch TO appsurface_durable_dispatcher;

-- Only a deployment/recovery principal receives epoch-rotation authority. SELECT is required for schema validation and
-- for the columns referenced by the optimistic UPDATE; column-scoped UPDATE prevents unrelated metadata changes.
GRANT USAGE ON SCHEMA appsurface_durable TO appsurface_durable_epoch_operator;
GRANT SELECT ON TABLE
    appsurface_durable.schema_migration,
    appsurface_durable.store_metadata,
    appsurface_durable.runtime_epoch_history
TO appsurface_durable_epoch_operator;
GRANT UPDATE (active_runtime_epoch, updated_at) ON TABLE appsurface_durable.store_metadata
TO appsurface_durable_epoch_operator;
GRANT INSERT ON TABLE appsurface_durable.runtime_epoch_history
TO appsurface_durable_epoch_operator;
GRANT USAGE, SELECT ON SEQUENCE appsurface_durable.runtime_epoch_history_event_id_seq
TO appsurface_durable_epoch_operator;

-- Do not use broad default table privileges for future migrations. Review each new table's exact protocol operations
-- and add its least-privilege grant alongside the migration before compatible workers are released.

-- Example only: grant group membership to separately provisioned LOGIN users.
-- GRANT appsurface_durable_runtime TO my_application_runtime_login;
-- GRANT appsurface_durable_dispatcher TO my_dispatcher_login;
-- GRANT appsurface_durable_epoch_operator TO my_recovery_operator_login;

-- Application operators intentionally receive no direct durable-table grant. Authorize them in the application,
-- then use IDurableWorkControlClient / IDurableScheduleClient so actor and reason codes are audited with revisions.
