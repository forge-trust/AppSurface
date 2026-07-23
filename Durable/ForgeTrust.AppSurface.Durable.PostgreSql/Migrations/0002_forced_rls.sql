ALTER TABLE appsurface_durable.scope ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.scope FORCE ROW LEVEL SECURITY;
CREATE POLICY scope_select ON appsurface_durable.scope
    FOR SELECT
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
CREATE POLICY scope_insert ON appsurface_durable.scope
    FOR INSERT
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
CREATE POLICY scope_disable ON appsurface_durable.scope
    FOR UPDATE
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK
    (
        scope_id = nullif(current_setting('appsurface_durable.scope_id', true), '')
        AND state = 'disabled'
    );

ALTER TABLE appsurface_durable.scope_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.scope_history FORCE ROW LEVEL SECURITY;
CREATE POLICY scope_history_isolation ON appsurface_durable.scope_history
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.work ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.work FORCE ROW LEVEL SECURITY;
CREATE POLICY work_scope_isolation ON appsurface_durable.work
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.work_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.work_history FORCE ROW LEVEL SECURITY;
CREATE POLICY work_history_scope_isolation ON appsurface_durable.work_history
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.work_operator_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.work_operator_command FORCE ROW LEVEL SECURITY;
CREATE POLICY work_operator_command_scope_isolation ON appsurface_durable.work_operator_command
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

ALTER TABLE appsurface_durable.effect_permit ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.effect_permit FORCE ROW LEVEL SECURITY;
CREATE POLICY effect_permit_scope_isolation ON appsurface_durable.effect_permit
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

-- Dispatch is payload-free and globally discoverable, but only its owning scoped runtime may mutate it.
ALTER TABLE appsurface_durable.dispatch ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.dispatch FORCE ROW LEVEL SECURITY;
CREATE POLICY dispatch_global_discovery ON appsurface_durable.dispatch
    FOR SELECT
    USING (true);
CREATE POLICY dispatch_scope_insert ON appsurface_durable.dispatch
    FOR INSERT
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));
CREATE POLICY dispatch_scope_update ON appsurface_durable.dispatch
    FOR UPDATE
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
