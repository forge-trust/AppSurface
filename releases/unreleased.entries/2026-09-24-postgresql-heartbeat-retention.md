<!-- appsurface:unreleased-entry section="included" -->
### Bounded PostgreSQL runtime heartbeat retention

- [`ForgeTrust.AppSurface.Durable.PostgreSql`](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md)
  now prunes stale runtime heartbeat identities after an admitted pass. The default diagnostic window is 24 hours;
  maintenance is asynchronous, deletes at most 500 rows per call, and does not make Work readiness depend on cleanup.
- Schema 11 adds an age-ordered index and a bounded deletion function. Apply the forward-only migration during a
  maintenance window, reapply the exact role recipe, and run structural preflight before reactivating workers. Follow
  the [heartbeat retention operations guide](../../Durable/heartbeat-retention-operations.md) for rollout order,
  catch-up limits, protected rows, the exact prior-binary proof, and recovery constraints.
