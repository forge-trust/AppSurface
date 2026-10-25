# Deployment reference

## Portable intent

`DeploymentIntent` is a schema-versioned, provider-neutral snapshot of one evaluated Aspire environment and an explicit full source revision. Version 1 accepts one or more `MigrationJobIntent` values. Jobs are sorted by `DeploymentLogicalId`; duplicate ids and empty targets fail before rendering.

A migration job contains an immutable `registry/repository@sha256:<64 lowercase hex>` image, candidate-preparation phase, executable and ordered arguments, explicit task/parallelism/retry/timeout bounds, logical connection-secret and database bindings, logical service identity, non-secret environment settings, and required run-to-completion, relational-connection, and private-network capabilities.

`SecretBinding` is a reference, never a secret container. Version 1 supports environment-variable reference delivery only. Plaintext secret-shaped environment fields are rejected. Provider adapters must not evaluate the parameter named by the binding.

## Provider contract

`IDeploymentTarget.RenderAsync` receives validated intent, a non-secret binding-profile path, output directory, generator version, and optional trusted authoring root. The GCP target uses that root to reject every symlink or reparse point between the AppHost and profile before reading. Without a trusted root, it scans every ancestor to the filesystem root while permitting only the standard macOS `/var`, `/tmp`, and `/etc` aliases to their matching `/private` directories; application-controlled ancestor symlinks still fail. It returns named `DeploymentArtifact` values with exact SHA-256 hashes. Rendering must be deterministic and must not call cloud APIs, apply infrastructure, or execute work.

`IDeploymentTarget.VerifyAsync` receives the freshly rendered result and either `Shadow` or `Owned` parity mode. Verification may inspect deployed state read-only. It must not accept or start an execution or alter traffic.

`GcloudCommandRunner` resolves `gcloud` directly on Unix-like hosts. On Windows it uses the Cloud SDK's `gcloud.cmd` launcher through a bounded `cmd.exe /d /c` dispatch that rejects whitespace, control characters, and shell metacharacters in every token before process start. Provider-generated verification arguments satisfy that contract; free-form shell fragments do not.

`DeploymentArtifact.Create` accepts one portable file name only. It rejects directory segments, control characters, Windows-invalid punctuation and device names, and trailing dots or spaces before any filesystem write. `DeploymentArtifactBundleWriter` validates the complete file set, rejects portable case collisions and the reserved ownership-marker name, rejects a non-owned or mixed directory, writes to a same-parent staging directory, and swaps the complete bundle only after all bytes have been written. Give each target a dedicated output directory; do not place unrelated files there or edit generated artifacts.

Migration-job `Environment` entries are serialized into review artifacts and must contain non-secret configuration only. Secret-shaped names, including password, token, credential, connection-string, database-URL, API-key, private-key, and access-key variants, fail with `ASDEPLOY115`; use `SecretBinding` instead. Treat command arguments and environment values as artifact-visible inputs even when their names look harmless.

## GCP binding profile v1

The closed JSON object contains:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Must be `1.0`. |
| `environment` | Exact Aspire environment selected with `--environment`. |
| `project`, `region` | Existing GCP location; AppSurface does not create it. |
| `jobs` | Logical job id to existing physical Cloud Run Job name. |
| `cloudSqlInstanceConnectionName` | Existing `project:region:instance` attachment. |
| `network` | Existing Direct VPC network, subnetwork, and `PRIVATE_RANGES_ONLY` or `ALL_TRAFFIC` egress. |
| `serviceAccounts` | Logical identity id to existing runtime service-account email. |
| `secrets` | Logical secret id to physical Secret Manager id and explicit `latest` version mode. |

Unknown, duplicate, secret-bearing, malformed, environment-mismatched, symbolic-link, and Terraform-template-shaped inputs are rejected. Logical map keys must be canonical `DeploymentLogicalId` values. Project, region, Job, Cloud SQL, network, subnet, service-account, and secret identifiers use anchored provider-specific formats rather than a generic token allowlist. The profile stores identifiers only. It must never contain a connection string, password, token, secret value, `${...}`, or `%{...}` expression.

## Generated artifacts

- `deployment-intent.v1.json` is the portable review contract. The Aspire adapter owns its canonical bytes: a provider may echo the exact artifact or omit it, but a contradictory provider copy is rejected before any bundle is written.
- `gcp-cloud-run-migration.tf.json` declares only `google_cloud_run_v2_job.appsurface_migration` and references externally provisioned foundations.
- `gcp-cloud-run-migration.plan.json` records project, region, exact Terraform resource addresses, required capabilities, logical-to-physical mapping, normalized expected parity, source revision, generator version, and hashes linking the intent and Terraform bytes. Verification cross-checks every operational expected field against the hashed Terraform before any cloud command.

The consuming OpenTofu root owns its exact OpenTofu and Google provider pins. Do not initialize or apply the generated file as a disconnected state root. Copy it into the existing reviewed root before `tofu init` and keep apply/state authority in CI.

## Parity modes

Shadow parity compares operational configuration while the legacy deployment command remains the only writer: image, command/arguments, sorted non-secret environment settings, task bounds, Direct VPC, Cloud SQL, secret reference/version, and service account. It intentionally ignores AppSurface provenance labels.

Cloud Run runtime names (`CLOUD_RUN_*`, `K_*`, and `PORT`) and `GOOGLE_APPLICATION_CREDENTIALS` cannot be declared as application settings. Use the runtime metadata as supplied and the explicit service identity for Google API access.

Owned parity additionally requires canonical environment and source-revision labels after state import and writer cutover. Effective inherited or custom IAM remains `not-independently-verified`; a public `allUsers` or `allAuthenticatedUsers` Job principal is always a failure.

## Exact Cloud Run execution observation

The GCP execution observer consumes the same immutable `DeploymentRenderResult` produced by the three-artifact render contract. Callers construct artifacts with `DeploymentArtifact.Create(fileName, bytes)` and pass the complete result to `CloudRunExecutionObservationRequest`; the package intentionally does not add a public bundle loader. The CLI is the trusted-bundle loader: it admits exactly the three canonical files and optional ownership marker, rejects symlinks/reparse points and unexpected entries, and enforces a 4 MiB limit per artifact.

Observation JSON is limited to depth 64 and 4096 direct entries per object or array, inclusive, within the artifact and provider byte caps. These are local admission bounds; exceeding them returns safe Unknown and does not reduce configuration requirements. Legacy configuration verification retains its existing admission behavior.

Use `GcpCloudRunExecutionObserver.Create()` for production. Tests and hosts that need deterministic time inject `IBoundedGcloudExecutionReader` and `TimeProvider` through `new GcpCloudRunExecutionObserver(reader, timeProvider)`. The bounded reader exposes complete output, nonzero exit, tool unavailable, output limit, invalid encoding, deadline, read failure, and cleanup-unconfirmed outcomes. No unbounded runner fallback exists.

The request requires a logical Job id, a canonical full execution resource (`projects/{project-or-number}/locations/{region}/jobs/{physical-job}/executions/{execution}`), and finite positive timeout/interval values with interval no greater than timeout. The observer matches a project ID or explicitly configured project number literally; it never resolves aliases or infers equivalence. It never selects latest, lists executions, uses a name prefix, reads today's Job to fill gaps, submits/cancels/retries work, or changes cloud state. The request's timeout is one monotonic budget covering validation, reads, polling, and cleanup. The caller owns submission correlation, acceptance, recovery, and any later retry.

`GcpCloudRunBindingProfile` retains its existing positional constructor and adds `ProjectNumber` only as an optional non-positional `init` property. When present it is a canonical positive decimal project number; when absent, only the configured project ID is admitted. This additive shape preserves existing source construction while allowing exact ID-or-number resource matching. Google documents project-number resource naming in the [Cloud Run execution resource schema](https://cloud.google.com/run/docs/reference/rest/v2/projects.locations.jobs.executions).

`CloudRunExecutionObservation` is deliberately small: logical id, selected resource, `CloudRunExecutionState`, `ConfigurationMatched`, UTC `ObservedAt`, and fixed diagnostic code. Its versioned CLI JSON is exactly:

```json
{"schemaVersion":"1.0","logicalJobId":"schema-preparation","executionResourceName":"projects/my-project/locations/us-central1/jobs/schema-preparation/executions/exec-20261024","state":"Succeeded","configurationMatched":true,"observedAt":"2026-10-24T15:04:05.0000000+00:00","diagnosticCode":"ASDEPLOY183"}
```

It has no rich provider fields, raw JSON, stderr, command arguments, secret/environment values, credentials, provider free text, or exception text. `ConfigurationMatched` is true only after all required captured fields match. A provider read failure is `Unknown`; it cannot be promoted from an earlier `Running` response. Caller cancellation is propagated by the API and maps to CLI exit 130.

### Evidence shape and Cloud SQL proof

The v1 admitted evidence is one gen2 container with the execution identity, UID, current execution generation, complete environment inventory, task settings, conditions, counters, network annotations, and the `run.googleapis.com/cloudsql-instances` annotation. That annotation represents the implicit Cloud SQL socket configuration for v1. The v1 contract intentionally has no explicit volume/mount inventory: it proves declared attachment/configuration only; filesystem permissions, IAM, socket creation, database reachability, and application use remain outside this observer. Any additional v1 volumes or mounts are unsupported evidence and return `Unknown`.

The v2 admitted evidence has typed Cloud SQL volume `cloudSqlInstance.instances` and a matching container mount at `/cloudsql`. It proves the typed attachment and socket projection. Both shapes require returned `cloud.googleapis.com/location` and `run.googleapis.com/job` labels matching the selected region and parent Job; a matching full resource path does not replace these independent required fields. Both shapes compare immutable digest image, command, ordered arguments, complete environment and secret reference/version, task count, parallelism, retries, timeout, service identity, network/subnetwork/egress, execution UID, same-execution generation, overall condition, completion marker, and counters. Missing R facts, unproven D defaults, contradictory fields, stale generation, changed UID, or unexplained execution-affecting additions return `Unknown`.

The [reviewed mapping](../docs/plans/issue-891-provider-mapping.md) and [exact-version manifest](ForgeTrust.AppSurface.Deployment.GcpCloudRun.Tests/Fixtures/ExecutionEvidence/v1/compatibility.json) record owner-authorized offline technical acceptance for the recovered v1 representation and gcloud 576.0.0 formatter replay on Darwin arm64. V2 typed normalization is verified separately with synthetic tests. Neither source is live provider certification or a supported version range; synthetic fixtures are never relabeled provider-observed.

For v1, omitted zero counters may be treated as zero only when the exact source shape and SDK/CLI replay document that omission; every positive task count, running/succeeded/failed/cancelled/retried counter, UID, generation, completion marker, and identity fact otherwise requires explicit evidence. The v1 replay source must be bounded and sanitized. For v2, `egress`, `reconciling`, `executionEnvironment`, and all execution counters are explicit required evidence; no omitted provider default is inferred. No global v1/v2 compatibility claim is made.

The CLI bundle path is strict: the directory and every path component must be free of symlinks/reparse points. Callers on macOS must provide the physical `/private/var`, `/private/tmp`, or `/private/etc` path when applicable; the CLI does not accept `/var`, `/tmp`, or `/etc` aliases.

### Supported completion reasons

These rules apply only after exact identity, configuration, UID and current-generation checks pass. Terminal outcomes additionally require a valid RFC3339 completion timestamp, no running tasks and consistent task totals. V2 requires explicit `reconciling`; a terminal response cannot be reconciling.

| Overall condition | v1 reason | v2 reason union | Result with consistent counters |
| --- | --- | --- | --- |
| Success | Absent or `Completed` | All reason fields absent | Succeeded, all tasks succeeded |
| Failure | `NonZeroExitCode` or `NON_ZERO_EXIT_CODE` | `executionReason: NON_ZERO_EXIT_CODE` | Failed, failed tasks and no cancelled tasks |
| Failure | `Cancelled`, `ExecutionCancelled` or `CANCELLED` | `executionReason: CANCELLED` | Cancelled, at least one cancelled task |
| Nonterminal | `Running`, `Pending`, `ResourcesAvailable`, `Retrying`, `CANCELLING` or `Starting` | Absent, `executionReason: CANCELLING` or `DELAYED_START_PENDING`, or common `reason: WAITING_FOR_OPERATION`, `IMMEDIATE_RETRY` or `POSTPONED_RETRY` | Running, no completion marker or all-tasks-success contradiction |
| Other or contradictory | Unsupported, missing required, malformed or conflicting | Multiple union members, unsupported members/enums, deleted or provider polling-error evidence | Unknown |

V2 reason values must occur in the correct union member; an arbitrary reason cannot promote success, workload failure or Running. This follows Google's [condition and reason schema](https://docs.cloud.google.com/run/docs/reference/rest/v2/Condition). `CANCELLING` is nonterminal and differs from caller cancellation. A failure without recognized workload-failure evidence stays Unknown.

### Diagnostic anchors

The execution contract reserves the following fixed anchors. Each result carries only the code; this table supplies the safe problem, cause, fix, recovery limit, and documentation anchor.

| Code | Safe problem and cause | Fix and recovery limit |
| --- | --- | --- |
| <a id="asdeploy170"></a>`ASDEPLOY170` | Invalid identifier, timer, or project-number admission. | Correct local input; no provider call. |
| <a id="asdeploy171"></a>`ASDEPLOY171` | Invalid, incomplete, oversized, or ambiguous three-artifact bundle. | Republish a bounded bundle; no provider call. |
| <a id="asdeploy172"></a>`ASDEPLOY172` | Project/region/Job/execution/image identity mismatch. | Select the caller-owned exact execution; no alias lookup or current-Job fallback. |
| <a id="asdeploy173"></a>`ASDEPLOY173` | Execution UID changed between reads. | Stop polling and investigate identity; never continue the same observation. |
| <a id="asdeploy174"></a>`ASDEPLOY174` | Malformed, duplicate, conflicting, or impossible evidence. | Correct provider/fixture shape; return `Unknown` until positively proven. |
| <a id="asdeploy175"></a>`ASDEPLOY175` | Unsupported shape, annotation, mount, default, or state token. | Use an accepted compatibility shape; no guessed normalization. |
| <a id="asdeploy176"></a>`ASDEPLOY176` | Stale generation, deletion, or terminal contradiction. | Re-read only as a new caller-owned observation after the cause is resolved. |
| <a id="asdeploy177"></a>`ASDEPLOY177` | Captured configuration mismatch. | Reconcile the bundle or select the intended execution; no workload retry. |
| <a id="asdeploy178"></a>`ASDEPLOY178` | `gcloud` unavailable. | Install/configure the caller-owned tool and `PATH`; no package-side installation. |
| <a id="asdeploy179"></a>`ASDEPLOY179` | Provider/read failure. | Investigate local/provider availability; no stale-state promotion. |
| <a id="asdeploy180"></a>`ASDEPLOY180` | Output limit exceeded or invalid UTF-8. | Use the fixed command and bounded provider output; never expose/truncate JSON. |
| <a id="asdeploy181"></a>`ASDEPLOY181` | No complete evidence before the monotonic deadline. | Increase caller budget only deliberately; no deadline extension inside the observer. |
| <a id="asdeploy182"></a>`ASDEPLOY182` | Clean deadline with last matching `Running` evidence. | Caller decides whether to observe again; no automatic retry. |
| <a id="asdeploy183"></a>`ASDEPLOY183` | Matching terminal success. | Continue caller acceptance; observer performs no mutation. |
| <a id="asdeploy184"></a>`ASDEPLOY184` | Matching workload failure. | Caller owns recovery; observer does not retry the workload. |
| <a id="asdeploy185"></a>`ASDEPLOY185` | Matching remote cancellation. | Caller owns recovery/correlation; API cancellation remains distinct. |
| <a id="asdeploy186"></a>`ASDEPLOY186` | Caller cancellation. | Propagate cancellation after bounded local cleanup; CLI exits 130 and does not retry or mutate. |
| <a id="asdeploy187"></a>`ASDEPLOY187` | Local process or stream cleanup unconfirmed. | Treat as `Unknown`; no success claim or unbounded cleanup. |
| <a id="asdeploy188"></a>`ASDEPLOY188` | Output delivery failed after the safe writer latched failure. | Suppress further stdout/stderr writes; CLI returns 2 except caller cancellation, which returns 130. |
| <a id="asdeploy189"></a>`ASDEPLOY189` | Unexpected internal observation failure. | Return safe `Unknown`; expose only this code and the canonical documentation link; do not expose exception/provider text. |

All local cleanup is bounded; no diagnostic contains provider free text. In text mode, failures write only the fixed diagnostic code, safe problem/cause/fix, recovery limit, and this canonical documentation link to stderr; stdout remains free of provider traces. See the [GCP package execution guide](./ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md#exact-cloud-run-execution-observation) and [adoption guide](adoption.md#observe-an-existing-execution).

The neutral two-consumer walkthrough is in the [Cloud Run execution consumers example](../examples/cloud-run-execution-consumers/README.md). It demonstrates separate `schema-preparation` and `catalog-refresh` identities and swapped-execution rejection without claiming production migration adoption.
