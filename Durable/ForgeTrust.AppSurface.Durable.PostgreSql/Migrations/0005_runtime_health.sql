CREATE TABLE appsurface_durable.runtime_heartbeat
(
    worker_id text PRIMARY KEY CHECK (length(worker_id) BETWEEN 1 AND 200),
    instance_id uuid NOT NULL,
    runtime_epoch uuid NOT NULL,
    hosted_surfaces smallint NOT NULL CHECK (hosted_surfaces BETWEEN 1 AND 7),
    started_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    last_heartbeat_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    last_successful_sweep_at timestamp with time zone,
    draining boolean NOT NULL DEFAULT false,
    pass_active boolean NOT NULL DEFAULT false,
    pass_started_at timestamp with time zone,
    last_discovered integer CHECK (last_discovered IS NULL OR last_discovered >= 0),
    last_claimed integer CHECK (last_claimed IS NULL OR last_claimed >= 0),
    last_processed integer CHECK (last_processed IS NULL OR last_processed >= 0),
    last_deferred integer CHECK (last_deferred IS NULL OR last_deferred >= 0),
    last_failed integer CHECK (last_failed IS NULL OR last_failed >= 0),
    last_pass_elapsed_ms double precision CHECK (last_pass_elapsed_ms IS NULL OR last_pass_elapsed_ms >= 0),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CHECK ((pass_active AND pass_started_at IS NOT NULL) OR (NOT pass_active AND pass_started_at IS NULL))
);

CREATE INDEX ix_runtime_heartbeat_epoch_liveness
    ON appsurface_durable.runtime_heartbeat (runtime_epoch, last_heartbeat_at, worker_id);
