<!-- appsurface:unreleased-entry section="included" -->
### Durable operational assessments and admission certainty

- [`ForgeTrust.AppSurface.Durable.Provider`](../../Durable/ForgeTrust.AppSurface.Durable.Provider/README.md#operational-assessment-and-admission)
  adds `DurableRuntimeHealthState.Unavailable = 5`, the computed `WasStoreObserved`,
  `CanEnableActivation`, `CanAttemptPump`, and `IsReady` predicates, plus
  `IDurableRuntimePumpAdmission` and its closed four-kind attempt result. Update exhaustive enum switches and
  allow serialized health-state consumers to receive value `5`; do not infer store observation from
  `ObservedAtUtc`.
- External activators should call `TryRunOnceAsync` directly and handle `Completed`, `Refused`, `Unavailable`, and
  `Incompatible`. `Completed` always carries a result, including a zero-valued empty pass. Returned
  non-completed outcomes certify only that this invocation did not enter application execution; caller cancellation,
  execution failures, malformed provider state, and finalization failures still propagate.
- The PostgreSQL rollout adds `0010_runtime_health_observation.sql`. Apply migration 10 with the migration owner,
  rerun the canonical [PostgreSQL role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql),
  run status/preflight, and smoke-test the supported `v0.2.0-preview.8` rollback binary before deploying the new
  runtime. Binary rollback keeps schema 10 in place; schema defects require a corrective forward migration.
- Caller cancellation that arrives after application execution returns no longer cancels successful-sweep
  bookkeeping. The provider uses a fresh bounded finalization reserve, and any finalization failure propagates because
  execution may already have begun. Hosted shutdown budgets must include two reserves: one for finalization and one
  for best-effort failed-pass cleanup.
- See [Adopt Durable operational assessments](../../Durable/operational-assessments.md) for complete existing-host and
  custom-composition examples, diagnostics/remedies, cancellation semantics, rollout, rollback, and the one-command
  local proof.
