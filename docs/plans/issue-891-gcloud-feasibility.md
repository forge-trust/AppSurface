# Issue 891: initial gcloud feasibility assessment

Initial status on 2026-10-24: **uncertified; integration gate not passed**.
This assesses the first gate of the [approved plan](../designs/issue-891-cloud-run-execution-observation.md#minimum-evidence-contract-and-compatibility-gate). It does not assert that v1 is impossible, that a schema-shaped fixture proves provider behavior, or that a live execution has been read.

**Later evidence:** the user-directed Skoolit worktree/archive search recovered real execution-shaped readbacks paired with intended Job definitions. See the [recovered-evidence assessment](issue-891-skoolit-evidence.md). It supersedes the missing-capture premise below: a new cloud read is no longer required merely to obtain an example. Numeric project identity and absent explicit Cloud SQL mounts require design reconciliation; compatibility is still uncertified. The remaining sections preserve the initial investigation record.

## Independently established facts

- Repository source: `c579bc7920e35c8c883891400ea3065319bf769e`.
- An isolated non-cloud `gcloud version --format=json` exited 0 and reported Google Cloud SDK **576.0.0**; core and beta components report **2026.07.10**. Candidate platform: Darwin arm64. No credentials or configured project were inspected.
- Installed `lib/surface/run/jobs/executions/describe.py`, lines 77–86, invokes `GetConnectionContext(..., version_override='v1')` and `GetExecution`. Its beta/alpha class inherits this implementation. Adjacent `describe.yaml` is an `is_python_command: true` metadata/hints file, not a v2 replacement command.
- Installed `lib/googlecloudsdk/api_lib/run/execution.py` uses `metadata.labels["run.googleapis.com/job"]` for the parent Job. `k8s_object.py` defines `cloud.googleapis.com/location` and reads it for the region. Consequently, absence of a top-level region field does **not** prove that v1 lacks region evidence.
- The [v1 ObjectMeta contract](https://docs.cloud.google.com/run/docs/reference/rest/v1/ObjectMeta) permits a project ID or number in `namespace` and lists execution annotations for network interfaces, egress, secrets and Cloud SQL. Project-number/ID equivalence must not be guessed; the existing binding profile requires a project ID.
- The [v1 Execution contract](https://docs.cloud.google.com/run/docs/reference/rest/v1/namespaces.executions) exposes captured configuration plus status generation, completion conditions and task counters. Its overall completion condition is `Completed`.
- The [v2 Execution contract](https://docs.cloud.google.com/run/docs/reference/rest/v2/projects.locations.jobs.executions) has direct name/parent, generation, task-template and status carriers. The installed describe implementation does not establish that gcloud can emit that shape.

The [documented describe command](https://docs.cloud.google.com/sdk/gcloud/reference/run/jobs/executions/describe) supports exact selection and output formatting. Selecting a resource in argv is not returned evidence of its identity. The [execution-management guide](https://docs.cloud.google.com/run/docs/managing/job-executions) also documents a v2 REST GET; changing the approved production adapter to use it would require an affected design/engineering review, not an implicit fallback.

## Required-fact worksheet status

The following is a feasibility inventory, **not** either completed neutral worksheet. No observed values or positive fixture hashes exist yet. Here `task` means `spec.template.spec` for v1 and `template` for v2. R and D retain the definitions in the approved plan; this assessment does not promote any R fact to a default.

| Required fact | v1 candidate | v2 candidate | Outstanding evidence |
| --- | --- | --- | --- |
| Exact execution / project / region / parent | `metadata.name`, `namespace`, location and Job labels | `name`, `job` | Exact emitted identity and label values; reject ID/number ambiguity and conflicting carriers. |
| UID | `metadata.uid` | `uid` | Nonempty valid execution UID, continuity across polls. |
| Same-execution generation | `metadata.generation`, `status.observedGeneration` | `generation`, `observedGeneration` | Current-generation representation and equality; no Job generation substitution. |
| Immutable image | `task.containers[0].image` | same task-relative field | Complete immutable reference preserved in actual output. |
| Command | `task.containers[0].command` | same | Exact ordered declared command. |
| Arguments | `task.containers[0].args` | same | Exact order; documented/fixture-proven omission rule only for an empty expectation. |
| Full environment inventory | `task.containers[0].env` | same | Complete values and names, no unsupported override or extra entry. |
| Secret delivery/reference/version | `valueFrom.secretKeyRef` plus alias annotation if used | `valueSource.secretKeyRef` | Alias resolution, destination and exact reference/version; no secret material. |
| Task count | `spec.taskCount` | `taskCount` | Explicit effective positive count. |
| Parallelism | `spec.parallelism` | `parallelism` | Explicit effective positive value, not inferred from task count. |
| Retries | `task.maxRetries` | same | Explicit valid number; omission is Unknown even when expected zero. |
| Timeout | `task.timeoutSeconds` | `task.timeout` | Explicit exact normalized duration. |
| Service account | `task.serviceAccountName` | `task.serviceAccount` | Exact declared identity, no project-default inference. |
| Direct VPC network/subnet | execution `network-interfaces` annotation | `task.vpcAccess.networkInterfaces` | Actual annotation JSON and independently supported reference normalization. |
| Egress | execution egress annotation | `task.vpcAccess.egress` | Exact mode; only the already permitted, proved v2 omitted-private default may be D. |
| Cloud SQL attachment/socket | execution Cloud SQL annotation plus verified mount evidence | `task.volumes[].cloudSqlInstance.instances` plus container mount | Actual socket-projection representation; do not invent an explicit v1 Cloud SQL volume. |
| Overall condition | `status.conditions` | `conditions` | Supported overall state/reason table, not a successful intermediate condition. |
| Completion marker | `status.completionTime` | `completionTime` | Required for terminal outcomes; absent for clean nonterminal evidence. |
| Five task counters | `status.*Count` | root `*Count` | Valid consistent counters and independently proved omitted-zero semantics. |
| Reconciliation | current-generation overall-condition semantics | `reconciling` | Supported current-state combinations and documented omission behavior. |
| Complete configuration inventory | task/container fields plus execution-affecting annotations | task/container fields | Reject unsupported additions, containers, mounts and malformed/contradictory fields. |

The [v1 TaskSpec reference](https://docs.cloud.google.com/run/docs/reference/rest/v1/TaskSpec), [v2 VPC reference](https://docs.cloud.google.com/run/docs/reference/rest/v2/VpcAccess) and [v2 Volume reference](https://docs.cloud.google.com/run/docs/reference/rest/v2/Volume) explain candidate carriers. Schema availability does not establish field presence, normalization or omission behavior in an exact gcloud response.

The [v1 Volume schema](https://docs.cloud.google.com/run/docs/reference/rest/v1/Volume) has no typed `cloudSqlInstance` field and reserves the volume name `cloudsql`. Its [container mount schema](https://docs.cloud.google.com/run/docs/reference/rest/v1/Container#VolumeMount) can carry a name/path. This confirms why a synthetic v2-style Cloud SQL volume must not be inserted into a v1 fixture; it does not establish whether the real v1 response exposes the implicit socket projection. The correct execution-relative task path is `spec.template.spec`, with no second execution-template nesting.

## Neutral consumer proof remains incomplete

Both `schema-preparation` and `catalog-refresh` must have independent expected bundles, distinct execution/image identities, positive evidence and swapped/malformed negative controls. Neither currently has a complete worksheet. No private application name or deployment detail is needed in committed fixtures: an authorized raw capture can be sanitized consistently into neutral evidence while preserving its shape, relationships and omission behavior. Sanitization must not insert missing evidence.

No `compatibility.json` is certified. Its eventual version, platform, argv, fixture hashes, state table and named maintainer acceptance must bind the real candidate. The reviewer's initial claim that v1 region was absent was corrected after checking the SDK's label carrier; it must not be reused as a decisive rejection.

## Minimum next evidence

An operator can provide the exact canonical name of an **existing** execution, the matching published bundle directory, and explicit read-only authorization. The intended read remains:

```text
gcloud run jobs executions describe <execution-id>
  --project <validated-project-id> --region <validated-region>
  --format=json --quiet
```

Capture stdout/stderr to restricted local evidence files with the approved 1 MiB/64 KiB streaming caps and a finite deadline; do not print provider output in chat or commit a raw private response. Validate the returned identity separately. No list/latest lookup, submission, cancellation, retry, Job read or cloud mutation is part of this request.

One real payload can resolve representation questions; it does not by itself satisfy both neutral worksheets, all negative controls, native cleanup proof or package adoption. If evidence is unavailable or a required fact cannot be proved, the integration gate remains closed. A direct-v2 adapter, a smaller evidence profile or new project-alias lookup would change the approved design and needs an explicit decision plus affected review.

## Reproducibility record

Full version output and SHA-256 values for six inspected SDK source files are saved in ignored local evidence at `.gstack/tmp/make-it-so-891/feasibility-evidence.json`. The describe implementation hash is `0db5a2ed09465e1f5bdfa980c2ffc6665f9c0001cfadbafc40b4f9130495477a`; its adjacent metadata file hash is `653a4fb242fa3c85309ccff1023c40398fa441d5d2c46429fc388ca270e96916`. These identify local source inspection only; no provider-observed fixture or compatibility certification is claimed.

A continuation rechecked the current branch and searched the repository's deployment, tests and examples for captured execution JSON/YAML and the relevant provider annotations. No reusable execution capture was found. Targeted searches of official Google documentation and GoogleCloudPlatform examples supplied configuration/schema material, not an exact-version execution response that closes the gate. Google's [Cloud Run known issues](https://docs.cloud.google.com/run/docs/known-issues) also distinguishes Cloud SQL socket behavior for multiple containers; the approved single-container inventory requirement must remain in place. This does not change the missing-evidence classification or authorize a provider read.
