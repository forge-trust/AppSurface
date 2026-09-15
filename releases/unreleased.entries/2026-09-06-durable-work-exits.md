<!-- appsurface:unreleased-entry section="included" -->
### Typed Durable Work exits

- [`ForgeTrust.AppSurface.Durable`](../../Durable/ForgeTrust.AppSurface.Durable/README.md#exit-aware-work-for-a-proven-pre-effect-retry)
  now lets `ProviderKeyed` Work opt into a closed executor result: success, a proven pre-effect retry, terminal
  application failure, or an explicitly ambiguous external outcome. Existing
  `IDurableWorkerExecutor<TWork, TResult>` registrations remain unchanged; the PostgreSQL provider keeps effect permits,
  retry timing, cancellation, and fencing authoritative rather than exposing a direct state mutation or retry hook.
- Register the exit-aware contract as a new immutable Work version only after every worker that can discover it runs
  exit-capable Provider/PostgreSQL binaries. Follow the package guide's fleet-complete rollout and rollback boundary;
  a legacy provider fails conservatively instead of treating a non-success exit as a successful completion.
