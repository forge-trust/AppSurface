# Replace the sample Work

The generated host and test project belong to your application after creation. Keep the authenticated activation and provider composition in place, then replace the sample Work in these three locations:

| Location | Change |
| --- | --- |
| `src/FirstDurableWorker/Work/SampleWork.cs` and `SampleWorkExecutor.cs` | Define your input and result types, stable Work name/version, codecs, provider-safety and retry choices; implement the executor and any reconciliation contract. |
| `src/FirstDurableWorker/Work/WorkRegistration.cs` | Bind the typed definition to your executor/reconciler once. Keep the registration facts aligned with the definition; do not duplicate them in request code. |
| `src/FirstDurableWorker/Work/SampleWorkProducer.cs` | Construct and submit the typed acceptance request from your domain-owned producer path. Retain the returned acceptance receipt and authorized scope outside the wake request. |

The codec identity/version is part of the Work contract. If input or result serialization changes, treat it as a persisted-data compatibility change. If the Work name/version changes, decide how old and new accepted records will be processed before deploying the new producer or executor. Existing accepted records are not rewritten by a template update. Review the [typed Work definition guide](https://github.com/forge-trust/AppSurface/blob/main/Durable/migrations/typed-work-definitions-v1.md) and [Work protocol](https://github.com/forge-trust/AppSurface/blob/main/Durable/work-protocol-v1.md).

Do not change activation plumbing to select your Work. `POST /private/durable/activate` remains an empty, authenticated wake using `DurableRuntimeSurface.Work`; it does not carry your input, scope, Work ID, or effect identity. The provider remains authoritative for discovery, claims, leases, fences, execution state, and terminal bookkeeping. Inspect persisted Work and effect evidence before a retry; a lost response, timeout, or pump-level `Completed` result does not prove whether an external effect occurred.

The template's `ActivationAuthorization` policy and Development-only token are examples of host composition. Your application chooses the production authentication scheme, claims, permission semantics, ingress concurrency, deployment gate, and health integration. Startup validates that the policy and scheme exist before opening database connections or listening, but it cannot certify the identity provider's security. See the [canonical activation reference](https://github.com/forge-trust/AppSurface/blob/main/Durable/external-activation-v1.md) and the [host composition guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/durable-worker.md).

## Verify your replacement

Use a separate generated copy so the untouched template-shape proof stays reproducible. Replace only the Work definition/executor, registration, and producer locations above. Give the new Work distinct input, result, and persisted identity. Then build the solution, run its non-Docker contract tests, and run the Docker-backed `FirstDurableWork` lifecycle test. The real proof must still show an authorized wake, persisted terminal Work, `NotStarted` to `Healthy` readiness, and an exported activation activity.

Installing an updated template affects future projects only. For a later template shape, compare files against the [versioned shape guide](https://github.com/forge-trust/AppSurface/blob/main/releases/durable-worker-template-shape-v1.md), review package compatibility, and merge source changes manually. Do not overwrite your generated application wholesale or assume a template install performs a migration.
