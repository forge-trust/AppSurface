<!-- appsurface:unreleased-entry section="included" -->
### Passive Durable external activation service (#804)

- Add a provider-neutral [external activation service and canonical v1 reference](../../Durable/external-activation-v1.md) for payload-free wakes, explicit cooperative request budgets, validated outcomes, recovery guidance, and bounded activity tags. The [authenticated PostgreSQL reference host and executable first-start guide](../../examples/durable-external-activation/README.md) show restricted-role provisioning, explicit schema and epoch setup, typed CLI acceptance, pre-heartbeat probes, and an authorized empty wake without starting a worker loop. Its PostgreSQL lifecycle test verifies persisted Work completion and bounded activation telemetry export through `ForceFlush`.
