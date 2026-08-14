<!-- appsurface:unreleased-entry section="migration-watch" -->
### PostgreSQL Work discovery

- [`ForgeTrust.AppSurface.Durable.PostgreSql`](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md)
  now supports PostgreSQL 16+, snapshots its configured Work contracts at activation, and uses only that immutable set
  for discovery before claims. Drain and stop every pre-`0009` worker first, apply `0009_work_contract_discovery.sql`,
  rerun the role recipe, and use the documented
  [`ASDUR119`](../../troubleshooting/durable-diagnostics.md#asdur119) recovery when a custom registration snapshot
  must be corrected and restarted.
