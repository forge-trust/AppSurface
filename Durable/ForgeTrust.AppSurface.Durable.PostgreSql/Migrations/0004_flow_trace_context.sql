CREATE TABLE appsurface_durable.flow_trace_context
(
    trace_context_id uuid PRIMARY KEY,
    scope_id text NOT NULL,
    flow_instance_id text NOT NULL CHECK (length(flow_instance_id) BETWEEN 1 AND 200),
    contract_version smallint NOT NULL CHECK (contract_version = 1),
    traceparent varchar(55) NOT NULL CHECK
    (
        traceparent ~ '^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$'
    ),
    tracestate varchar(512),
    correlation_token uuid NOT NULL,
    cause_kind varchar(32) NOT NULL CHECK
    (
        cause_kind IN
        (
            'command_accepted',
            'activity_scheduled',
            'activity_completed',
            'event_winner',
            'timer_winner',
            'evaluation_committed'
        )
    ),
    committed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (scope_id, trace_context_id),
    FOREIGN KEY (scope_id, flow_instance_id)
        REFERENCES appsurface_durable.flow_instance(scope_id, flow_instance_id)
);

ALTER TABLE appsurface_durable.flow_instance
    ADD COLUMN trace_context_id uuid,
    ADD CONSTRAINT fk_flow_instance_trace_context
        FOREIGN KEY (scope_id, trace_context_id)
        REFERENCES appsurface_durable.flow_trace_context(scope_id, trace_context_id);

ALTER TABLE appsurface_durable.flow_command
    ADD COLUMN trace_context_id uuid,
    ADD CONSTRAINT fk_flow_command_trace_context
        FOREIGN KEY (scope_id, trace_context_id)
        REFERENCES appsurface_durable.flow_trace_context(scope_id, trace_context_id);

ALTER TABLE appsurface_durable.flow_history
    ADD COLUMN trace_context_id uuid,
    ADD CONSTRAINT fk_flow_history_trace_context
        FOREIGN KEY (scope_id, trace_context_id)
        REFERENCES appsurface_durable.flow_trace_context(scope_id, trace_context_id);

ALTER TABLE appsurface_durable.flow_wait
    ADD COLUMN trace_context_id uuid,
    ADD CONSTRAINT fk_flow_wait_trace_context
        FOREIGN KEY (scope_id, trace_context_id)
        REFERENCES appsurface_durable.flow_trace_context(scope_id, trace_context_id);

ALTER TABLE appsurface_durable.flow_timer
    ADD COLUMN trace_context_id uuid,
    ADD CONSTRAINT fk_flow_timer_trace_context
        FOREIGN KEY (scope_id, trace_context_id)
        REFERENCES appsurface_durable.flow_trace_context(scope_id, trace_context_id);

ALTER TABLE appsurface_durable.work
    ADD COLUMN trace_context_id uuid,
    ADD CONSTRAINT fk_work_trace_context
        FOREIGN KEY (scope_id, trace_context_id)
        REFERENCES appsurface_durable.flow_trace_context(scope_id, trace_context_id);

CREATE INDEX ix_flow_trace_context_resume
    ON appsurface_durable.flow_trace_context
        (scope_id, flow_instance_id, committed_at, trace_context_id);

ALTER TABLE appsurface_durable.flow_trace_context ENABLE ROW LEVEL SECURITY;
ALTER TABLE appsurface_durable.flow_trace_context FORCE ROW LEVEL SECURITY;
CREATE POLICY flow_trace_context_scope_isolation ON appsurface_durable.flow_trace_context
    USING (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''))
    WITH CHECK (scope_id = nullif(current_setting('appsurface_durable.scope_id', true), ''));

REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;

