ALTER TABLE appsurface_durable.scope ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.scope FORCE ROW LEVEL SECURITY;
CREATE POLICY scope_isolation ON appsurface_durable.scope
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

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

-- Dispatch intentionally has no payload and no row-level security. A dispatcher may discover opaque
-- scope and aggregate identifiers, but every claim and payload read must occur in a separate scoped transaction.
