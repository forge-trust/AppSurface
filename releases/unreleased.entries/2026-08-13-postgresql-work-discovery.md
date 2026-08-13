<!-- appsurface:unreleased-entry section="migration-watch" -->
### PostgreSQL Work discovery

- [`ForgeTrust.AppSurface.Durable.PostgreSql`](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md)
  now supports PostgreSQL 16+ and discovers only its configured immutable Work contracts before each claim. Apply
  `0009_work_contract_discovery.sql`, drain every pre-migration worker before removing raw dispatcher access, and use
  the documented `ASDUR119` recovery when a custom registration snapshot must be corrected and restarted.
