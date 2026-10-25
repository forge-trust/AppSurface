# ForgeTrust.AppSurface.Deployment.GcpCloudRun

This provider compiles AppSurface migration-job intent into reviewable Google Cloud Run v2 Job Terraform/OpenTofu JSON and verifies deployed configuration through read-only `gcloud` commands.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

This AppHost-oriented package follows the coordinated AppSurface release policy.
Before using a prerelease build in an AppHost, development, or test environment,
check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for publication status,
compatibility guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Operational boundary

Publishing is pure artifact generation. It does not authenticate to Google Cloud, resolve secret values, apply infrastructure, execute a job, or change traffic. Foundations such as the VPC, subnet, service account, Cloud SQL instance, and Secret Manager secrets remain externally provisioned.

Use `GcpCloudRunDeploymentTarget.Create()` from an AppSurface Aspire deployment target. The binding profile is a checked-in, non-secret, schema-versioned JSON file beneath the AppHost. The trusted-root loader rejects symlinks from that AppHost boundary through the file; the rootless loader scans to the filesystem root while allowing only the standard macOS `/var`, `/tmp`, and `/etc` aliases to their matching `/private` directories. Unknown properties, other symlinks, environment mismatches, malformed provider identifiers, Terraform template expressions, and fields shaped like secret values are rejected.

Verification validates hashes, resource addresses, capabilities, and every duplicated operational field across the portable intent, provider plan, and Terraform artifact before invoking `gcloud`. It also inspects the live IAM policy and rejects public principals. A coordinated plan/Terraform rewrite that contradicts intent fails closed. Shadow mode compares operational configuration, including declared non-secret environment settings, while a legacy writer remains authoritative. Owned mode additionally checks canonical AppSurface provenance labels. Undefined parity modes fail rather than silently behaving like shadow mode.

The target applies Cloud Run's provider boundary before rendering: names containing `=`, names beginning with `X_GOOGLE_`, and runtime-owned names are rejected, and the secret-backed connection variable counts toward Cloud Run's 1,000-variable container limit.

The default verifier launches `gcloud` directly on Unix-like hosts. Windows uses the Cloud SDK's `gcloud.cmd` launcher through `cmd.exe /d /c`, but only after rejecting empty arguments, whitespace, control characters, and command-shell metacharacters. Keep provider identifiers canonical; do not pass free-form shell fragments to `IGcloudCommandRunner`.

## Exact Cloud Run execution observation

`GcpCloudRunExecutionObserver.Create()` creates the production observer with the fixed, bounded `gcloud` reader and `TimeProvider.System`. For tests or a caller-owned clock, construct `new GcpCloudRunExecutionObserver(reader, timeProvider)` with an `IBoundedGcloudExecutionReader`; the observer never accepts or adapts the legacy unbounded runner. The observer is read-only: it does not submit, cancel, retry, or mutate a Job.

The public request is `CloudRunExecutionObservationRequest(Deployment, LogicalJobId, ExecutionResourceName, Timeout, PollInterval)`. `Deployment` is the trusted, complete three-artifact `DeploymentRenderResult`; validate it with the existing `DeploymentArtifact.Create`/bundle writer path and pass the immutable result to the observer. There is no public bundle loader in this package. `Timeout` and `PollInterval` are finite and positive, `PollInterval <= Timeout`, and the timeout is bounded to the supported timer range. The API has no duration defaults. Caller cancellation throws `OperationCanceledException`; invalid non-null input returns a safe `Unknown` observation without provider access.

Use a full selected resource such as `projects/my-project/locations/us-central1/jobs/schema-preparation/executions/exec-20261024`. The project segment may be the exact configured project ID or its configured numeric project number when the binding profile supplies both; the observer compares the literal value and performs no ID/number lookup or inference. Region, physical Job, and execution name must all match. `latest`, prefixes, list operations, and current-Job substitution are rejected.

`GcpCloudRunBindingProfile.ProjectNumber` is an optional non-positional `init` property added to the existing profile shape; the existing positional constructor remains source-compatible. If supplied, it is a canonical positive decimal number and is accepted only as the literal alternate project segment for identity matching. See Google's [Cloud Run execution resource schema](https://cloud.google.com/run/docs/reference/rest/v2/projects.locations.jobs.executions) for the provider resource naming model.

The result contains only `LogicalJobId`, the validated execution resource (or empty safe fields), `State`, `ConfigurationMatched`, UTC `ObservedAt`, and a fixed `ASDEPLOY170`–`ASDEPLOY189` code. It contains no raw provider JSON, stderr, command arguments, environment values, credentials, provider messages, or exception text. `ConfigurationMatched` is true only after every required captured operational field is positively matched. A matching configuration with workload failure is `Failed`; a provider read failure is `Unknown`.

### Captured configuration and provider shapes

The v1 admitted shape is a single-container gen2 execution. Its `run.googleapis.com/execution-environment` annotation must be `gen2`; the `run.googleapis.com/cloudsql-instances` annotation is the implicit Cloud SQL socket configuration. v1 has no explicit volume or mount inventory in this contract, so the observer proves the declared connection annotation and configured environment/reference, while filesystem permissions, IAM access, database reachability, and actual socket use remain application/provider acceptance concerns. Additional v1 volumes or mounts are rejected as unsupported evidence.

The v2 admitted shape uses typed `template.volumes[].cloudSqlInstance.instances` and a matching container `volumeMounts` entry at `/cloudsql`. It therefore proves the typed Cloud SQL attachment and socket projection. Network interfaces, egress, service account, command, ordered arguments, immutable digest image, complete environment inventory, task bounds, retries, timeout, UID, execution generation, conditions, completion time, and counters are compared exactly. Missing required facts, stale generation, changed UID, conflicting fields, unsupported defaults, or unexplained execution-affecting additions produce `Unknown`.

The [reviewed offline mapping](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-891-provider-mapping.md) records exact gcloud 576.0.0 source/formatter replay on Darwin arm64, fixed command arguments, neutral fixtures and hashes in the [compatibility manifest](https://github.com/forge-trust/AppSurface/blob/main/Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun.Tests/Fixtures/ExecutionEvidence/v1/compatibility.json). The owner authorized technical acceptance of this mapping and its executable checks. The v2 typed parser has separate synthetic tests. These are scoped offline proofs; no live provider certification or version range is claimed.

For v1, omitted zero counters are admissible only when the exact source shape and bounded SDK/CLI replay document that omission; positive task count, all counters, UID, generation, completion marker, and identity facts otherwise require explicit evidence. For v2, `egress`, `reconciling`, `executionEnvironment`, and all execution counters are explicit required evidence. No global v1/v2 compatibility claim is made. The CLI path admission rejects symlinks/reparse points in the directory and every component; on macOS callers must provide physical `/private/var`, `/private/tmp`, or `/private/etc` paths rather than aliases.

### CLI and offline quickstart

The CLI reads one trusted published bundle directory containing `deployment-intent.v1.json`, `gcp-cloud-run-migration.tf.json`, and `gcp-cloud-run-migration.plan.json` plus the optional ownership marker. It rejects symlinks/reparse points, unexpected entries, incomplete bundles, and artifacts over 4 MiB. It validates with `DeploymentArtifact.Create` and uses the same observer as library callers; it does not evaluate an AppHost.

Observation JSON is limited to depth 64 and 4096 direct entries per object or array, inclusive, within the artifact and provider byte caps. These are local admission bounds; exceeding them returns safe Unknown and does not reduce configuration requirements. Legacy configuration verification retains its existing admission behavior.

```sh
appsurface deployment gcp-cloud-run observe-execution \
  --bundle-dir ./artifacts/schema-preparation \
  --job schema-preparation \
  --execution projects/my-project/locations/us-central1/jobs/schema-preparation/executions/exec-20261024 \
  --timeout 60s --poll-interval 2s --json
```

`--timeout` defaults to `60s` and `--poll-interval` to `2s`; the API itself has no defaults. `--json` emits exactly one versioned object with `schemaVersion`, `logicalJobId`, `executionResourceName`, `state`, `configurationMatched`, RFC 3339 UTC `observedAt`, and `diagnosticCode`. It intentionally has no rich provider fields. The command uses one safe writer: cancellation attempts one safe `Unknown`/false envelope only before any output failure, suppresses all further output after the first write/flush failure, and returns 130 for caller cancellation even if the envelope cannot be delivered. Ordinary output failure returns 2.

Exit `0` means `Succeeded` with matched configuration; `1` means matched `Failed` or `Cancelled`; `2` means invalid input, `Unknown`, mismatch, provider/output/cleanup failure; `3` means the last matching `Running` evidence at a clean deadline; `130` means caller cancellation. The command never retries a workload, submits or cancels an execution, or exposes a general executable option.

The offline neutral fixtures for `schema-preparation` and `catalog-refresh` prove parser and classifier behavior and swapped-execution rejection. A separately authorized provider read, if performed, uses the caller's existing `gcloud` credentials and `PATH`; this guide makes no live-read or compatibility claim. Do not place credentials, project secrets, or raw provider output in fixtures. Environment variable names may be documented, but their values remain outside this package's documentation.

### Execution diagnostics

Every code has a stable problem, cause, fix, recovery limit, and documentation anchor. `ASDEPLOY170` invalid identifier/timer/project-number admission: correct local input; no provider call. `ASDEPLOY171` invalid or incomplete three-artifact bundle: republish the bounded bundle; no provider call. `ASDEPLOY172` project, region, Job, image, or configuration identity mismatch: select the caller-owned execution matching the bundle; no alias lookup or current-Job fallback. `ASDEPLOY173` UID changed across reads: stop and investigate execution identity; never continue polling.

`ASDEPLOY174` malformed, duplicate, conflicting, or impossible evidence; `ASDEPLOY175` unsupported shape, annotation, mount, default, or state token; `ASDEPLOY176` stale generation, deletion, or terminal evidence contradiction; `ASDEPLOY177` captured configuration mismatch; and `ASDEPLOY178` unavailable `gcloud` tool. These return `Unknown`/false and require corrected evidence or local installation/configuration; they never retry mutation.

`ASDEPLOY179` provider/read failure, `ASDEPLOY180` output limit or invalid UTF-8, `ASDEPLOY181` unknown deadline, and `ASDEPLOY187` unconfirmed local process/stream cleanup are bounded failures. They return `Unknown`, expose no provider text, and permit a caller-owned later read only as a new observation. `ASDEPLOY182` is clean running-deadline evidence; `ASDEPLOY183`, `ASDEPLOY184`, and `ASDEPLOY185` are terminal success, workload failure, and remote cancellation respectively. `ASDEPLOY186` is caller cancellation: the API propagates it after bounded cleanup and the CLI exits 130 without retry or mutation. `ASDEPLOY188` is latched output-delivery failure: suppress further writes and return 2, except caller cancellation remains 130. `ASDEPLOY189` is an unexpected internal observation failure: return safe `Unknown` with only the fixed code and canonical documentation link. Text-mode failures write only fixed safe problem/cause/fix/recovery guidance and that link to stderr; no provider trace or exception text is rendered.

See the [deployment reference](https://github.com/forge-trust/AppSurface/blob/main/Deployment/reference.md#exact-cloud-run-execution-observation), [adoption guide](https://github.com/forge-trust/AppSurface/blob/main/Deployment/adoption.md#observe-an-existing-execution), and [CLI command reference](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-deployment-gcp-cloud-run-observe-execution) for the complete contract and POSIX CI recipe.

The two neutral consumer workflows are documented in the [Cloud Run execution consumers example](https://github.com/forge-trust/AppSurface/blob/main/examples/cloud-run-execution-consumers/README.md).

For the shortest offline walkthrough, use the [CLI quickstart](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-deployment-gcp-cloud-run-observe-execution), which includes the bounded published-bundle reconstruction and the captured neutral fixture result.
