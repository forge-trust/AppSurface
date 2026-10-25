<!-- /autoplan restore point: "/Users/andrew/.codex/worktrees/ea75/runnable__jupiter/.gstack/tmp/autoplan/main-autoplan-restore-20261024-135210.md" -->
## URGENT, outside this plan

None.

Autoplan amendments: APPROVED by the owner on 2026-10-24 via `Rec` for run `main-20261024-135210`, gate revision 1 (`rec_hash=2592f8063bfc`). Full configuration proof (d1a), the code-only diagnostic contract with its documented CAP6 tradeoff (d2a), and the reviewed plan (p1a) are accepted. The original office-hours baseline remains approved. This is planning approval; provider compatibility and implementation evidence remain outstanding.

### Execution amendment authorized on 2026-10-24

The owner directed: “Update the plan using the Skoolit evidence, reconcile those two assumptions without weakening configuration proof, and continue make-it-so.” The following rules supersede the original ID-only/explicit-v1-mount assumptions and their historical review copies. All other full-parity, identity, byte/time/cancellation, diagnostic, documentation, test and packaging obligations remain required. Source evidence is in [the recovered-evidence assessment](../plans/issue-891-skoolit-evidence.md).

1. **Declared project identity, no discovery:** retain `Project` as the existing project ID. Add optional `ProjectNumber` as a non-positional init property on `GcpCloudRunBindingProfile` and optional `projectNumber` in its closed JSON schema. It is a canonical positive decimal string within signed 64-bit range, without leading zeroes. Existing constructor calls, ID grammar, old profiles and old artifacts remain valid and omit this field. Render the number into optional plan metadata and duplicate it in inert Terraform `locals.appsurface_execution_identity` together with the project ID; hash and cross-validate both values before provider access. Portable deployment intent remains provider-neutral. The observer derives this expectation internally; its public request/result and CLI arguments stay unchanged.
2. **Returned identity:** canonical resource project segments may equal only the bundle's exact ID or explicitly declared number. A v1 numeric namespace must equal that number; a textual namespace must equal the ID. A full returned path, namespace, self-link, region label and parent label must not contradict each other. Require returned `cloud.googleapis.com/location`, `run.googleapis.com/job`, exact execution ID, UID and same-Execution current generation. Missing number for numeric evidence is Unknown. Never infer aliases from service accounts, Cloud SQL names, argv, name prefixes or a current Job; add no Resource Manager call. The binding is a caller-declared expected identity, not provider attestation or publisher authentication.
3. **Full semantic Cloud SQL proof:** v1's admitted representation is one gen2 container, an exact singleton `run.googleapis.com/cloudsql-instances` connection name, and no explicit task volumes/container volume mounts, extra container, VPC connector, or contradictory projection. The documented built-in single-container Cloud SQL connection supplies the `/cloudsql/{connection-name}` configuration; its absence as an explicit v1 volume field is not missing configuration. Verify the whole representation and all other operational settings. Missing/malformed/duplicate annotations, unsupported execution environment or conflicting inventory are Unknown. v2 continues to require the typed Cloud SQL instance volume and the matching `/cloudsql` mount (or its documented equivalent). Configuration proof does not claim that a socket was opened, IAM/DB connectivity worked, or application acceptance succeeded. See [Google's socket configuration](https://docs.cloud.google.com/sql/docs/postgres/connect-run#connect_with_unix_sockets) and [single-container limitation](https://docs.cloud.google.com/run/docs/known-issues).
4. **Scoped reference equivalence:** expand a bare v1 network/subnetwork or same-project secret reference only when the explicit ID/number declaration proves the expected owning project and, for subnetworks, the region. Compare full normalized resource identities. Cross-project bare references, conflicting aliases, extra interfaces/tags, duplicate secrets and unsupported reference forms are Unknown. No weakening to leaf-name comparison is allowed.
5. **Honest compatibility evidence:** recover and sanitize the existing captures without filling omitted fields. The selected successful migration pair has no volumes/mounts; entries with unrelated mounts are negative/unsupported examples. Creator-client annotations do not prove the describing CLI version. Independently authored neutral fixtures must preserve the captured representation and clearly distinguish recovered shape evidence, documentation-derived values and local exact-version SDK formatter/replay evidence. Validate gcloud 576.0.0's installed read command/JSON formatter offline against that representation, record exact SDK hashes/OS/argv, and make no new-live-read claim. Do not demand a new cloud read solely to reproduce an already recovered shape. Compatibility acceptance is the reviewed mapping plus executable fixture/manifest checks; the owner's direction authorizes implementing this evidence-backed reconciliation. No supported-platform/process claim precedes its native tests.
6. **Required regressions:** old profile constructor/omitted-property artifacts; absent/invalid/overflow/leading-zero/wrong project number; independent plan/Terraform identity tamper; wrong ID with correct number and vice versa; swapped region/parent/execution; v1 implicit SQL positive, missing/multiple SQL connections, extra container/mount/volume/connector/unknown execution-affecting annotation; v2 typed-mount positive and conflict; full/bare same-project network and cross-project/region rejection. Keep all previously planned state, process, CLI, neutral-consumer and legacy tests. Retain exact original gate thresholds and no reflection.

The prior missing-capture blocker is resolved. This amendment changes representation rules and source evidence, not the set of operational facts required for `ConfigurationMatched=true`. A native engineering review challenged compatibility, identity/inventory negatives and provenance; those obligations are carried here. The full historical review remains below.

## Implementation plan
# Design: Exact Cloud Run execution observation with bounded evidence

Generated by /office-hours on 2026-10-24.
Repository: forge-trust/AppSurface. Branch: main. Source inspected: `486d68cdca85cbe33bda4f97dfe5cd3d0a674a55`.
Status: APPROVED. Mode: Intrapreneurship. Selected approach: B.
Issue: [#891](https://github.com/forge-trust/AppSurface/issues/891). Extends the [approved deployment-target design](https://github.com/forge-trust/AppSurface/blob/main/Deployment/reference.md); retains its pure-render and application-owned mutation boundaries. No unrelated design is superseded.

## Problem and evidence

Applications interpret Cloud Run execution identity, configuration and terminal outcomes themselves. The existing [GCP deployment package](../../Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md) validates deployment artifacts and deployed Job configuration. It cannot establish that a selected execution ran the intended configuration and completed successfully.

The concrete evidence is static inspection of the existing target, command runner and CLI at the source above, together with the issue's adoption requirement. No adopter time savings, removed lines, performance improvement, live-cloud behavior or implementation test result has been measured in this session. The user selected internal-platform work, reuse of the approved design, all five premises, the identity refinement and Approach B. No named-user interview or urgency measurement was supplied.

The primary user is an application/platform developer who already owns Job submission and a deployment bundle. The narrow delivery is one shared observer plus its CLI adapter, demonstrated by two neutral logical migration Jobs. It does not introduce a general Job authoring model.

## Decisions and premises

1. Validate the existing three-artifact bundle and logical-to-physical binding before provider access. Require `projects/{project}/locations/{region}/jobs/{job}/executions/{execution}`; never list, choose latest, infer an execution from a name prefix, or substitute another resource.
2. Prove the requested execution's captured operational configuration, including its complete immutable image identity, and its overall terminal outcome. Today's Job configuration and a successful intermediate condition cannot supply missing proof.
3. Missing, malformed, conflicting, inaccessible or insufficient evidence is `Unknown`. Later read failures do not refresh or promote an earlier observation. Workload failure and provider-read failure remain distinct.
4. Use one monotonic budget, caller cancellation and streaming byte limits. Introduce an additive bounded runner seam; existing `IGcloudCommandRunner` implementations remain source-compatible. No observer fallback may call an unbounded runner.
5. Read an existing bundle from the CLI and use the same observer/classifier as library callers. Aspire retains deployment authoring and pipeline ownership. Submission, provider cancellation, retries of the workload, recovery, IAM verification and application acceptance remain caller-owned.
6. Require an execution UID and retain it across polls. A changed UID or insufficient current-generation evidence is `Unknown`. Compare execution generation with that execution's observed generation, never with a Job generation. The bundle contains no provider UID; the caller links the requested execution to its submission. Continuity before the first response and provenance of a particular apply/submission are not claimed.

## Public-contract research and independent perspective

Google documents immutable execution configuration, a parent Job, a task template, conditions, task counters and execution UID/generation fields in the [v2 Execution schema](https://docs.cloud.google.com/run/docs/reference/rest/v2/projects.locations.jobs.executions). [Per-execution overrides](https://docs.cloud.google.com/run/docs/execute/jobs#override_job_configuration_for_a_specific_execution) can change arguments, environment, task count and timeout without changing the Job. These support execution-based configuration proof.

The [v1 execution status](https://docs.cloud.google.com/run/docs/reference/rest/v1/namespaces.executions#ExecutionStatus) identifies `Completed` as the overall condition. The [v2 condition contract](https://docs.cloud.google.com/run/docs/reference/rest/v2/Condition) distinguishes transient and terminal states and cancellation in progress from completed cancellation. The [exact describe command](https://docs.cloud.google.com/sdk/gcloud/reference/run/jobs/executions/describe) and [v2 get method](https://docs.cloud.google.com/run/docs/reference/rest/v2/projects.locations.jobs.executions/get) provide read-only inspection. These are documentation checks, not a live CLI compatibility proof.

A fresh native Codex reviewer supported separate configuration and workload classification and challenged name-only identity continuity. Its proposal to add Job UID/generation to the rendered bundle was not adopted: render is provider-free, and Job and Execution generations describe different resources. The user instead approved premise 6. Claude Code was unavailable; five `combo/sub` launches were rejected and the configured `gpt-5.6-luna` fallback completed. This is a same-harness independent read, not a cross-model result.

## Approaches considered

- **A, compact observer:** rejected in favor of independently testable evidence rules; it would meet the same contract with parsing, comparison and polling more closely coupled.
- **B, small internal pipeline:** selected because strict normalization, classification and deadlines need independent verification. Provisional effort: human 18–28 hours / agent-assisted 7–10 hours, plus any separately authorized live-provider verification. These are estimates.

## Recommended architecture

Keep all new execution behavior in `ForgeTrust.AppSurface.Deployment.GcpCloudRun`; the neutral deployment package remains provider-independent. Add no cloud SDK, hosted service or generic workflow framework.

```text
API request / CLI bundle snapshot
    -> validated bundle expectations + exact execution identity
    -> bounded gcloud execution describe
    -> strict response normalizer
    -> pure identity/configuration/state classifier
    -> bounded observer loop
    -> immutable observation / CLI renderer and exit mapping
```

Concrete internal responsibilities:

| Component | Responsibility and reuse |
| --- | --- |
| Bundle validator | Extract the target's existing artifact/hash/cross-artifact checks into one internal authority. Produce immutable expected Job/configuration facts. Preserve existing configuration-verification semantics and diagnostics. |
| Execution identity and normalizer | Admit a canonical resource and normalize supported v1/v2 concrete response shapes into one internal snapshot with presence information. Reject ambiguous fields instead of reusing the permissive Job parser. |
| Evidence classifier | Pure comparison of one validated expectation and one normalized snapshot. Configuration matching remains independent of Running/Succeeded/Failed/Cancelled. No I/O or clock access. |
| Bounded runner | Own argument-safe launch, byte capture, timeout, cancellation, kill and stream cleanup. Reuse launcher/token rules where compatible, not unbounded capture. |
| Observer | Own one request's immutable expectation, pinned UID, monotonic deadline and sequential polling. No cross-request cache. |
| CLI adapter | Load one bounded bundle snapshot, construct the request, call the observer, render safe output and map exits. Reuse CLI cancellation and duration conventions. |

The production runner and injectable bounded seam are new contracts; do not add a required member to the legacy interface. The observer accepts only the bounded seam, with production defaults and an injectable `TimeProvider`. Name and document the seam's result/failure contract before implementation; it must distinguish complete output, nonzero exit, overflow, deadline and cleanup failure. Existing legacy verification does not acquire a new output cap through this change.

## Public API contract

Retain the issue's proposed public shape in the GCP package:

```csharp
public enum CloudRunExecutionState
{
    Running, Succeeded, Failed, Cancelled, Unknown
}

public sealed record CloudRunExecutionObservationRequest(
    DeploymentRenderResult Deployment,
    string LogicalJobId,
    string ExecutionResourceName,
    TimeSpan Timeout,
    TimeSpan PollInterval);

public sealed record CloudRunExecutionObservation(
    string LogicalJobId,
    string ExecutionResourceName,
    CloudRunExecutionState State,
    bool ConfigurationMatched,
    DateTimeOffset ObservedAt,
    string DiagnosticCode);

public interface IGcpCloudRunExecutionObserver
{
    Task<CloudRunExecutionObservation> ObserveAsync(
        CloudRunExecutionObservationRequest request,
        CancellationToken cancellationToken = default);
}
```

There are no API duration defaults. Require finite positive `Timeout` and `PollInterval`, with interval no greater than timeout; reject unsupported timer ranges before dispatch. A null request is a programming error and throws `ArgumentNullException`. For non-null requests, invalid values, invalid bundles and evidence failures return `Unknown` with a stable diagnostic and zero provider calls for local failures. Caller cancellation throws `OperationCanceledException`, including pre-cancellation, and takes precedence when observed concurrently with an internal deadline.

`ConfigurationMatched=true` means all supported operational fields were positively established and matched. False means no complete match was established; the diagnostic distinguishes mismatch from insufficient evidence. A definite configuration mismatch returns `Unknown/false` even if the provider reports success: the requested deployment's outcome has not been established. Matching configuration with a workload failure remains `Failed/true`.

`ObservedAt` is UTC. For a successful provider read it is the local receipt time for the evidence used; a running deadline result retains that time. Unknown failures use the local failure-decision time and do not imply provider freshness. Invalid identifiers are represented by empty safe identifier fields, never echoed verbatim. Results contain no raw JSON, provider message, environment value, command argument, stderr, credential or exception text. UID and generation remain internal evidence in this version.

## Validation and provider normalization

Snapshot artifact bytes once. Reuse the three canonical filenames, target/schema checks, exact hashes, cross-artifact consistency, capabilities and unique logical/physical bindings from the [deployment reference](../../Deployment/reference.md#generated-artifacts). Revalidate the immutable image grammar and required operational fields before deriving expectations. A bad bundle never reaches gcloud. Hash consistency is not a signature or proof that an untrusted caller's expected bundle is authentic.

The CLI reads only those three files from one explicit directory, rejects symlink/reparse-point file or directory paths, missing/duplicate/unexpected bundle entries except the existing ownership marker, and reads each file through a 4 MiB byte limit before parsing; the API applies the same per-artifact admission limit. This is a proposed finite input limit, not a measured existing artifact size. Snapshot validation detects inconsistent files read during replacement; it does not claim an adversarially mutable directory is a trusted release source.

Require the request's project/region/physical Job to match the validated bundle exactly. Do not guess project-ID/project-number equivalence or fetch alias mappings. The provider adapter may use an execution ID plus explicit project and region if required by the supported CLI, but it must validate returned identity independently. The v1 form requires a fixture-backed mapping for execution name, namespace, region and parent Job evidence; command arguments or a common name prefix alone are insufficient. No current-Job read fills a missing identity/configuration field.

The v1 `metadata/spec/status` and v2 `name/job/template/conditions` forms are candidate shapes pending exact-version compatibility-manifest acceptance. Implement admitted concrete forms through the same normalizer; no shape is supported merely because it resembles a documented schema. Preserve missing versus explicit values. Reject duplicate evidence properties, mixed contradictory shapes, malformed types, duplicate condition types, ambiguous containers/environment entries and unsupported state tokens. Ignore harmless extra metadata, not unexplained execution-affecting configuration. Normalize omissions only where the documented format defines a specific default and independent fixtures prove equivalence. For example, protobuf omitted zero task counters may represent zero; a missing positive task count, UID, image or generation proof cannot be fabricated.

Compare the execution's image, command, ordered arguments, non-secret environment, task count, parallelism, retries, task timeout, service account, secret delivery/reference/version, network/subnetwork/egress and Cloud SQL attachment against the existing operational expectation. Unexpected containers, environment overrides or unsupported execution-affecting additions cannot pass. Source/environment labels are not submission provenance or a substitute for these fields. Secret `latest` reference equality does not establish equal secret material.

### Minimum evidence contract and compatibility gate

The internal contract is `cloud-run-execution-evidence/1`. The table below is normative for both provider shapes: R means present, valid and positively matched; D permits only the named documented default; U means no supported mapping exists and the entire observation is Unknown. An absent R field is Unknown, never a match. Null, malformed and conflicting values are not omissions eligible for D. A schema-shaped fixture alone does not certify a gcloud version.

Here `task` is v2 `template` or v1 `spec.template.spec`; container facts refer to its single supported container. Source paths are candidates until the compatibility manifest binds them to concrete fixtures; the semantic requirements and missing-field behavior are fixed now.

| Fact | v2 source / v1 source | Requirement and omission rule |
| --- | --- | --- |
| Execution path, project, region, parent Job | `name` plus `job` / `metadata.name`, `metadata.namespace` and independently verified location/parent metadata | R; all scope segments must agree. An unproven v1 parent/location mapping is U. Never infer from the requested command or execution-name prefix. |
| Execution UID | `uid` / `metadata.uid` | R, nonempty valid UID, invariant across reads. |
| Execution status generation | `generation`, `observedGeneration` / `metadata.generation`, `status.observedGeneration` | R, valid integral values describing the same current execution generation. |
| Image | `task.containers[0].image` / same task-relative field | R, exact full repository plus immutable digest; no tag resolution. |
| Command | `task.containers[0].command` / same | R, exact declared command elements. |
| Ordered arguments | `task.containers[0].args` / same | R when nonempty expected; D to no argument override only when expectation is empty and the admitted format proves absent/empty equivalence. |
| Complete environment inventory | `task.containers[0].env` / same | R because supported intent includes secret-backed delivery; compare the full inventory, names and non-secret values. Extra entries are mismatches. |
| Secret delivery, ID and version | `env[].valueSource.secretKeyRef` / fixture-backed `env[].valueFrom.secretKeyRef` and alias metadata | R, exactly the expected environment destination/reference/version; an unresolved alias is U. |
| Task count | `taskCount` / `spec.taskCount` | R; no task-count default is used in this first contract. |
| Parallelism | `parallelism` / `spec.parallelism` | R, exact effective positive value. |
| Retries | `task.maxRetries` / same task-relative field | R; omission is Unknown, including when the expected value is zero. |
| Task timeout | `task.timeout` / `task.timeoutSeconds` | R, normalized exact duration. No omitted timeout default is used. |
| Service account | `task.serviceAccount` / `task.serviceAccountName` | R, exact declared identity; never infer a project default. |
| Network and subnet | `task.vpcAccess.networkInterfaces[]` / verified execution network annotation | R, exact configured network and subnet. Missing network/subnet is not resolved through another API. |
| Egress | `task.vpcAccess.egress` / verified execution egress annotation | R unless the admitted v2 format documents omitted egress as `PRIVATE_RANGES_ONLY`, in which case D applies only to that expected value. A missing v1 mapping is U. |
| Cloud SQL attachment and socket projection | `task.volumes[].cloudSqlInstance.instances` plus container mount / verified execution Cloud SQL annotation plus mount evidence | R, exact instance and required socket projection; an annotation alone cannot stand in for an unproven mount. |
| Overall completion condition | `conditions` / `status.conditions` | R, one supported overall condition; never substitute a stage condition. |
| Terminal completion marker | `completionTime` / `status.completionTime` | R for terminal outcomes; absent is required for a clean nonterminal interpretation. |
| Running/succeeded/failed/cancelled/retried counters | root counters / `status` counters | R or D to zero only for admitted formats with independently verified omitted-zero semantics; reject negatives, overflow and terminal contradictions. |
| Reconciliation status | `reconciling` / current-generation overall-condition semantics | R or documented admitted omitted-false semantics for v2; otherwise U. No active reconciliation may pass as terminal. |
| Additional execution-affecting configuration | complete task/container inventory | R as an inventory check: unsupported additions are Unknown, never ignored as metadata. |

The [VPC schema](https://docs.cloud.google.com/run/docs/reference/rest/v2/VpcAccess), [volume schema](https://docs.cloud.google.com/run/docs/reference/rest/v2/Volume) and [v1 task schema](https://docs.cloud.google.com/run/docs/reference/rest/v1/TaskSpec) identify candidate carriers. Document and fixture each permitted name/reference normalization; do not invent a mapping for an absent field. This deliberately permits truthful Unknown outcomes for insufficient provider formats.

There is **no certified gcloud version in this design**. A local non-cloud `gcloud version --format=json` check exited 0 and reported **576.0.0**; that exact version is the first candidate for the compatibility manifest, not a supported-version claim. Before accepting the observer implementation, require `compatibility.json` with exact version strings, OS/launcher combinations, format identifiers, command arguments, fixture paths/hashes, normalizer-contract version and maintainer acceptance. No version range or v1/v2 blanket claim is allowed. An additional candidate version needs the same proof. A shape without every R fact and documented D rule remains uncertified; if no candidate satisfies this gate, return to design rather than ship an observer that can only return Unknown.

### Evidence worksheet delivery

The implementing engineer produces two worksheets and their independently authored positive/negative fixtures under the future test directory `Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun.Tests/Fixtures/ExecutionEvidence/v1/`, in `schema-preparation/worksheet.json` and `catalog-refresh/worksheet.json`. A `compatibility.json` at that root lists the exact eligible tool versions and references both worksheets. These are implementation deliverables, not files created by this design session.

Worksheet schema `cloud-run-execution-worksheet/1` requires: `schemaVersion`, `logicalJobId`, `expectedBundle` (three artifact hashes, canonical expected Job and all matrix expectations), `tool` (exact version, OS/launcher, argv and output shape), `provenance` (document/source URL, fixture kind, sanitization notes and whether a provider read actually ran), `positiveFixture` and `negativeFixture` (paths, SHA-256 and expected classification), and `fields` (one entry per matrix row containing source JSON pointer(s), expected safe fact, observed safe fact, R/D/U rule and evidence citation). An unavailable fact is explicitly `{status:"unavailable-proof", reason:"<safe reason>"}`; do not insert a guessed or copied expected value as observation. Secret payloads and private deployment values are forbidden. `review` records named implementing owner, named accepting repository maintainer, date, disposition and unresolved facts.

Completion gate: both worksheets, all field entries and negative controls must be complete, and the named maintainer must record acceptance before the implementation's supported response/state table or provider-compatibility claim is finalized. Every unavailable R fact blocks that shape; D needs its documented default and omission fixture. Documentation-derived fixtures prove parser behavior only. A separately authorized, sanitized read of an already existing execution is needed to label a fixture provider-observed; no submission or mutation is implied by this assignment.

## Classification and polling

The classifier uses the overall execution-completion condition, current-generation evidence, completion marker and consistent task counters. An intermediate `Started` or `ResourcesAvailable` success is insufficient. A terminal condition with contradictory counters, stale generation, active reconciliation or missing required evidence is `Unknown`.

| Evidence after identity/configuration admission | State |
| --- | --- |
| Current overall success, completion marker, all expected tasks succeeded, none running/failed/cancelled | Succeeded |
| Current overall terminal failure with recognized workload-failure evidence and consistent counters | Failed |
| Current terminal cancellation evidence with consistent completion/counters | Cancelled |
| Positive supported pending/running/reconciling/cancelling evidence, with no terminal contradiction | Running |
| Missing, stale, conflicting, unsupported or inaccessible evidence | Unknown |

`CANCELLING` is nonterminal. Provider `CANCELLED` is distinct from caller cancellation. A retried task is not automatically a failed execution; compare the configured retry policy and final counters. Missing conditions are not evidence of Running. Exact accepted state/reason/counter combinations become a documented table backed by fixtures before declaring compatibility; an unproven combination returns Unknown.

The observer polls immediately after local validation, then delays sequentially after a matching nonterminal read. Each iteration checks the caller token, remaining monotonic budget and pinned UID. No overlapping requests, hidden SDK retries, latest lookups or workload retries. Stop on matching terminal state, configuration mismatch, Unknown or caller cancellation. A provider failure after a Running response returns Unknown, not stale Running.

Use `TimeProvider.GetTimestamp`/elapsed time; wall-clock changes cannot extend the budget. The budget starts at API entry. The CLI additionally starts its timeout before bundle loading and passes only the remaining budget to the API. File admission and parsing check cancellation and the remaining budget. If the deadline expires while no read is in flight, return the last matching Running observation only if no later read failed; retain its `ObservedAt`. If no such observation exists, or an in-flight read/cleanup fails, return Unknown. A running result is last-observed evidence, not proof of the state at the deadline.

Reserve cleanup time inside, not after, the overall budget: the runner reserves the lesser of 250 ms and one tenth of its allotted remaining time for termination and stream closure. Do not launch when no usable command time remains. Very short positive budgets can therefore return Unknown without starting a process; do not round them upward. Tests verify admission, deadline boundaries and cleanup separately; no claim of preempting a blocked operating-system process-start call or a nonconforming injected runner is made.

## Bounded process contract

Run only the fixed `gcloud run jobs executions describe` read operation with validated argument-array tokens, explicit location/project, JSON output and noninteractive behavior. Preserve safe Unix and Windows launcher handling. No arbitrary executable, shell fragment, provider mutation or raw extra-argument extension is exposed.

Read stdout and stderr concurrently from byte streams. Maximum retained stdout is 1,048,576 bytes and stderr is 65,536 bytes. Detect overflow by reading at most the remaining allowance plus one byte; exactly-at-limit output is allowed, one byte beyond fails. Do not decode or parse a truncated response. Invalid UTF-8 also fails. Overflow cancels observation, terminates the process tree, closes/drains both streams and returns a safe diagnostic. Never append unbounded discard data or echo the offending output.

Link caller cancellation and the allotted deadline before dispatch. On timeout, cancellation, overflow or reader failure, stop the process tree and settle both reader tasks. Cleanup must consume only its reserved remaining time; pipe closure is the final bounded drain escape. Cleanup failure is Unknown and cannot leave a success result. Native process tests must establish the supported OS behavior and disclose an unkillable-process limitation rather than claiming fixture success proves universal OS cleanup. Cancellation rethrows after cleanup; it does not cancel the remote execution.

## CLI and diagnostic contract

```text
appsurface deployment gcp-cloud-run observe-execution
  --bundle-dir <directory>
  --job <logical-id>
  --execution <canonical-resource-name>
  --timeout 60s
  --poll-interval 2s
  --json
```

Use defaults of 60s and 2s when omitted. Duration syntax follows the existing [CLI polling convention](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll); require the same finite ordering as the API. The command consumes a published bundle without evaluating an AppHost. Add a GCP package reference and command-local registrations in the actual [CLI runtime](../../Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs); the issue's `DeploymentCommands.cs` reference is prospective, not an existing file.

With `--json`, write exactly one result object to stdout with `schemaVersion: "1.0"` and the observation fields in camelCase, string state values and RFC 3339 UTC `observedAt`. No progress or provider output appears on stdout. Without `--json`, render the same safe result as a short text summary. Standard help remains standard CLI help. Recognized-command input failures, including malformed option values, use safe admission before generic parser errors can echo input.

| Observation/condition | Exit |
| --- | ---: |
| Succeeded and ConfigurationMatched | 0 |
| Failed or Cancelled and ConfigurationMatched | 1 |
| Invalid input, Unknown, mismatch, provider/output/cleanup failure | 2 |
| Last matching Running evidence when budget ends cleanly | 3 |
| Caller cancellation | 130 |

The API propagates caller cancellation; the CLI catches only its own cancellation token. It attempts one Unknown/false caller-cancelled envelope only while no output failure has occurred. After the first stdout write/flush failure it suppresses further stdout/stderr writes. Caller cancellation returns 130 even when the envelope cannot be delivered; ordinary output delivery failure returns 2. Never retry an envelope or claim successful delivery of a partial object. C1 requires command-path tests for already-broken output and failure during the envelope write.

Allocate a documented execution-observation diagnostic range after the currently used `ASDEPLOY169`, verifying collisions during implementation. Required categories are invalid request/bundle, identity mismatch/change, malformed/unsupported/stale evidence, configuration mismatch, unavailable tool, provider-read failure, output overflow, unknown deadline, running deadline, terminal success/failure/cancellation, caller cancellation and cleanup failure. Each code has fixed safe problem/cause/fix text and a reference link; no diagnostic parses or renders provider free text as authority.

## Verification, documentation and distribution

All items below are planned checks, not executed results. Every new test states `protects=<observable>; fails_when=<wrong result rejected>; no_claim=<unproven property>`. Exercise public APIs or intentionally internal test seams; no reflection or placeholder assertions.

| Planned group | Required evidence |
| --- | --- |
| Parser/classifier, 8 groups | Exact identity/UID continuity; v1/v2 normalization; all states; missing/malformed/duplicate/conflicting/stale evidence; every configuration field and image; byte boundaries; monotonic budgets; caller cancellation. |
| Native process, 3 groups | Both streams at/over limits including concurrent/multibyte output; timeout/tree/pipe cleanup and cancellation; exact argument/launcher handling and no mutation. |
| CLI/consumer, 3 groups | Versioned safe JSON and all exits including invalid argv; running deadline versus read failure/cancellation; two neutral Job consumers including cross-execution rejection. |

Negative controls include a success response for another execution/Job, a changed UID, stale generation, the correct current Job with the wrong captured execution image, per-execution argument/environment overrides, missing overall completion, contradictory task counts, unsupported cancellation evidence, exactly-at-limit and limit-plus-one streams, and a read failure after a valid Running response. Polling tests use deterministic time; native process tests verify actual child behavior with scheduling tolerance reported separately from the configured budget. Parser fixtures must be independently derived from documented/provider shapes, not generated by the code being tested.

Use two synthetic consumers such as `schema-preparation` and `catalog-refresh`, both within the supported migration-job intent, with distinct physical Jobs, execution names and images. Prove shared behavior and rejection of swapped evidence. They are neutral contract proofs, not proof of two real production migrations or a consumer recovery strategy.

Before push, run formatting, relevant builds/analyzers/XML-documentation checks, focused GCP/CLI/process tests, unchanged render/config-verification regressions, and packed library/tool consumer checks. Run [solution coverage](../../scripts/coverage-solution.sh) when practical; preserve existing gates and target nearly complete branch verification in changed critical paths. Do not run the package artifact verifier concurrently with solution coverage in one checkout: shared build outputs can invalidate evidence. Report actual commands, revisions, exits, counts, failures and skips. Separate implementation/test line additions and removals; individually justify test, validator, timeout, threshold, snapshot, CI or suppression changes. No performance or net-deletion claim follows from this design.

Document public and internal contract shape, defaults, constraints, state table, diagnostics, cancellation, limits, compatibility and ownership in XML/reference content. Update the package README, [deployment overview](../../Deployment/README.md), [reference](../../Deployment/reference.md), adoption guidance, [CLI guide](../../Cli/ForgeTrust.AppSurface.Cli/README.md), package chooser/discoverability where affected, and two runnable neutral examples. Explain configuration parity versus execution completion versus application acceptance, latest-secret limits, caller-selected execution identity, zero mutation and recovery choices. Preserve existing render and VerifyAsync behavior through regression tests.

Ship through the existing coordinated NuGet library and `appsurface` dotnet-tool release pipeline. Extend package metadata/closure and packed-consumer proof; merging this design does not publish a release. Verify actual published-package adoption after a separately authorized release if making a released-adoption claim. No new package or deployment service is required. Reverting observer adoption restores application observation without persisted state or cloud changes.

## Success criteria and remaining feasibility gates

Acceptance requires every [#891 criterion](https://github.com/forge-trust/AppSurface/issues/891) plus the approved UID-continuity refinement: matching configuration and exact terminal success; old/concurrent execution rejection; provider-unavailable distinction; zero mutation; API/CLI time/output/cancellation bounds; unchanged legacy behavior; operational boundary docs; two neutral consumers; honest separate line accounting.

Before claiming provider compatibility, establish concrete supported gcloud versions and sanitized response fixtures with identity/UID, generation, captured image/configuration and terminal-state evidence. Official schemas establish the candidate design, not the actual command's emitted shape. If a supported shape omits required proof, return Unknown and expose the compatibility gap; do not silently weaken a premise or add a current-Job fallback. Also establish that inherited secret aliases, network annotations and provider defaults normalize unambiguously.

The proposed 4 MiB artifact cap, supported duration/timer range, cleanup reserve, diagnostic allocation and exact recognized state table require executable boundary tests and reference entries. They are explicit implementation obligations. No independent live-cloud check was authorized or run; fixture and native-process proof must remain labeled separately from any future protected read-only provider verification. The real application's submission correlation and acceptance behavior remain outside this feature's acceptance.

## The Assignment

Before replacing an application observer, have the implementing engineer prepare the two sanitized worksheets using [the delivery schema and paths above](#evidence-worksheet-delivery), and have the named repository maintainer accept them. Include expected bundle facts, execution identity/UID/generation, one matching terminal response and one deliberately swapped or overridden response for each Job. Point every asserted match to execution evidence, explicitly mark `unavailable-proof`, and block the affected shape until required proof exists. This is the go/no-go input for the exact-version compatibility manifest and final state table, not permission to run or mutate a cloud Job.

## What I noticed about how you think

- Your `A` on extending the prior design and accepting the ownership premises kept this contribution inside an existing package boundary rather than reopening deployment orchestration.
- Your `A` on the UID refinement accepted a stronger evidence check while retaining pure rendering; your `B` on architecture chose independently testable rules over the fewest files. No user pushback, interview evidence or adopter timing is inferred from these selections.

<!-- gstack:office-hours:concerns:start -->
## Reviewer Concerns

Disposition: COMPLETED

Stop: PASS

No blocking findings remain. The 1 open minor finding(s) below are recorded for the user, not fixed.

### R2-1 — clarity (minor)

**Problem**

> The document still says the CLI emits an Unknown/false envelope on caller cancellation only 'when output is still writable', but does not specify the observable behavior when output has already failed or the envelope cannot be written. The exit code is stated, but the output and diagnostic behavior for this required path remain ambiguous.

**Remedy**

> State the exact best-effort behavior when cancellation output cannot be written, such as suppressing further output and returning 130, and include that rule in the CLI cancellation/output-failure test contract.

### Prior finding evidence

**R1-3 → R2-1 (persisting)**

> The CLI cancellation paragraph still limits the Unknown/false envelope to cases where output is writable and gives no behavior for an already failed output stream.
<!-- gstack:office-hours:concerns:end -->

<!-- autoplan-accepted:ceo -->
- Preserve the complete approved #891 source contract, including the exact-execution-only read, immutable captured configuration, required evidence matrix, UID continuity, same-Execution generation proof, Unknown boundaries, legacy compatibility, monotonic total budget, byte caps, zero mutation, two neutral consumers, documentation and separate line accounting.
- On caller cancellation, the CLI attempts its single safe cancellation envelope only while no output failure has occurred. After the first stdout write/flush failure, it suppresses further stdout/stderr writes and returns 130; a failed cancellation-envelope write also returns 130. Ordinary output delivery failure returns 2. It never retries an envelope or claims successful delivery of a partial object. Add command-path tests for a stream broken before cancellation and a failure during the cancellation-envelope write, with safe-output/no-raw-data assertions and required test cards.
- Include a copyable CI shell recipe in the CLI guide and neutral examples that captures the command's exit status and handles 0, 1, 2, 3 and 130 distinctly without automatically retrying, cancelling or submitting a workload. Verify the recipe against installed-tool or equivalent command-path fixtures; label provider evidence separately.
- Sequence concrete exact-version evidence feasibility before observer/CLI integration. Failure to provide any required R fact blocks certification and returns to design; no always-Unknown delivery or current-Job fallback meets acceptance. Rebase against refreshed main and repeat command, diagnostic and package collision checks before implementation.
- Replace the original cancellation paragraph with the final C1 wording and label v1/v2 as candidate shapes until exact-version acceptance; retain original behavior and evidence requirements. The appended accepted obligations are normative, while the earlier office-hours concern block is historical review evidence.
- Acceptance checklist: #891 AC1 maps to the Public API contract, evidence matrix and terminal classifier fixtures; AC2 to exact path/UID and swapped-execution tests; AC3 to provider-read diagnostics versus Failed/true; AC4 to runner fixed-argv allowlist and zero-mutation process assertions; AC5 to API/CLI monotonic entry budgets, streaming caps, cancellation and cleanup tests; AC6 to unchanged render/VerifyAsync regressions; AC7 to reference/adoption boundary docs; AC8 to both neutral consumers with distinct execution/image identities; AC9 to actual separate implementation/test added/removed line counts in the completion report. Every new test retains its protects/fails_when/no_claim card.
- Feasibility go/no-go: complete both neutral worksheets first; any unavailable R fact or unproven D omission blocks that format immediately. Record which candidate failed and why; attempt another candidate only if a documented mapping can supply the missing proof. If none can, stop integration and return to design. Only accepted fixtures, version/OS/launcher manifest, state table and named maintainer acceptance unlock observer/CLI integration and compatibility claims. Documentation fixtures cannot be relabeled provider-observed.
- Document a safe caller action for every diagnostic category: repair local input; hold on identity/configuration conflict; inspect tool/auth/network for provider failure; escalate unsupported/stale evidence to compatibility review; investigate overflow/cleanup; allow only an explicit caller decision to start another bounded observation after Running/Unknown. Add no automatic workload or observation retry policy, alert threshold or telemetry service.
- Keep normalization helpers small and explicit by identity/configuration/completion responsibility. Set and document a finite JSON depth and collection admission within existing byte limits, and test exact admission boundaries without weakening any required evidence field. Preserve public/internal documentation and no-reflection test seams.
- Enforce one result/cleanup owner: do not parse/classify before process exit and both readers settle; latch any nonzero/overflow/read/cleanup failure so later callbacks cannot publish success. Sequential polling keeps request state isolated. Test stdout/exit completion in both orders, either stream failing first, caller cancellation before/after internal deadline and read failure after Running using controlled pause/release points, preserving the caller-token precedence contract.
- Keep one implementation wave/PR by default, with no cut to the issue acceptance criteria. Follow-up real-adopter outcome measurement is C6 in TODOS.md and does not become a new release gate; retain both neutral consumers as required contract proof.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:dx -->
- Add one complete quickstart in the existing CLI guide, linked from the provider README, Deployment overview and package chooser. State .NET 10, certified gcloud/OS/launcher, inherited credentials/read permissions, trusted published bundle and exact existing execution prerequisites. Include concrete copyable CLI and C# construction snippets, actual fixture output, all state/exit branches and caller-owned acceptance/recovery. Show the existing RenderAsync result path and bounded artifact-byte reconstruction with DeploymentArtifact.Create; add no new public bundle-loader or replay/compatibility command.
- Keep offline sample evidence separately labeled from provider-observed compatibility. Use the existing two neutral examples and bounded injection seam for the offline path; their success never certifies cloud fields. Provide the fixture/worksheet validation checklist through the test project and manifest files, including exact hashes/provenance and named maintainer acceptance; cloud capture remains separately authorized.
- Measure one manual implementation-time first-use session from opening the quickstart on the explicitly prepared machine to understanding a safe result and next action; include reading/version-selection/setup/command time and actual versions/OS/prerequisite blockers. Target 2–5 minutes is aspirational, with no automated release-time threshold, recurring process or telemetry. Time offline demonstration separately and do not substitute it for this clock.
- Run observation-command safe argument admission in AppSurfaceCliApp.RunAsync before generic CliFx parsing, following the existing doctor admission pattern. Test separate/joined values, missing/duplicate/unknown options, malformed resource/path/duration, help interactions and --json position. Never echo rejected values. Preserve C1 single-output-attempt/cancellation rules.
- Add the fixed execution-observation diagnostic table with planned codes ASDEPLOY170–ASDEPLOY189 and stable Deployment/reference.md anchors; recheck collisions after rebase before freezing codes. Cover invalid request/bundle, identity/change, malformed/unsupported/stale evidence, configuration, tool/provider, output/encoding, deadlines, terminal states, caller cancellation, cleanup, delivery and unexpected failures. For each failure give fixed safe problem, exact fix/escalation, recovery/retry bound, cloud side effects none, possible local-process cleanup state, safe context taxonomy, trace applicability, redaction and native channel. Text-mode failures write only safe problem/fix/code/reference to stderr; JSON stdout remains one existing-shape observation. No raw provider/exception/environment/argv data or debug file writer is added; after any output failure suppress further writes.
- Provisionally preserve the exact proposed public observation and code-only JSON shape pending final owner T2: no rich error fields or additional public diagnostic API are assumed. Explicitly retain the CAP6 machine fix/context/trace limitation; documentation/text improvements do not claim those fields are present in machine results.
- Document POSIX sh as C2 recipe's portability target; capture status immediately and branch on 0/1/2/3/130, preserve interruption, and never automatically retry/readjust/cancel/submit a workload. Verify via command-path/installed-tool fixtures and link it from quickstart.
- Organize existing documentation as quickstart -> adoption -> API/state/diagnostic reference -> compatibility matrix/manifest -> troubleshooting; use canonical absolute links in packaged README where source-relative targets are absent. Document PATH/inherited approved gcloud config variable names, certified installation selection and the injectable bounded seam; never expose arbitrary executable or argument extensions. Preserve coordinated upgrade, old-runner compatibility and caller rollback guidance.
- Normative diagnostic allocation is ASDEPLOY170 invalid request; 171 invalid bundle; 172 identity mismatch; 173 UID change; 174 malformed/conflicting evidence; 175 unsupported shape; 176 stale evidence; 177 configuration mismatch; 178 launcher unavailable; 179 provider read failure; 180 output/encoding limit; 181 unknown deadline; 182 clean idle Running deadline; 183 success; 184 workload failure; 185 remote cancellation; 186 caller cancellation; 187 cleanup unconfirmed; 188 delivery failure; 189 unexpected failure. Each code has a stable Deployment/reference.md execution-observation diagnostic anchor. Fixed fix/recovery text must implement the previously specified safe caller actions, including no automatic retries, no remote mutation and local-process reconciliation after cleanup failure.
- Diagnostic ASDEPLOY170: Invalid observation request. Fix: provide canonical IDs and finite positive ordered durations. Recovery: fix input; no provider work.
- Diagnostic ASDEPLOY171: Bundle validation failed. Fix: provide the complete trusted published artifact set. Recovery: repair, then explicit new read; no mutation.
- Diagnostic ASDEPLOY172: Execution identity does not match the bundle. Fix: ask the submission owner for its exact execution resource. Recovery: hold caller acceptance; no substitution.
- Diagnostic ASDEPLOY173: Execution UID changed between reads. Fix: reconcile identity with the submission owner. Recovery: hold; never continue with new UID.
- Diagnostic ASDEPLOY174: Execution evidence is malformed or conflicting. Fix: provide a sanitized reproduction to the maintainer. Recovery: stop; compatibility review.
- Diagnostic ASDEPLOY175: Execution evidence shape is unsupported. Fix: use a manifest-certified tool/OS or ask the maintainer for support. Recovery: stop; no inferred fields.
- Diagnostic ASDEPLOY176: Execution evidence is stale. Fix: inspect reconciliation and selected identity with the owner. Recovery: hold; explicit later observation only.
- Diagnostic ASDEPLOY177: Captured execution configuration differs. Fix: compare the expected bundle and submitted execution with the owner. Recovery: no workload retry or reconfigure.
- Diagnostic ASDEPLOY178: Certified gcloud launcher is unavailable. Fix: install/select a certified version on PATH. Recovery: one explicit new read after repair; no automatic retry.
- Diagnostic ASDEPLOY179: Exact execution read failed. Fix: have the operator inspect gcloud credentials, permissions and connectivity. Recovery: no automatic retry; caller may choose one fresh bounded read after repair, then escalate.
- Diagnostic ASDEPLOY180: Process output exceeded its byte limit or encoding contract. Fix: stop automation and ask the maintainer to inspect the tool format. Recovery: no truncated parse; local cleanup required.
- Diagnostic ASDEPLOY181: Observation budget ended without usable final evidence. Fix: keep acceptance unresolved and decide whether to observe again. Recovery: no automatic retry; no remote mutation.
- Diagnostic ASDEPLOY182: Last observed execution was Running when the clean idle budget ended. Fix: use ObservedAt and explicitly decide whether to wait/observe again. Recovery: last-read evidence only; CLI 3.
- Diagnostic ASDEPLOY183: Matching execution succeeded. Recovery: terminal evidence; CLI 0, not application acceptance.
- Diagnostic ASDEPLOY184: Matching execution failed. Fix: ask the workload owner to inspect its logs and recovery policy. Recovery: CLI 1; observer never retries workload.
- Diagnostic ASDEPLOY185: Matching execution was cancelled remotely. Fix: ask the workload owner to choose recovery. Recovery: CLI 1; distinct from local cancellation.
- Diagnostic ASDEPLOY186: Caller cancelled local observation. Recovery: CLI 130; remote execution unaffected.
- Diagnostic ASDEPLOY187: Local process cleanup could not be confirmed. Fix: reconcile the local child/pipe state before another observation. Recovery: no success; process side effects may remain, cloud mutation none.
- Diagnostic ASDEPLOY188: Observation output could not be delivered. Recovery: stop further writes; CLI 2, or 130 if caller cancelled.
- Diagnostic ASDEPLOY189: Unexpected observation failure. Fix: report the safe stage/type and certified tool/OS to the maintainer. Recovery: stop; reconcile local process, no raw trace.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Preserve all original/CEO/DX obligations and the issue's proposed observer request/result/interface. Retain Approach B, one implementation wave, no scope cut, no current-Job fallback and no new orchestration/SDK/signature service. T1/T2 remain final-owner choices with original values provisionally retained.
- Snapshot the caller's artifact list and each immutable artifact's bytes once at observer entry; derive immutable expected facts before the first provider await. Reject malformed/inconsistent snapshots before provider access. Test mutation during admission/provider pause and concurrent independent calls; no expectation, UID, deadline or result may leak across calls.
- Use one process cleanup/result owner with Started/Reading/Stopping/ClosingPipes/JoiningReaders/Finished states and a failure latch. Normal completion joins both readers and exit before parsing. On overflow/read fault/deadline/cancellation kill the owned local tree, close both pipes, join the readers inside the existing reserve and prohibit success after any failure. Do not count root exit or detached active reads as settlement. Native declared-platform tests must prove descendant pipe behavior, no active reader at return, either-stream/exit completion orders and bounded settlement. Unconfirmed local process/tree cleanup yields Unknown; a platform without the required proof is uncertified. Preserve the already disclosed OS-start/nonconforming-reader/unkillable-process limitations rather than asserting a universal OS guarantee.
- Keep the original CLI deadline as outer monotonic authority from before bundle load through observer await and delivery; all nested time allowances are recomputed conservatively from remaining time and never renew the original allowance. Public API creates a per-call ObservationBudget at entry, using TimeProvider timestamps/elapsed time. CLI guards the outer await separately and only the original console token produces caller-cancellation exit130; internal deadline during a read/cleanup yields Unknown/2. A completed clean idle Running result retains its original receipt time and exits3. Check elapsed/caller token at handoff, parse and return boundaries. Test controlled pauses around file admission, CLI/API handoff, read, delay, parse, cleanup and serialization/write initiation; do not add a public deadline type or alter the proposed observer signature.
- Define additive public IBoundedGcloudExecutionReader.ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken) accepting only a canonical selected execution and remaining allowance, no executable or extra argv. BoundedGcloudExecutionReadResult is immutable, holds typed outcome, optional exit code and defensively copied complete stdout bytes only; no public raw stderr. Distinguish Complete, NonzeroExit, ToolUnavailable, OutputLimit, InvalidEncoding, Deadline, ReadFailure and CleanupUnconfirmed. Only Complete/zero exit/settled capture permits bytes to be parsed. The observer rechecks custom-result cap/encoding/outcome; bounded implementers must honor cancellation/lifetime, without an unbounded legacy adapter. Default reader constructs fixed safe describe argv; GcpCloudRunExecutionObserver.Create() supplies defaults and an injection constructor accepts the bounded reader and TimeProvider. Fully document new public/internal contracts and intentionally internal trusted child-launch test seam.
- Make caller-expectation trust explicit in both API/CLI quickstarts and reference: hash-consistent bundles are not authenticated publishers, applied revisions or pre-first-read submission provenance. Test a self-consistent changed bundle against corresponding evidence as caller expectation only. Add no signature requirement or trust-service dependency.
- Give one command-local serializer/writer ownership of all stdout/stderr write/flush and exit selection, with Uncommitted/Writing/Delivered/Failed states. Only Uncommitted may replace the normal result with a cancellation envelope. Never emit a second object after writing begins or completes; caller cancellation observed before return still selects130 without claiming partial delivery. Any serialization/stdout/stderr/flush failure latches delivery failure and suppresses all later writes; ordinary failure exits2. Use ASDEPLOY188 for serialization/delivery and ASDEPLOY180 for captured-process encoding/limit failure. Test admission stderr failure, serialization failure, mid-object/flush failure, cancellation before/during/after delivery and already-broken output. No raw exception text, progress or retry output is allowed.
- Validate the planned new code allocation against current production codes after rebase; add scoped tests for unique ASDEPLOY170–189 definitions and their stable canonical reference anchors before package publication. Do not infer atomic reservation across branches or rewrite unrelated diagnostics.
- Use explicit JSON MaxDepth64 with strict duplicate/type/inventory checks and incremental bounded admission; test depth64/depth65, byte caps and malformed additions. Durations must be positive, interval<=timeout, and no larger than 4,294,967,294 milliseconds; one positive TimeSpan tick is valid but may return Unknown with no child if no usable command time remains. Cleanup reserve remains min(250ms, remaining/10); never round launch allowance up. TimeProvider-aware timers/delays and elapsed checks preserve logical deadlines. Document cooperative stream/OS scheduling limits, without widening timeout/tolerance thresholds or claiming measured p99/memory totals.
- Carry the complete planned test groups CP1–CP14: zero-read invalid admission/null behavior; exact path/UID continuity; independent v1/v2 R/D/U malformed/default/inventory matrix; overall state/generation/counters; every captured configuration field; exact cap/cap+1/UTF8; original monotonic budget across API/CLI; caller cancellation precedence; simultaneous stream/exit orders; descendant/pipe cleanup; safe fixed read-only argv; installed command JSON/text/exits/admission; Running-versus-read-failure deadlines; both packed neutral consumers and unchanged legacy render/Verify/custom-runner compatibility. Use public/intentionally internal production seams, deterministic time and native process proof, with required protects/fails_when/no_claim cards and no reflection. No proposed path is claimed tested by legacy tests.
- Carry edge groups EC1–EC8: mutable artifact lists; depth64/depth65/duplicate/conflicting JSON; tiny/invalid/above-range timers; all single-writer delivery/cancellation failures; caller-expectation trust; unavailable R/unproven D/wrong hash/version/OS/argv/maintainer manifests; diagnostic-code/anchor collisions; interleaved independent observer calls. Each entry in the saved QA test plan has observable/failure/why-new/seam/no-claim evidence. Preserve all old tests; no test retirement, threshold/timeout/tolerance/snapshot/suppression/CI relaxation is approved. Package verification and solution coverage stay sequential in one checkout; report actual commands/exits/counts and separate product/test line additions/removals.
<!-- /autoplan-accepted:eng -->
## Review record

### Autoplan intake and methodology

Run `main-20261024-135210`; source and active plan are this file. Restore point is `.gstack/tmp/autoplan/main-autoplan-restore-20261024-135210.md`. Review is implementation-ready, planning only. Base is main; HEAD is `486d68cd`. The approved office-hours source is retained byte for byte. Global newest-design discovery returned an unrelated completion note, so this invocation pins the explicit #891 source.

CEO methodology loaded through EOF: ranges 1–600, 601–1200, 1201–1800, 1801–2400, 2401–2472, 169485 bytes, SHA-256 `df3df0b96cf0e9b2590e863cc908fb7fa5e7bb432e2c6199e03d13fe13959a05`. Initial truncated reads were repeated successfully. Review sections are included in that methodology. Scope detector: 111 developer-term occurrences, developer-tool enabled, DX required; no rendered UI scope, Design not required. Brain product/goals/profile unavailable; recent decisions stale and empty. User answers and inspected source supply the context instead.

### CEO Step 0 — system audit and premise challenge

The pain is inconsistent application-owned interpretation of a submitted execution. The target outcome is exact, bounded, reusable observation; doing nothing retains duplicate interpretation and the risk of accepting another execution or a current Job template as proof. The issue is an internal platform capability, not a market-demand experiment. No adoption savings or urgency has been measured.

Audit: working tree initially held only this untracked design. Six existing stashes concern #806, #819 and RazorWire; none is applied or altered. Recent release/CLI changes include #886 test stabilization and #884 readiness proof; preserve their packaging and coverage gates. Repository TODOs defer caller polling/workflow ownership under #625 and transport-neutral package extraction pending adopter evidence; this observer does not take those responsibilities. The TODO/FIXME scan mostly found existing planning docs, not an execution observer implementation. A refreshed origin/main has no `IGcpCloudRunExecutionObserver` or `observe-execution` implementation and no #891 implementation commit. The refreshed remote has advanced beyond this checkout; implementation must rebase and repeat the surface/collision audit first.

Taste calibration: reuse immutable `DeploymentArtifact` and the verifier's pre-cloud hash/binding checks; reuse CanaryPoll's explicit cancellation/result pattern. Avoid `ReadToEndAsync` as bounded capture and avoid the permissive Job parser as execution evidence. These are behavior-based reuse decisions, not a broad cleanup mandate.

Landscape (public Google documentation, 2026-10-24; Aside unavailable, web fallback): conventional monitoring uses Cloud Console, gcloud, REST/client libraries and Cloud Monitoring. [Execution details](https://docs.cloud.google.com/run/docs/managing/job-executions) expose a selected execution; [Cloud Run monitoring](https://docs.cloud.google.com/run/docs/monitoring) covers operational metrics. These solve inspection and operations, while this package adds bundle-to-execution evidence classification. First-principles inference: a green workload status alone cannot prove intended configuration, so wrap the narrow read instead of creating another orchestrator. Cloud Workflows and an SDK are viable for orchestration owners but enlarge this package's dependency/ownership surface without replacing its classifier.

| Premise | Claim | Receipt | Verdict |
| --- | --- | --- | --- |
| PR1 | Existing verification proves Job parity, not execution completion | Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:88 | verified |
| PR2 | Hash/binding validation can be reused | Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:141 | verified |
| PR3 | Existing runner is not output bounded | Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:593 | verified |
| PR4 | CLI command is prospective and must be registered | Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs:87 | verified |
| PR5 | At least one concrete gcloud format can supply every required fact | this plan, Minimum evidence contract and compatibility gate | unverified; first feasibility gate, no support claim |

No new approach decision was needed: office-hours D7/B selected the small internal pipeline. Complexity exceeds two classes and likely eight files, but each boundary owns a distinct trust/lifetime responsibility; combining the strict normalizer with the permissive Job parser or using the unbounded runner would violate accepted guarantees. The minimum correct slice retains all API, CLI, two-consumer, documentation and proof requirements. No accepted requirement is deferred.

```text
CURRENT: Job parity + application interpretation
       -> THIS PLAN: exact execution + captured config + bounded evidence
       -> IDEAL: applications share observation, retain submission/acceptance policy
```

10x ambition is one interpretation contract reused across applications, with the same safe failure semantics in API and CLI. This is potential reuse, not a measured benefit. Platform potential stays within the existing provider package; no new hosted service or SDK abstraction is justified.

### CEO decision ledger and scope proposals

Mode provenance: /autoplan explicitly overrides mode to SELECTIVE EXPANSION. Principles P1–P6 authorize routine decisions; taste is provisional until the final gate. Existing user D1–D8 approvals remain binding.

| ID and owner | Contract and evidence | Current | Proposed | Status | Exact approval and scope |
| --- | --- | --- | --- | --- | --- |
| C0 / CEO | Office-hours D1–D8, issue #891 | Approach B and all invariants | Retain | approved | User D8/A; no cuts or ownership expansion |
| C1 / CEO, CLI | Prior minor R2-1 leaves broken-output behavior unclear | Best effort cancellation envelope, exit 130 | Stop writes after first output failure, preserve 130 on caller cancellation; test broken stream before/during envelope | approved | Autoplan P1/P5, mechanical, completes already-required output/cancellation behavior; human ~30m / agent ~10m, estimated, low risk, two existing/new command-test files |
| C2 / CEO, docs | Exit distinctions are easy to lose in CI | Exit table, two examples | Add a copyable shell recipe handling all five exits and preserving command status without retrying | approved | Autoplan P2, in direct CLI/doc blast radius, low risk, human ~30m / agent ~10m estimated; examples and CLI guide, no new runtime behavior |
| C3 / CEO | User needs permission troubleshooting | Safe cause/fix diagnostic docs already required | Separate IAM doctor/preflight | declined | P4 duplicate presentation; extra provider operations rejected under C0 ownership |
| C4 / CEO | Exact-version compatibility must be visible | Manifest and reference already required | Separate support certificate/dashboard | declined | P4 duplicates manifest, adds unconsumed process artifact |
| C5 / CEO | Resume after a CI process restart | Stateless observation | Persist observations and resume/watch command | declined | P5 new state/command lifecycle, no demonstrated need; no TODO because rejected rather than deferred |

Five adjacent delight candidates were evaluated individually above; C1/C2 accepted, C3–C5 skipped. Options differ in kind, no completeness score. For C1, keep ambiguity vs specify bounded best effort: choose explicit failure behavior; for C2, bare table vs runnable recipe: choose the recipe; for C3/C4 choose reuse over duplicate infrastructure; for C5 retain statelessness over persistence. Accepted additions work without any pending user change. No taste or user challenge has been identified at Step 0. No new TODO is required.

The evidence worksheets have an actual consumer: the implementing owner and accepting maintainer use them to decide format certification and reject unavailable R facts. They are the smallest per-field provenance record for two required consumers, retained alongside fixtures while that compatibility version is supported; replacement fixtures retire old versions through the same acceptance process. This does not add another report or runtime consumer.

<!-- autoplan-accepted:ceo -->
- Preserve the complete approved #891 source contract, including the exact-execution-only read, immutable captured configuration, required evidence matrix, UID continuity, same-Execution generation proof, Unknown boundaries, legacy compatibility, monotonic total budget, byte caps, zero mutation, two neutral consumers, documentation and separate line accounting.
- On caller cancellation, the CLI attempts its single safe cancellation envelope only while no output failure has occurred. After the first stdout write/flush failure, it suppresses further stdout/stderr writes and returns 130; a failed cancellation-envelope write also returns 130. Ordinary output delivery failure returns 2. It never retries an envelope or claims successful delivery of a partial object. Add command-path tests for a stream broken before cancellation and a failure during the cancellation-envelope write, with safe-output/no-raw-data assertions and required test cards.
- Include a copyable CI shell recipe in the CLI guide and neutral examples that captures the command's exit status and handles 0, 1, 2, 3 and 130 distinctly without automatically retrying, cancelling or submitting a workload. Verify the recipe against installed-tool or equivalent command-path fixtures; label provider evidence separately.
- Sequence concrete exact-version evidence feasibility before observer/CLI integration. Failure to provide any required R fact blocks certification and returns to design; no always-Unknown delivery or current-Job fallback meets acceptance. Rebase against refreshed main and repeat command, diagnostic and package collision checks before implementation.
- Replace the original cancellation paragraph with the final C1 wording and label v1/v2 as candidate shapes until exact-version acceptance; retain original behavior and evidence requirements. The appended accepted obligations are normative, while the earlier office-hours concern block is historical review evidence.
- Acceptance checklist: #891 AC1 maps to the Public API contract, evidence matrix and terminal classifier fixtures; AC2 to exact path/UID and swapped-execution tests; AC3 to provider-read diagnostics versus Failed/true; AC4 to runner fixed-argv allowlist and zero-mutation process assertions; AC5 to API/CLI monotonic entry budgets, streaming caps, cancellation and cleanup tests; AC6 to unchanged render/VerifyAsync regressions; AC7 to reference/adoption boundary docs; AC8 to both neutral consumers with distinct execution/image identities; AC9 to actual separate implementation/test added/removed line counts in the completion report. Every new test retains its protects/fails_when/no_claim card.
- Feasibility go/no-go: complete both neutral worksheets first; any unavailable R fact or unproven D omission blocks that format immediately. Record which candidate failed and why; attempt another candidate only if a documented mapping can supply the missing proof. If none can, stop integration and return to design. Only accepted fixtures, version/OS/launcher manifest, state table and named maintainer acceptance unlock observer/CLI integration and compatibility claims. Documentation fixtures cannot be relabeled provider-observed.
- Document a safe caller action for every diagnostic category: repair local input; hold on identity/configuration conflict; inspect tool/auth/network for provider failure; escalate unsupported/stale evidence to compatibility review; investigate overflow/cleanup; allow only an explicit caller decision to start another bounded observation after Running/Unknown. Add no automatic workload or observation retry policy, alert threshold or telemetry service.
- Keep normalization helpers small and explicit by identity/configuration/completion responsibility. Set and document a finite JSON depth and collection admission within existing byte limits, and test exact admission boundaries without weakening any required evidence field. Preserve public/internal documentation and no-reflection test seams.
- Enforce one result/cleanup owner: do not parse/classify before process exit and both readers settle; latch any nonzero/overflow/read/cleanup failure so later callbacks cannot publish success. Sequential polling keeps request state isolated. Test stdout/exit completion in both orders, either stream failing first, caller cancellation before/after internal deadline and read failure after Running using controlled pause/release points, preserving the caller-token precedence contract.
- Keep one implementation wave/PR by default, with no cut to the issue acceptance criteria. Follow-up real-adopter outcome measurement is C6 in TODOS.md and does not become a new release gate; retain both neutral consumers as required contract proof.
<!-- /autoplan-accepted:ceo -->

### CEO spec review round 1

Read both current inputs through EOF; implementation read completed with 1–140 and 141–281 after an initial truncated response. Native reviewer score 8/10, substantive dimensions pass but four minor clarity findings: criterion mapping, C1 placement, candidate-shape status, operational go/no-go checklist. Five combo/sub dispatch attempts were rejected; configured Luna fallback completed. Treat issues as findings, not an unconditional PASS. Autoplan P1/P5 accepts these factual/clarity repairs without changing any approved runtime outcome.

<!-- autoplan-baseline-edits:ceo {"sourceSha256":"158bc75537c78983005febd8f378378cb08b7a4fbd8ac363a54aa0640ff7f7f0","replacements":[{"oldText":"The API propagates caller cancellation; the CLI catches only its own cancellation token and emits an Unknown/false envelope with a caller-cancelled diagnostic when output is still writable. The exit remains 130. Output failure cannot be reported as successful observation delivery.","newText":"The API propagates caller cancellation; the CLI catches only its own cancellation token. It attempts one Unknown/false caller-cancelled envelope only while no output failure has occurred. After the first stdout write/flush failure it suppresses further stdout/stderr writes. Caller cancellation returns 130 even when the envelope cannot be delivered; ordinary output delivery failure returns 2. Never retry an envelope or claim successful delivery of a partial object. C1 requires command-path tests for already-broken output and failure during the envelope write."},{"oldText":"Support concrete v1 `metadata/spec/status` and v2 `name/job/template/conditions` forms through the same normalizer.","newText":"The v1 `metadata/spec/status` and v2 `name/job/template/conditions` forms are candidate shapes pending exact-version compatibility-manifest acceptance. Implement admitted concrete forms through the same normalizer; no shape is supported merely because it resembles a documented schema."}]} -->

### CEO Step 0 completion and temporal interrogation

Spec round 2 read both inputs fully: score 9/10, overall PASS, all five dimensions PASS, four prior minor issues confirmed resolved. Required metrics persisted: iterations 2, found 4, fixed 4, remaining 0. Autoplan P6 approves these consistent document versions for continued review; final owner approval remains pending. The immutable Step 0 checkpoint stays `autoplan-ceo-erBUef/ceo-implementation.md`; the voice input is the fresh `autoplan-ceo-XaLiao/ceo-implementation.md`, hash `82b8d520845270c07dfdac801b0f941c7573f914f2b58a717f664dd7a13ecb29`.

| Stage | What the implementer must resolve | Gate and estimate |
| --- | --- | --- |
| Foundation / hour 1 | Rebase, command/diagnostic audit, concrete fields and provenance in both worksheets | Stop a shape on any unavailable R fact or unproven D rule; estimated human 4–6h / agent 1.5–2h plus separately authorized provider evidence |
| Core / hours 2–3 | Strict normalized snapshot, complete inventory, state/counter table and null/default behavior | Only accepted fixture/manifest/state table unlock integration; estimated human 5–7h / agent 1.5–2h |
| Integration / hours 4–5 | One deadline, bounded seam, Windows launch, caller cancellation, safe argv admission | Native-process proof on declared OS; estimated human 5–7h / agent 2–3h |
| Polish / hour 6+ | Installed consumers, CLI status recipe, docs, branch coverage and package closure | Preserve source gates; estimated human 5–8h / agent 2–3h |

The hour labels describe ordering, not a promise to fit all work in six hours. The original estimate remains an estimate; actual provider access, maintainer acceptance and OS proof can add elapsed time. No calendar cutoff can waive an R fact. Implementation owner must identify a maintainer and evidence source before beginning integration.

### CEO voices and finding dispositions

Fresh native review completed with matching INPUT hash. Outside preflight rechecked: `CODEX_MODE: not_installed`, Claude Code unavailable; no external invocation or replacement provider. Native reviewer is the configured Luna fallback after five rejected combo/sub attempts, a same-harness independent review. Native findings are not outside coverage.

| Native finding | Disposition and reason | Decision |
| --- | --- | --- |
| N1 certification before value | Feasibility milestone already precedes integration; keep all required parity fields | T1 taste: provisionally retain C0 full contract under P1; final gate |
| N2 separate minimal evidence profile | Would change the user-selected API and weaken full configuration acceptance | T1 alternative, not applied; original stands |
| N3 named adopter validation | Useful outcome evidence, beyond required two neutral consumers | C6 deferred to TODO, P3; does not block or replace existing acceptance |
| N4 six-month always-Unknown regret | Real risk; existing go/no-go explicitly prevents shipping that result | Mechanical retention of feasibility gate, no new scope |
| N5 REST/SDK/adapter alternatives | Add comparison below; gcloud deliberately retains existing package/tool footprint | Mechanical analysis; no transport change |
| N6 schema versus actual response | Already explicit: candidate only, per-field source, no manufactured observed fact | Keep, required worksheet proof |
| N7 Unknown actionability | Spell out safe caller actions per diagnostic; no automatic retries or recovery | C7 approved P1/P5 documentation, no new runtime |
| N8 public API before evidence stabilizes | Freeze public API only after feasibility; field/profile API would change C0 | T1 retained contract; no extra result fields |
| N9 sequencing contradiction | Current appended go/no-go already unlocks integration only after acceptance | Factual correction to reviewer premise; phases in temporal table |
| N10 conventional competition | Differentiator is bundle-to-selected-execution proof; Console/Workflows do not remove that comparison | Keep package wedge; no claim of market demand |
| N11 process correctness first | These are explicit user bounds; certification is limited to proven OS/launcher combinations | Keep all bounds and required process tests, no relaxation |
| N12 synthetic versus real adoption | Agree proof boundaries; real outcome workflow joins C6 follow-up | Deferred, neutral consumers remain mandatory |
| N13 measured value | Do not invent savings; measurement is C6 after a real adopter exists | Deferred, separate from correctness gates |

| Transport | Identity/config facts | Footprint/credentials | Test/version behavior | Decision |
| --- | --- | --- | --- | --- |
| gcloud exact describe | Candidate concrete v1/v2 output; must prove complete matrix | Existing installed tool and credentials; subprocess/launcher cost | Strict fixtures, exact versions/OS; byte/time bounds required | Retain user-approved existing-package route, gate feasibility first |
| Direct REST or SDK | Canonical v2 schema; still must prove all facts/defaults | New HTTP/auth or SDK dependency and release surface | Avoids CLI text/launch drift; HTTP/auth/retry/version proof replaces it | Valid future redesign if gcloud cannot meet contract; not silently substituted |
| Caller-supplied semantic adapter | Can supply selected evidence but could manufacture facts | Smaller package transport footprint, larger caller implementation burden | Every adapter must honor provenance/deadline contract | Does not remove classifier obligation; no generic plugin framework now |

CEO DUAL VOICES — CONSENSUS TABLE:

| Dimension | Native | Claude Code | Consensus |
| --- | --- | --- | --- |
| Premises valid | feasibility concern | unavailable | N/A |
| Right problem | useful, adopter evidence absent | unavailable | N/A |
| Scope calibrated | prefers smaller evidence profile | unavailable | N/A |
| Alternatives explored | transport comparison requested | unavailable | N/A |
| Competitive risks | bundle-proof differentiator needed | unavailable | N/A |
| Six-month trajectory | no always-Unknown shipment | unavailable | N/A |

### Current scope — Section 1: Architecture Review

SELECTIVE EXPANSION, provenance /autoplan override. C0/user D8 retains full #891 contract; C1/C2 accepted by P1/P2/P5; C3–C5 rejected by P4/P5; C6 real-adopter measurement deferred by P3; C7 Unknown runbook accepted by P1/P5. T1 provisionally keeps original full evidence profile; no user-direction change is assumed.

The boundaries are appropriate: the neutral package remains provider-free; only the GCP package handles execution evidence; the CLI depends on that package and adds no competing classifier. The main architectural risk is coupling proof to a concrete tool format, so feasibility precedes integration and each admitted shape has independently sourced fixtures. No new persistent state or public endpoint is introduced. At tenfold concurrency, independent processes/API quota dominate; at hundredfold, caller-owned scheduling must limit parallel calls. No cache or batching is added because exact per-request freshness and budgets are required.

```text
BEFORE: CLI -> existing features       GCP target -> legacy runner -> Job/IAM reads
AFTER:  CLI -> bounded bundle loader -> observer -> bounded runner -> exact Execution read
        API ----------------------------^           |
        GCP target -> shared bundle validator       v
                  immutable expectation -> normalizer -> pure classifier -> safe observation
        legacy target -> legacy runner (existing semantics retained)
```

Happy path is valid bundle + exact matching execution -> immutable result. Nil request throws ArgumentNullException; non-null missing deployment becomes safe Unknown before provider access. An empty artifact set/string fails admission with zero reads. Provider failure becomes Unknown after bounded cleanup; no prior Running result is promoted. The process and gcloud/auth are single integration points; failures are visible safe diagnostics. Rollback is caller adoption rollback to its previous observer, no state migration; operational time depends on the caller release pipeline, not a claimed measured duration.

### Section 2: Error and rescue map

Every boundary has a typed failure outcome; free-form exception messages and raw stderr never enter diagnostics. Caller cancellation is identified by the caller token and rethrown after cleanup; internal deadline cancellation is converted to evidence failure. Broad catches are prohibited as classification policy; any final CLI host boundary retains safe generic failure only for explicitly documented unexpected defects, with no success delivery claim.

| Method/codepath | Failure / exception class | Rescue and user result | Required proof |
| --- | --- | --- | --- |
| ObserveAsync admission | ArgumentNullException | throw for null request | public null test |
| ValidateRequest/Bundle | invalid values; DeploymentValidationException; JsonException; FormatException | safe Unknown, zero provider reads | corrupt/missing/duplicate/hash/binding/limit cases |
| LoadBundleAsync | IOException, UnauthorizedAccessException, invalid/reparse path | safe Unknown / exit 2 | path/size/replacement/read error |
| BoundedRunner start | Win32Exception, InvalidOperationException; unsafe token | unavailable-tool or launch diagnostic / Unknown | absent tool, launch failure, exact argv |
| CaptureBytesAsync | IOException, DecoderFallbackException, overflow result | stop tree, settle both readers, Unknown | both-stream limits, invalid encoding, read failure |
| Run/cleanup | internal deadline; cleanup failure | safe Unknown; no stale Running fallback | clock and native child cleanup |
| Normalize/Classify | JsonException, malformed type, duplicate/unsupported facts | safe Unknown, no partial parse | per-field matrix negative cases |
| Observe/delay | caller OperationCanceledException | cleanup then propagate; CLI 130 | token before/during read/delay, race with deadline |
| RenderResultAsync | IOException, ObjectDisposedException | stop further writes; 2, or 130 on caller cancellation | broken-output C1 command tests |

Nine codepath rows mapped, zero unresolved rescue gaps. Expected malformed evidence uses a typed parse failure, not exception-driven success fallback. No hidden provider-read retry is introduced for auth, rate limits or nonzero exits.

### Section 3: Security and threat model

The new attack surface is local argv, bundle files, provider JSON and process output. The API accepts caller-supplied expected facts rather than authenticating the bundle; hashes establish consistency only. Read authority is inherited from the invoking user's gcloud credentials, scoped by the exact project/region/Job/execution; the package never broadens IAM or discovers an alternate identity. No secret payload is read deliberately or persisted in diagnostics.

| Threat | Likelihood / impact | Mitigation and disposition |
| --- | --- | --- |
| shell/control-token injection | medium/high | anchored resource/ID grammar, fixed command and safe launcher; zero mutation test; retain C0 |
| unsafe path/reparse/replacement | medium/high | reject chain links, bounded snapshot, cross-artifact consistency; trusted immutable source limitation explicit |
| hostile oversized/duplicate JSON | medium/high | byte caps before parse, bounded depth and strict typed inventory; never parse truncated bytes |
| data leakage through error/help | medium/high | pre-parser command admission, fixed safe diagnostic text, validated identifiers only; command-path raw-value tests |
| credentials or SDK side effects | low/high | no new SDK, fixed read command/noninteractive flags; documented existing CLI credential ownership |

One security refinement is mechanical under P1/P5: use an explicit parser maximum depth and bounded collections within admitted byte limits; determine and document values alongside compatibility fixtures, then test exact boundaries. This implements existing finite-input guarantees rather than accepting arbitrary nested JSON. No new service-level audit store is warranted; the host's invocation history and safe observation are the operational handoff.
### Section 4: Data flow and interaction edge cases

The observer owns one immutable expectation and per-call UID/deadline/last-result state. There is no shared request state or cache, so separate concurrent callers cannot replace each other's selected execution. API and CLI return the same classification; CLI adds file admission and output delivery only. Double invocation creates two independent reads, never two submissions. A cancelled terminal command stops its local observation; the remote Job continues.

```text
INPUT -> VALIDATE -> NORMALIZE -> CLASSIFY -> [no persistence] -> OUTPUT
 nil  -> null request throws; missing member -> Unknown
 empty/wrong/too long -> safe invalid diagnostic, zero provider calls
 files changed -> snapshot hash/cross-artifact mismatch -> Unknown
 stale/duplicate/partial/encoding -> reject whole evidence -> Unknown
 upstream failure/timeout -> kill+settle streams -> Unknown
 matching Running -> bounded delay -> next exact read
 last matching Running + clean idle deadline -> Running (original receipt time)
```

The main async invariant is that a result can be delivered only after the process exit and both stream readers are settled, and after cancellation/deadline precedence has been evaluated. A nonzero exit cannot be hidden by stdout completing first. The implementation must use one join/cleanup owner rather than racing callbacks that publish results independently.

```text
ORDER A                 STDOUT              STDERR/EXIT          SHARED
 read completes         complete bytes      still running        no result
 exit arrives                              nonzero              failure latched
 stderr completes                          bounded bytes        settle -> Unknown

ORDER B                 STDOUT              STDERR/EXIT          SHARED
 overflow/read error    failure             still running        failure latched
 exit/cleanup                              settles              no parse/success
 both settled           settled             settled              Unknown

CANCEL/DEADLINE A        caller token        internal deadline    decision
                        cancelled           later                cleanup -> OCE
CANCEL/DEADLINE B        checked at return   expired first        if now cancelled -> OCE
                        remains clear       expired              Unknown or idle Running rule
```

Controlled pause/release tests cover stdout-first and exit-first, each stream overflowing first, caller cancellation before/after internal deadline, and read failure after Running. Sequential polling prevents overlapping read/delay updates; UID is pinned before the next iteration. Bundle-load replacement is either one coherent immutable snapshot or fails consistency; no claim is made against an adversarial mutable directory.

| Interaction | Edge | Handling |
| --- | --- | --- |
| API call | null / empty / invalid duration | null throws; non-null invalid -> Unknown, no provider |
| CLI invocation | malformed value or duplicate options | safe admission before generic parser, no raw echo |
| repeated invocation | another execution succeeds | exact resource + captured config remain required |
| wait | deadline during delay | last matching Running only under clean idle rule |
| wait | provider read fails after Running | Unknown with new failure-decision time |
| Ctrl-C | during load/read/delay/output | local cancellation, cleanup, exit 130, single output attempt |
| output | broken pipe or partial write | C1 stops further writes; no success-delivery claim |
| provider workload | failed/duplicate/backlogged | classify selected evidence only; never resubmit |

Eight interaction rows mapped, none left without a specified handling policy. The regression checks above implement already-approved behavior and P1 completion, rather than introducing workload scheduling.

### Section 5: Code quality review

The validator extraction is justified because two real callers require identical bundle integrity; it must preserve VerifyAsync's existing semantics through regressions. Execution parsing deliberately remains distinct because strict presence, duplicate detection and generation/counter rules differ from the permissive Job parser. Argument launch validation may be reused, but no adapter may call the legacy unbounded capture path.

One mechanical maintainability refinement (C8, P5): organize normalization by identity, task configuration and completion evidence, each returning an explicit failure; keep the poll loop as a short sequential coordinator. Review any method with more than five independent branches for a small named helper split, without adding a new framework, base class or pluggable policy layer. Internal seams are exposed intentionally to tests, never by reflection. Names describe facts and outcomes; diagnostics are fixed codes with safe text.

### Section 6: Test review

Coverage follows the public caller path as well as pure helpers. Deterministic time proves admission/deadline decisions; native child fixtures prove actual byte/pipe behavior for each declared OS. Installed-tool tests prove command registration, packaging, safe argv admission, JSON and exit behavior. A schema fixture proves parsing, never cloud acceptance or a production migration.

```text
API/CLI admission [unit + command] -> immutable bundle [unit + file integration]
 -> bounded runner [native process] -> exact read [fake argv recorder]
 -> strict normalizer [unit matrix] -> classifier [unit state/counter matrix]
 -> poll/delay/cancel [controlled clock + pause gates]
 -> JSON/text/exit [command + installed tool]
 -> two neutral consumers [packed API/tool proof]
```

| Behavior | Observable assertion / wrong result rejected | Test layer | no_claim |
| --- | --- | --- | --- |
| bundle/identity | invalid bundle causes exactly zero reads; other execution never succeeds | public API + command | provider permissions (fake read) |
| all config fields | any required mismatch/absence fails complete match | pure + API matrix | provider-emitted field availability (fixture) |
| states | exact documented success/failure/cancel/running/unknown matrix | pure + API | undocumented state tokens |
| generation/UID | stale/current mismatch or changed UID -> Unknown | sequential API | continuity before first response |
| deadlines | no round-up; total budget includes validation/drain; last Running timestamp retained only on clean idle expiry | fake clock + API/CLI | blocked OS start preemption |
| byte bounds | exact cap allowed; cap+1, invalid UTF8 and either-reader failure -> Unknown without parsing | native process | universal OS kill guarantees |
| cancellation | caller OCE precedence, both readers settled, CLI 130 | fake clock + native/command | remote workload cancellation |
| delivery | one safe object; no second attempt after failure; C1 exit precedence | command + installed tool | successful write to broken pipe |
| compatibility | old custom IGcloudCommandRunner still compiles; render/Verify unchanged | packed consumer + regressions | every external implementation |
| neutral consumers | distinct Jobs/images work; swapping either cannot satisfy other | packed API/tool | actual adopter recovery |
| accounting/docs | actual additions/removals separated; all named links and defaults agree | completion diff + doc contract | net savings/performance |

Each new test carries the required protects/fails_when/no_claim card. Friday confidence comes from the installed CLI returning all exits for both consumers; hostile QA targets swapped successful execution plus omitted image/network fact; chaos combines an overflowing stderr child, open descendant pipe and concurrent cancellation. These are existing bound/identity requirements exercised together, not live-cloud fault injection. Unit tests dominate; fewer file/process tests and a small packed-tool proof prevent an inverted pyramid. Scheduling tolerance is reported separately, never used to enlarge the configured runtime budget. No LLM/prompt change or eval suite applies.

### Section 7: Performance review

The expensive path is gcloud startup/auth/network plus provider latency, not classifier computation. There is one sequential exact read per iteration and no N+1 Job/IAM enrichment, hidden retry, cache refresh or database query. The top slow paths are process/provider read, bundle file admission, and cleanup of inherited pipes; each consumes the same remaining monotonic budget. No p99 or startup latency was measured, so no numerical performance promise is made beyond the specified admission and budget contracts.

Retained inputs are finite: three artifacts at the proposed 4 MiB cap each, stdout at 1 MiB and stderr at 64 KiB, plus the one-byte overflow probes. These are byte-retention limits, not a claim that total managed memory equals their sum: decoding, JSON DOM, immutable expectations and concurrent reader buffers add bounded overhead. Maximum JSON depth and collection admission prevent small-byte/deep-object exhaustion. No DB index, connection pool, persistent cache or background worker applies. Concurrent callers multiply process/memory/quota costs; the host must bound concurrency. Required byte-boundary and deterministic-budget tests cover the frequent path without an unrelated throughput benchmark.

### Section 8: Observability and debuggability review

The public observation is the operational handoff: safe identity, state, configuration match, UTC receipt/decision time and stable diagnostic. It deliberately excludes provider text, environment, commands and exception details. Host metrics can count outcomes and diagnostic codes without introducing a telemetry service into this library. Log safe entry/exit category and local correlation in the invoking application if needed; never add progress to JSON stdout. No cross-service trace propagation is possible through a read-only describe alone, and no request-specific cloud label is created.

C7 resolves the operational gap with a diagnostic runbook. Local admission errors mean repair bundle/arguments and stop automation; identity/configuration conflicts mean hold the caller decision and compare selected execution/submission; unavailable tool/auth/provider means inspect installation/credential/permission/network in the caller context and decide separately whether to observe again; unsupported/malformed/stale evidence means preserve uncertainty and escalate compatibility review; overflow/cleanup failure means stop automation and investigate the tool/process; clean Running deadline means the last read was nonterminal and the caller may explicitly choose another bounded observation. Caller cancellation means local observation stopped, never remote cancellation. No automatic retry is prescribed by the package. An adopter may alert on sustained Unknown/cleanup failures and chart state/diagnostic counts; no numeric alert threshold is invented without deployment traffic. Reproducing a defect requires the exact supported tool version/OS/format and sanitized fixture under controlled handling, not dumping secrets to logs.

### Section 9: Deployment and rollout review

This feature changes a coordinated library/tool release, not a database or cloud deployment. The existing verifier and runner remain available; the new bounded runner is additive. The first rollout risk is a CLI/package graph missing the GCP assembly or registering different services from direct library callers; packed consumer proof must cover both. The second is legacy validator extraction drift; render/Verify regressions block release. The third is unsupported-platform overclaim; only manifest-proven versions/OS launchers receive compatibility statements. No feature flag is necessary for a new explicit command/API with unchanged defaults elsewhere.

```text
refresh/rebase -> evidence worksheets + maintainer acceptance
 -> API/parser/runner + regression proof -> CLI + two consumers
 -> format/build/docs/coverage -> packed library + installed tool
 -> separately authorized coordinated release -> first explicit adopter

bad adoption -> stop caller's rollout -> restore prior observer/tool version
            -> keep existing Job and application state -> inspect safe diagnostic
```

First-use checks are command help, safe invalid-input JSON, both neutral positive/swapped cases and no provider mutation; a production execution read requires separate authorization and is labeled separately. During the first operator window, inspect diagnostic frequencies and false success/Unknown reports; the duration follows that deployment's policy rather than an invented universal hour gate. Mixed old/new consumers are additive; use pinned package/tool versions and do not delete the old application path before acceptance. Reverting the feature has no persisted state or cloud mutation to reverse.

### Section 10: Long-term trajectory review

Reversibility is 4/5: callers can restore their previous observer without data migration, but published API/diagnostic/JSON contracts need compatibility discipline. The principal debt is maintaining admitted CLI shape fixtures and supported OS process behavior. XML/reference docs, the state table and both examples reduce knowledge concentration. Three explicit debt categories remain: version maintenance, provider evidence drift and cross-platform process evidence; none licenses weakening a required check.

The one-year engineer should see a strict execution-evidence primitive in the existing deployment package, with submission and acceptance still owned by the application. The next feature can reuse the pure classifier only if its evidence/ownership contract actually matches; there is no generic observation framework to maintain now. T1's minimal-profile alternative is viable as a separate user-approved redesign if feasibility fails, but it cannot be assumed for this implementation. C6's real-adopter measurement is a useful later follow-up; no rejected expansion became load-bearing.

### Section 11: Design and UX review

SKIPPED (no UI scope). The plan adds a CLI/library contract and documentation, with no screens, visual components, frontend assets or design-system change. Developer-facing states and command ergonomics are carried into the required DX phase.

### NOT in scope

Deferred: C6 real-adopter outcome/Unknown-rate measurement, recorded in `TODOS.md` under “Measure real Cloud Run observer adoption after #891”; depends on a certified package, consenting adopter and separately authorized access/release. This does not defer any issue requirement.

Rejected C3 IAM doctor, C4 separate support certificate/dashboard, C5 persisted resume/watch command. Submission, remote cancellation, workload retry, sequencing, infrastructure/IAM mutation, recovery, application acceptance and a general deployment orchestrator remain excluded by user C0. No public minimal profile or per-field result collection has been approved.

### What already exists

| Subproblem | Existing authority | Reuse |
| --- | --- | --- |
| immutable artifact bytes/hash | DeploymentContracts.cs:452 | use unchanged |
| cross-artifact identity/bindings | GcpCloudRunDeploymentTarget.cs:88 and :141 | extract one internal validator, preserve legacy behavior |
| immutable image grammar | DeploymentContracts.cs:99 | revalidate before expectations |
| safe launcher/token grammar | GcpCloudRunDeploymentTarget.cs:615 | reuse compatible launch rules, never unbounded capture |
| CLI cancellation/duration | CanaryPollCommand.cs:108 and :139 | follow conventions, own observer command path |
| coordinated packages/tool | package closure and release workflows | extend existing proof, no new package |

### Dream state delta

This plan removes divergent interpretation as a capability, subject to evidence feasibility. It does not prove production adoption, eliminate operator work, authenticate the expected bundle, prove submission provenance before the first read, or establish application acceptance. The next meaningful delta is C6 evidence from an actual caller, not another certification dashboard.

### Failure modes registry

| Codepath | Failure | Rescued? | Test? | User sees | Logged? |
| --- | --- | --- | --- | --- | --- |
| admission | invalid/missing/corrupt bundle | yes | required | Unknown/invalid diagnostic | safe result |
| identity | other execution/Job or changed UID | yes | required | Unknown identity code | safe result |
| normalize | malformed/duplicate/unsupported/stale | yes | required | Unknown evidence code | safe result |
| configuration | any operational mismatch | yes | required | Unknown/false mismatch code | safe result |
| provider | tool/auth/read/nonzero failure | yes | required | Unknown provider category | safe result |
| capture | overflow/encoding/reader error | yes | required | Unknown bounded-output code | safe result |
| cleanup | deadline/tree/pipe failure | yes | required | Unknown cleanup/deadline | safe result if writable |
| idle deadline | matching Running last read | yes | required | Running + original receipt, exit 3 | safe result |
| cancellation | caller token | cleanup + rethrow | required | CLI 130, best effort safe object | safe result if writable |
| delivery | broken pipe/partial object | yes | required | exit 2 or cancellation 130 | no further output possible |

Ten rows, zero unhandled silent failures in the plan. “Required” denotes future tests, not completed tests. Output failure is an explicit process-exit signal rather than a fictional successfully logged diagnostic.

### Scope expansion decisions and approval readiness

Six product proposals evaluated: C1/C2 accepted, C6 deferred, C3/C4/C5 skipped. C7 runbook, C8 helper organization and bounded parser admission are mechanical completion of existing obligations. Spec repairs are traceability/clarity changes. All dispositions derive from /autoplan P1–P6 or exact user C0 approval; no accepted requirement is cut. T1 remains a final-gate taste item with original full parity provisionally selected. Approval readiness: PASS for continued pipeline, checked C0–C8 and four spec fixes; final owner gate remains open.

### Stale diagram audit

The original pipeline diagram remains accurate. Added architecture, shadow data flow, async schedules, test graph and rollout/rollback diagrams reflect retained stateless boundaries. Runtime README/reference diagrams will be audited when the implementation edits those files; none was edited in this planning run.

### Observer state and error flow

```text
Admit -> Read -> Settle process/readers -> Normalize/Classify
  |         |             |                  |
 invalid    error         failure            Unknown/terminal -> Return
  v         v             v                  |
Unknown   Cleanup ------> Unknown             matching Running -> Wait -> Read
 caller cancellation from any active state -> Cleanup -> throw OCE
 idle deadline from Wait -> Return last Running with original receipt
 no evidence / in-flight deadline -> Cleanup -> Unknown
```

Impossible transitions: no Read before admitted bundle; no classify before both streams/exit settle; no Waiting after Unknown or terminal; no success after cleanup failure. A single owner and failure latch enforce these transitions; tests control each await ordering rather than assuming one favorable schedule.

### CEO implementation tasks

All work is future implementation and follows the feasibility gate. Estimates reflect research/architecture/OS proof, not a blanket code-generation multiplier; human total about 19–28h / agent about 7–10h, excluding external wait and separately authorized live reads.

- [ ] **T1 (P1, human ~4–6h / agent ~1.5–2h)** — evidence — Rebase, audit collisions and prove candidate exact-version worksheets before integration.
  - Surfaced by: PR5 and native N1/N4/N6/N9; no format is certified.
  - Files: future `Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun.Tests/Fixtures/ExecutionEvidence/v1/` files and compatibility manifest.
  - Verify: every required field and documented omission, two independent positive/swapped fixtures, hashes, version/OS/argv and named maintainer acceptance; stop on unavailable R proof. No live read implied.
- [ ] **T2 (P1, human ~4–6h / agent ~1–2h)** — API/evidence — Extract the validator and implement strict normalizer, classifier and stable public contracts.
  - Surfaced by: Sections 1/3/5/6; legacy parser/capture cannot prove execution facts.
  - Files: GCP target, future observer/contracts/helpers, existing target tests and future observer tests.
  - Verify: all matrix/state/identity/UID/generation branches, malformed data, zero-read local rejection and unchanged render/Verify behavior.
- [ ] **T3 (P1, human ~4–6h / agent ~2h)** — process/polling — Implement the additive bounded runner and single-deadline observer loop.
  - Surfaced by: Sections 2/4/7; legacy ReadToEnd and unjoined cleanup are insufficient.
  - Files: future GCP bounded runner and observer, existing/future runner tests.
  - Verify: exact cap and cap+1 for both streams, UTF8, process/pipe settlement, cancellation precedence, deterministic budgets, actual declared OS launch behavior.
- [ ] **T4 (P1, human ~3–4h / agent ~1–1.5h)** — CLI — Register the command with safe admission, bundle snapshot, one-object output and exact exits.
  - Surfaced by: C1 and Sections 2/4/6; command does not yet exist.
  - Files: `Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs`, CLI project, future command and tests.
  - Verify: installed help/registration, invalid argv safety, all exits and C1 broken-stream/cancellation cases.
- [ ] **T5 (P2, human ~2–3h / agent ~1h)** — docs/examples — Publish full reference/runbook and two runnable neutral consumers with C2 recipe.
  - Surfaced by: C2/C7, Sections 6/8/10.
  - Files: GCP README, Deployment README/reference, CLI README, package chooser and future neutral examples.
  - Verify: every API/default/limit/diagnostic/state/ownership link, recipe status preservation, swapped evidence; no real-adoption claim.
- [ ] **T6 (P1, human ~2–3h / agent ~0.5–1.5h)** — packaging/verification — Run format, focused tests, warning/doc checks, packed library/tool proof and practical solution coverage sequentially.
  - Surfaced by: Sections 6/9 and user repository gates.
  - Files: existing package metadata/closure and consumer proof files, locations confirmed after rebase.
  - Verify: actual commands/exits/counts, no concurrent shared-output verifiers, no warning/test-gate relaxations, actual implementation/test line additions/removals separately.

_No new implementation tasks from Section 10 beyond documented maintenance and the separately deferred C6 follow-up. Section 11 has no UI scope._

### CEO completion summary

| Field | Result |
| --- | --- |
| Mode | SELECTIVE EXPANSION |
| System audit | existing validator/launcher reusable; legacy capture/parser insufficient; command prospective |
| Step 0 | approach retained; two spec rounds, final PASS 9/10; feasibility first |
| Section 1 Architecture | one principal format-coupling risk, gated |
| Section 2 Errors | nine codepath rows, zero unresolved gaps |
| Section 3 Security | five threats mapped; finite parser admission refinement |
| Section 4 Data/UX | eight interactions; controlled ordering schedules; zero unhandled |
| Section 5 Quality | one helper-organization refinement |
| Section 6 Tests | graph and eleven assertion rows; zero missing planned contract branches |
| Section 7 Performance | finite retained bytes; no measured latency; host concurrency boundary |
| Section 8 Observability | one runbook clarification, no new telemetry service |
| Section 9 Deploy | three risks; coordinated package/tool proof and caller rollback |
| Section 10 Future | reversibility 4/5; three maintenance-debt categories |
| Section 11 Design | SKIPPED, no visual UI scope |
| NOT in scope | one deferred candidate, three rejected proposals; user non-goals retained |
| Existing code / dream delta | documented above |
| Error/rescue / failure modes | nine / ten rows, zero critical gaps in planned handling |
| TODOs | one actual C6 entry saved |
| Scope proposals | six proposed, two accepted, one deferred, three skipped |
| CEO archive | saved and synchronized with scope decisions |
| Outside voice | Claude Code unavailable, not installed; native completed, 13 findings dispositioned |
| Lake Score | N/A; no coverage-scored human question |
| Diagrams | architecture, shadow data flow, async schedules, test graph, observer state, rollout/rollback |
| Stale diagrams | none in edited source; runtime documentation audit remains implementation work |
| Unresolved decisions | T1 final-gate taste; original full parity remains provisional, no user challenge |
| Reality | eleven applicable rows; one unverified feasibility premise |
| Timing caveat | timer start recorded only at close preparation; phase timing undercounts earlier work and is not a full duration claim |

All required source behavior remains in the implementation plan. No implementation test, provider execution or released-adoption evidence was produced by this planning review.

REALITY: premise-table pass inspected sources distinguish verified facts from unverified=1 provider-format feasibility Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:88
REALITY: already-done pass refreshed main; no observer symbol or execution command implementation at review time Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:88
REALITY: surface-check finding prospective observe-execution command and flags require registration and installed help proof Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs:87
REALITY: binding-decisions pass issue-specific office-hours answers retained; unrelated branch decisions do not change them docs/designs/issue-891-cloud-run-execution-observation.md:17
REALITY: numbers pass caps/defaults are requirements; effort is estimated; no latency or adoption measurement claimed docs/designs/issue-891-cloud-run-execution-observation.md:180
REALITY: deferred-asks pass all issue acceptance remains required; real-adopter measurement is an extra follow-up only TODOS.md:3
REALITY: pr-count pass use "default one PR per wave" because no ship-policy override exists; staged tasks do not split PRs docs/designs/issue-891-cloud-run-execution-observation.md:5
REALITY: prior-art pass diverge because existing package deliberately uses gcloud; official execution schema and exact describe docs retained; no vendored patch Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md:16
REALITY: contract-assumptions pass tool output remains candidate until exact-version fixture acceptance; no SDK or assumed field fallback docs/designs/issue-891-cloud-run-execution-observation.md:109
REALITY: dependency-fallback pass no certified format blocks integration; legacy verifier and application observer remain available docs/designs/issue-891-cloud-run-execution-observation.md:149
REALITY: obligation-tiers pass must ship all issue guarantees in the same wave; adopter outcome measurement deferred; no new infrastructure TODOS.md:3

PLAN_REALITY: phase=ceo rows=11/11 missing=none unverified=1 verdict=complete

### Phase 2 scope skip

Design review skipped: no visual UI scope in the detector or accepted additions. This is a skip, not a completed design review. DX remains required.

### DX methodology and Step 0 evidence

Loaded full DX methodology through EOF, ranges 1–600, 601–1200, 1201–1800, 1801–2137; 139482 bytes, SHA-256 `12e82fef9f988e30fa3dfb58ab9dfe6102026b080e543cd14051bc6671659444`. Mode DX POLISH is mandated by /autoplan. Product is primarily a .NET library plus CLI and documentation, not a hosted platform or skill. Persona/product/empathy/target confirmations are auto-selected under P6, with predictions explicitly unmeasured. Brain persona/competitive-intel unavailable; source docs provide audience evidence.

TARGET DEVELOPER PERSONA: internal .NET platform/backend developer, integrating an existing migration Job submission into CI. They expect explicit exact identity, noninteractive behavior, safe machine output, reusable testing seams and operational ownership boundaries. An unattended coding/CI agent is the secondary reader of errors. No observed abandonment threshold is available; assumed tolerance is a short documented terminal session on a prepared machine.

Developer Perspective (predicted first-person narrative): I already own submission of a Cloud Run migration Job, but my current verification only checks the deployed Job. I need to know whether this selected execution ran the intended immutable configuration. I open the deployment package README, which currently describes pure rendering and read-only configuration/IAM verification; it does not yet document an execution observer. I look in the CLI guide and find many existing command families, so I need an obvious link to the new execution command. I expect one exact example with a bundle directory and a full execution name, followed by output I can safely branch on in CI. I do not want the observer to launch another workload when I retry a read. If I get Unknown, I need to know whether I supplied the wrong identity, the tool could not read the provider, or the output format lacks proof. I need the support manifest before trusting a success. A local fixture example helps me understand those branches without credentials, but it does not establish that my provider version works. Before replacing my existing code, I want both the package API and installed command to handle swapped executions, cancellation and deadline Running identically. I will keep my application's acceptance and recovery decisions.

The narrative is inferred from inspected README/reference and approved issue requirements, not an observed user interview. It does not claim actual output from a command that does not exist yet.

#### Competitive benchmark and target

| Tool | Start to useful result | Time/evidence | DX choice | Source |
| --- | --- | --- | --- | --- |
| gcloud exact describe | installed/authenticated CLI plus known execution to returned metadata | unmeasured; official docs | familiar exact-execution read, classification caller-owned | https://docs.cloud.google.com/run/docs/managing/job-executions |
| AWS Batch describe-jobs | installed/authenticated AWS CLI plus Job ID to structured job status | unmeasured; official docs | one describe with structured status; different cloud/schema | https://docs.aws.amazon.com/cli/latest/reference/batch/describe-jobs.html |
| Azure job execution show | installed/authenticated az plus group/job/execution to JSON | unmeasured; official docs | explicit selected execution and standard JSON/help | https://learn.microsoft.com/en-us/cli/azure/containerapp/job/execution |
| AppSurface plan | prepared .NET 10/tool + certified gcloud/auth + trusted bundle + exact existing execution to understood safe result and correct caller branch | estimated 5–10 minutes before quickstart refinement; no human run | bundle-to-execution proof, bounded total observation and explicit exits | this design and CLI README |

The peer clocks are not measured or equivalent, so only DX choices are compared. Target selected under /autoplan P6: Competitive, 2–5 minutes from opening the new quickstart on the explicitly prepared machine to understanding one result and its safe next action, including reading the sample/exit table. Installing prerequisites, gaining cloud permission, rendering/submitting a Job and waiting for workload completion are separate prerequisites; they are not silently omitted from a fresh-start claim. Unknown for insufficient evidence is a useful diagnostic endpoint, not a successful configuration proof. No timing release gate or recurring analytics process is added.

Magical moment selected under P5: copy a complete exact-execution command from the existing CLI guide, see one safe observation, and branch correctly without writing another classifier. The same two neutral examples provide an offline fixture path using the injectable bounded seam; it is labeled fixture evidence and does not add a replay command or cloud claims. No hosted playground, new API or account signup is proposed.

| Stage | Developer does | Friction evidence | Resolution/status |
| --- | --- | --- | --- |
| Discover | package chooser -> deployment overview -> observer quickstart | current package README only describes configuration verification | required discoverability update retained |
| Install | choose coordinated library or dotnet tool; inspect supported gcloud version/OS | README release policy is linked; no observer support exists yet | exact-version compatibility and minimum prerequisites at quickstart top |
| Hello World | copy command with trusted bundle and full execution | command is prospective; no sample output exists | planned complete sample with safe annotated output; target unmeasured |
| Real Usage | call same API from submission owner | no application submission correlation in bundle | explicit identity handoff, neutral consumers, no inferred provenance |
| Debug | resolve code using safe runbook | code-only JSON risks extra lookup | evaluate error contract in Pass 3; don't invent runtime text |
| Upgrade | pin coordinated package/tool and manifest | release policy exists, legacy runner compatible | old-implementer compile proof and changelog/reference retained |

First-time confusion report (simulated timestamps, not timings): T+0:00 current GCP README says it verifies configuration; new observer link must be visible. T+0:30 current CLI guide has many families; a direct anchor avoids a source search. T+1:00 prospective command needs exact resource and bundle prerequisites; no “latest” shortcut is appropriate. T+2:00 sample JSON/exit table must explain Unknown versus Failed and last-observed Running. T+3:00 caller decides safely using C2 recipe, with fixture/provider evidence labeled. Each is source-backed missing documentation for a new declared surface, not evidence that current software emits a faulty message.

#### DX Step 0 decisions

| Source/evidence | Current | Proposed | Exact approval/scope | Fixed/pending |
| --- | --- | --- | --- | --- |
| DX1 new command not documented | source reference only | one quickstart in existing CLI guide, linked from package/overview, with all prerequisites, copyable library/CLI snippets and actual supported sample output | P1/P5 mechanical within required docs; estimates human 1h / agent 20m | all runtime contracts fixed |
| DX2 no measured first-use run | unmeasured | one manual implementation-time walkthrough of approved clock, record actual reading/setup/command time separately | P1, small proof in direct docs/example scope; target remains aspirational | no release timing gate, no telemetry |
| DX3 distinct proof kinds | neutral examples only | make the existing two examples visibly label offline fixture versus separately authorized provider evidence | exact C0 verification boundary, no new capability | no replay CLI or live authorization |

### DX voices and dispositions

Native reviewer completed with matching INPUT `dx ef0f32f4fae8fcead1e6b5f68fb56687928e5e8a9759163a37f1e0601cf3ecf0`; ten findings. Claude Code rechecked unavailable, not installed; no external process or cross-model confirmation. Five combo/sub launches were rejected, then configured Luna completed.

| Finding | Resolution | Authority |
| --- | --- | --- |
| D-N1 quickstart/TTHW | DX1/DX2; prepared-machine provider clock and separately labeled offline example | P1/P5, no new CLI replay mode |
| D-N2 executable compatibility workflow | use test-project fixture/manifest validation and explicit checklist; no new public command | P4/P5, reuse required fixtures/tests |
| D-N3 code-to-error contract | diagnostic table below; T2 retains code-only API/JSON tradeoff | P1 docs, exact C0 shape retained |
| D-N4 API construction | show existing render result and bounded published-artifact reconstruction using public DeploymentArtifact.Create | P5, no new loader API |
| D-N5 parser admission | name pre-CliFx hook and malformed forms | P1 existing safe-admission obligation |
| D-N6 launcher/credentials | document PATH, inherited approved gcloud config and certified OS/version; fixed executable remains | P5, no arbitrary command escape |
| D-N7 progressive docs | direct quickstart then adoption/reference/matrix/runbook links | DX1, existing documentation scope |
| D-N8 correlation/trace | safe fixed stage/exception-type taxonomy in local diagnostics where available; no raw exception/provider text; T2 machine metadata gap explicit | P1 redaction boundary retained |
| D-N9 Running deadline surprise | examples for clean idle versus in-flight failure and stale receipt | C7 and exact deadline contract |
| D-N10 shell portability | POSIX sh recipe, save status immediately, preserve 130 | C2 clarification, no retry policy |

DX DUAL VOICES — CONSENSUS TABLE:

| Dimension | Native | Claude Code | Consensus |
| --- | --- | --- | --- |
| Getting started below target | no concrete workflow; time unmeasured | unavailable | N/A |
| Naming guessable | construction/admission detail missing | unavailable | N/A |
| Errors actionable | per-path table and structured detail gaps | unavailable | N/A |
| Docs complete/findable | progressive guide needed | unavailable | N/A |
| Upgrade safe | keep coordinated release/legacy compatibility | unavailable | N/A |
| Environment friction | explicit certified tool/OS/PATH required | unavailable | N/A |

### DX Pass 1: Getting started — 6 -> 8/10 (predicted plan quality)

The existing package README and CLI guide expose no execution observer because it is new. A newcomer must not read the full evidence matrix to try the approved workflow. DX1 chooses one golden path in the existing CLI guide, directly linked from package/overview/chooser, rather than a hosted playground or extra public command. The Pass 1 reference was read at Step 0D; its useful pattern is one visible result with prerequisites disclosed, not an unrelated account-free cloud promise.

The planned prepared-machine quickstart has three actions: inspect the supported version/OS/prerequisite box and choose the coordinated tool version; run the complete observe-execution command with the caller's trusted bundle/logical ID/canonical execution; read the JSON example and branch on its exit using C2. Budget allocation is estimated: read/prerequisite check about 1–2 minutes, invocation up to its 60-second default, interpretation about 1 minute, excluding a long-running workload from a promised success endpoint. A separately labeled offline example runs through the bounded fake seam in the neutral sample project and proves parser/caller behavior only. Complete concrete commands and expected fixture output become implementation deliverables once files exist; the review does not invent a currently runnable command or provider success.

Ten would require an observed first-use session at the accepted clock and actual compatible provider evidence. Retain score 8 because both are future proof; no extra installation abstraction is justified.

### DX Pass 2: API/CLI/SDK design — 7 -> 8/10

Names follow the existing deployment/provider grammar and the issue's exact API. API callers supply their existing RenderAsync result; callers with a published bundle can reconstruct DeploymentRenderResult from the three bounded byte snapshots using DeploymentArtifact.Create and the canonical target, after which ObserveAsync still validates the complete bundle. The example must show this composition, canonical identity, token handling and separate Unknown/Failed decisions; it must not introduce a second permissive validator. The additive bounded seam is a production injection contract, not solely a mock adapter, and its constructor/factory is documented before implementation.

CLI defaults remain 60s/2s and API durations stay explicit; changing this to optional API policy is out of scope. The pre-parser observation-command admission runs in AppSurfaceCliApp.RunAsync before generic CliFx binding, alongside the existing DurableDoctorArgumentAdmission pattern at AppSurfaceCliApp.cs:69. Cover separate/joined values, missing/duplicate/unknown flags, invalid duration/resource/path, mixed help and malformed values, and `--json` position; no branch may echo raw input. Safe inherited PATH and gcloud configuration select a certified installation, while arbitrary executable/extra args remain unsupported. Ten would require an implemented packed consumer and proven launcher matrix, so retain 8.

### DX Pass 3: Errors and debugging — 5 -> 6/10, agent-ready-v1

Trace four concrete paths: invalid bundle, unavailable tool/provider read, stale/unsupported evidence, and unexpected reader/cleanup defect. The proposed API is a typed observation carrying State/ConfigurationMatched/DiagnosticCode, not a rich typed exception, and JSON mirrors its fixed fields. The human/text renderer and documentation can explain what/fix/recovery, but a code-only machine envelope does not carry every agent-ready field. This is `CAP6:machine-result:fix-context-trace`, a visible residual High gap rather than a fabricated pass. No CAP4 issue is accepted: failure never exits 0, invalid data is redacted, all reads are bounded and noninteractive behavior is mandatory.

T2 is a taste/contract choice: provisionally preserve the exact proposed API and JSON shape with stable diagnostic lookup (C0/P5); alternative is an explicitly approved additive rich machine-diagnostic contract. No extra public API or JSON field is silently added by a polish review. Required error handling remains in scope regardless of that final choice. Raw exception text and provider output are prohibited even for traces. Expected failures need no stack trace; unexpected failures may expose only a safe internal stage/type token and a static escalation instruction in text/runbook, which does not satisfy a machine trace claim by itself.

| Path/class | what | fix | recovery | code | context | trace | redaction | channel | Cap after plan |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| bundle / usage | safe field/category | docs/text pass; machine fail | N/A pre-work | pass | N/A usage | N/A usage | pass | typed Unknown / CLI 2 | CAP6 machine fix |
| tool/provider / dependency | safe operation + valid identity | docs/text pass; machine fail | explicit caller decision, no automatic retry | pass | docs/text stage; machine fail | N/A expected | pass | typed Unknown / CLI 2 | CAP6 machine fix/context |
| stale/unsupported / evidence | safe evidence category | docs/text escalation; machine fail | hold, compatibility review | pass | admitted format/version docs; machine fail | N/A expected | pass | typed Unknown / CLI 2 | CAP6 machine fix/context |
| reader/cleanup defect / unexpected | safe stage/type only | docs/text maintainer escalation; machine fail | reconcile local process before new read | pass | fixed taxonomy; machine fail | machine fail | pass | typed Unknown / CLI 2 | CAP6 machine trace |

The following diagnostic allocation is concrete plan intent and must be collision-checked again after rebase. Anchors live under the execution-observation section of Deployment/reference.md. All messages are fixed safe templates; only already validated canonical identity may be included. Input/path values, environment values, stderr and exception messages are never interpolated.

| Code | Safe problem / fix text | Recovery and side effects |
| --- | --- | --- |
| ASDEPLOY170 | Invalid observation request. Fix: provide canonical IDs and finite positive ordered durations. | fix input; no provider work |
| ASDEPLOY171 | Bundle validation failed. Fix: provide the complete trusted published artifact set. | repair, then explicit new read; no mutation |
| ASDEPLOY172 | Execution identity does not match the bundle. Fix: ask the submission owner for its exact execution resource. | hold caller acceptance; no substitution |
| ASDEPLOY173 | Execution UID changed between reads. Fix: reconcile identity with the submission owner. | hold; never continue with new UID |
| ASDEPLOY174 | Execution evidence is malformed or conflicting. Fix: provide a sanitized reproduction to the maintainer. | stop; compatibility review |
| ASDEPLOY175 | Execution evidence shape is unsupported. Fix: use a manifest-certified tool/OS or ask the maintainer for support. | stop; no inferred fields |
| ASDEPLOY176 | Execution evidence is stale. Fix: inspect reconciliation and selected identity with the owner. | hold; explicit later observation only |
| ASDEPLOY177 | Captured execution configuration differs. Fix: compare the expected bundle and submitted execution with the owner. | no workload retry or reconfigure |
| ASDEPLOY178 | Certified gcloud launcher is unavailable. Fix: install/select a certified version on PATH. | one explicit new read after repair; no automatic retry |
| ASDEPLOY179 | Exact execution read failed. Fix: have the operator inspect gcloud credentials, permissions and connectivity. | no automatic retry; caller may choose one fresh bounded read after repair, then escalate |
| ASDEPLOY180 | Process output exceeded its byte limit or encoding contract. Fix: stop automation and ask the maintainer to inspect the tool format. | no truncated parse; local cleanup required |
| ASDEPLOY181 | Observation budget ended without usable final evidence. Fix: keep acceptance unresolved and decide whether to observe again. | no automatic retry; no remote mutation |
| ASDEPLOY182 | Last observed execution was Running when the clean idle budget ended. Fix: use ObservedAt and explicitly decide whether to wait/observe again. | last-read evidence only; CLI 3 |
| ASDEPLOY183 | Matching execution succeeded. | terminal evidence; CLI 0, not application acceptance |
| ASDEPLOY184 | Matching execution failed. Fix: ask the workload owner to inspect its logs and recovery policy. | CLI 1; observer never retries workload |
| ASDEPLOY185 | Matching execution was cancelled remotely. Fix: ask the workload owner to choose recovery. | CLI 1; distinct from local cancellation |
| ASDEPLOY186 | Caller cancelled local observation. | CLI 130; remote execution unaffected |
| ASDEPLOY187 | Local process cleanup could not be confirmed. Fix: reconcile the local child/pipe state before another observation. | no success; process side effects may remain, cloud mutation none |
| ASDEPLOY188 | Observation output could not be delivered. | stop further writes; CLI 2, or 130 if caller cancelled |
| ASDEPLOY189 | Unexpected observation failure. Fix: report the safe stage/type and certified tool/OS to the maintainer. | stop; reconcile local process, no raw trace |

Context for expected dependency/evidence failures is the selected safe operation, validated resource, package/tool-format identifier where established and fixed failure category; actual provider errors remain unavailable rather than guessed from raw text. No debug flag or diagnostic file writer is added. Documentation must explicitly distinguish cloud side effects (none) from a cleanup failure's possibly still-running local process. A new explicit read is a caller action, never package recovery. Text-mode failure stderr uses fixed safe problem/fix/code/reference text; JSON stdout retains exactly one observation. After output failure C1 forbids all further writes.

### DX Pass 4: Documentation and learning — 7 -> 9/10

The current README already links coordinated release guidance and the deployment reference, so extend those paths rather than creating another documentation site. The entry point is one quickstart with prerequisites and understood result; the adoption guide then explains library construction, caller identity handoff and safe next actions. The reference carries the exact API, diagnostic table, evidence matrix, state rules and version/OS manifest. All named concepts link to their canonical page at first meaningful use, as AGENTS requires.

DX4 is a mechanical documentation refinement under P1/P5: link quickstart -> CI recipe -> adoption/reference -> compatibility/troubleshooting, with concrete sample output generated from checked fixtures and labeled as such. NuGet-packaged README links must use canonical absolute repository URLs where relative source targets are absent from the package; this applies the existing project package-README learning. Do not claim a two-minute findability result until the manual walkthrough. Nine reflects a complete planned path; ten needs implemented navigation and observed findability. The Pass 4 reference's relevant pattern is docs shipping with the feature, without copying its unsupported adoption statistics.

### DX Pass 5: Upgrade and migration — 8 -> 9/10

The existing changelog states the repository ships coordinated packages/tool/examples and breaking behavior needs migration guidance. The new observer is additive and does not change IGcloudCommandRunner members or legacy VerifyAsync behavior. A previous custom runner consumer must still compile, while the new production observer accepts only the bounded contract. Documentation must show how a consumer opts in and how to retain/restore the previous application observer.

No codemod or deprecation mechanism is needed for an additive API with no removed behavior. Version/OS/fixture certification is explicit and pinned rather than a floating “all gcloud versions” promise. Package/tool closure and actual released adoption remain separate gates. Ten needs compiled old/new consumers and installed-tool proof; planned compatibility is not a measured upgrade experience. Pass 5's lesson is predictable versioned migration, implemented through the existing release system.

### DX Pass 6: Environment and tooling — 7 -> 8/10

The projects target .NET 10; the production path additionally needs an installed certified gcloud launcher, inherited authentication/configuration and permission to read the exact execution. State these at the top of both library and CLI guides. On Unix the fixed executable resolves through PATH; Windows uses certified gcloud.cmd plus the safe fixed cmd.exe launch. The caller selects a certified installation through its normal environment, not a free-form executable/argument injection option. Document relevant variable names only and avoid logging their values.

Existing TimeProvider and intentional bounded seams allow deterministic local tests. The neutral offline examples exercise those seams without credentials; an actual provider read remains separately authorized and cannot be hidden in a getting-started check. CI uses --json and noninteractive flags, stable exits and POSIX sh status handling; IDE users receive XML docs and normal C# types. TypeScript/editor plugins/watch mode are inapplicable to this .NET evidence primitive. Eight retains uncertainty about actual supported OS/native process behavior and tooling startup; no performance benefit from Pass 6 reference examples is claimed.

### DX Pass 7: Community and ecosystem — 6 -> 7/10

The repository is public and has existing package/release discovery, issues, contribution guidance and licensing; the feature should point to those paths. Two runnable neutral examples make the boundary inspectable without private application names. Report failures with a diagnostic code, supported version/OS and a sanitized fixture, never a secret-bearing provider dump. There is no new plugin ecosystem or support promise to build here.

No new community channel, pricing plan or response-time SLA is justified by the issue. Actual maintainer response and adoption sentiment are unmeasured, so the score remains 7 rather than inventing desirability or an active monitored channel. The Pass 7 reference informs the ecosystem lens only; its numeric claims are not evidence for this package. Existing GitHub issue reporting is the lowest-cost feedback vehicle.

### DX Pass 8: Measurement and feedback — 5 -> 7/10

DX2 adds one manual implementation-time first-use walkthrough on the exact prepared-machine clock: start at quickstart open, include reading and any tool-version selection, stop when the developer understands the result and safe next action. Record missing prerequisites, time spent reading/setup/command, outcome, actual tool/package/OS and whether provider access ran. The offline example gets its own timer and explicit fixture label, never substitutes for this useful result. The target is 2–5 minutes and remains aspirational; no benchmark or onboarding session ran in this review.

Do not add analytics, a recurring review schedule or a timing release gate. C6 defers real-adopter outcome/Unknown-rate measurement until a consenting workflow exists. This limited manual record and existing issues are enough for a later /devex-review to compare the implemented path with the planned clock, while seven honestly retains the lack of measured feedback. SPACE/DevEx reference frameworks are lenses, not authorization for a telemetry program.

### DX scope and reuse

NOT in scope: hosted playground; new compatibility/replay public command; public bundle-loader framework; arbitrary executable/config extra-argument escape; new SDK languages; automatic observation/workload retries; new telemetry/community service. C6's existing TODO covers real-adopter measurements, with no duplicate TODO. T2 keeps the fixed code-only API/JSON contract pending owner decision instead of adding a rich machine error API silently.

Already exists: coordinated package/release links, public artifact construction, command discovery, CanaryPoll cancellation/time conventions, DurableDoctor pre-parser admission and existing package consumer proof. Reuse them as described; none proves the new command exists or the new provider format works. Prior project-wide DX reviews are other features, not a #891 trend. Their missing/different rubric rows are not comparable; this review records agent-ready-v1 explicitly.

## DX Review Scorecard

All scores below rate predicted plan quality, not achieved runtime DX.

| Dimension | Initial | Revised | Prior #891 | What remains for 10 |
| --- | ---: | ---: | --- | --- |
| Getting started | 6 | 8 | none | observed first-use clock and concrete implemented path |
| API/CLI/SDK | 7 | 8 | none | packed construction/injection/CLI proof |
| Error messages | 5 | 6 | none | T2 machine fix/context/trace gap; CAP6 retained |
| Documentation | 7 | 9 | none | implemented links/examples and findability observation |
| Upgrade path | 8 | 9 | none | old/new consumer and installed-tool verification |
| Dev environment | 7 | 8 | none | certified OS/launcher evidence |
| Community | 6 | 7 | none | actual feedback/support evidence |
| DX measurement | 5 | 7 | none | first-use run and later adopter data |
| Overall, arithmetic mean | 6.375 | 7.75 | not comparable | unresolved machine-error tradeoff and runtime proof |

TTHW: unmeasured; target Competitive 2–5 minutes on explicitly prepared machine, not a fresh cloud setup or terminal-success promise. Magical moment designed through existing CLI guide and two neutral examples. Product: .NET library + CLI + docs. Mode: DX POLISH. Zero friction, learning by doing, uncertainty, contextual examples and agents are addressed in plan; machine error details retain a CAP6 gap. Opinionated explicit defaults remain, with supported environment/injection seams rather than an unsafe bypass. No score establishes an actual provider compatibility result.

### DX implementation checklist

- [ ] Record the one-time prepared-machine first-use walkthrough against the 2–5 minute target; report actual time and blockers, no automated timing gate.
- [ ] Document the existing coordinated dotnet-tool install and .NET 10/gcloud/auth/bundle/execution prerequisites; no account-free cloud claim.
- [ ] Implement copyable library construction and command examples with actual fixture output and all safe exits.
- [ ] Deliver the magical moment through the existing guide; label offline and provider evidence separately.
- [ ] Preserve the exact public API and JSON observation; T2 richer machine diagnostics awaits the owner, CAP6 remains visible.
- [ ] Publish the per-code fixed safe problem/fix/recovery/side-effect/reference table; use safe text stderr on failures, no raw values or new debug file writer.
- [ ] Test safe pre-CliFx argument admission and POSIX sh status recipe, including interruption/130 and C1 output failure.
- [ ] Link quickstart/adoption/reference/matrix/runbook/version manifest from package, overview, CLI guide and chooser; verify packaged README links.
- [ ] Verify old custom runner compatibility, pinned coordinated upgrade, rollback and packed library/tool consumers.
- [ ] Document certified OS/launcher/PATH/config-variable names and intentional injectable bounded seam.
- N/A: TypeScript types, hot reload, hosted free tier, codemods/deprecations for removed APIs, new community channels. This is additive .NET/CLI work with no new hosted service or removed API.
- Existing changelog, documentation search and GitHub issues are reused; no search engine or monitored-response promise is added.

### DX implementation tasks

- [ ] **DX-T1 (P1, human ~1–2h / agent ~20–40m, estimated)** — docs/examples — Write the exact first-use and library construction path plus compatibility worksheet checklist.
  - Surfaced by: DX1, D-N1/N2/N4/N7 and Passes 1/2/4.
  - Files: Deployment/reference.md, GCP README, CLI README and the required neutral examples.
  - Verify: complete copyable snippets, fixture provenance, accepted manifest prerequisites and manual prepared-machine walkthrough; no new public validator/replay API.
- [ ] **DX-T2 (P1, human ~1–2h / agent ~20–40m, estimated)** — CLI/diagnostics — Implement safe command admission and fixed diagnostic rendering/lookup with C1/C2.
  - Surfaced by: D-N3/N5/N8/N9/N10 and Pass 3.
  - Files: AppSurfaceCliApp.cs, future observation command/admission tests and Deployment/reference.md.
  - Verify: malformed forms never leak input; exact exits/JSON, failed output, safe text stderr, documented code categories. T2 richer API/JSON is not assumed.
- [ ] **DX-T3 (P2, human ~30–60m / agent ~15–20m, estimated)** — environment/adoption — Document certified launch/configuration and upgrade paths with package discoverability.
  - Surfaced by: D-N6 and Passes 5/6.
  - Files: GCP README, CLI README, package chooser and existing consumer proof.
  - Verify: exact tested OS/tool combinations and old custom runner compile; no generic process escape.

_No new tasks from Pass 7 beyond reuse of existing feedback channels. Pass 8's one manual check is part of DX-T1; no ongoing measurement task is added._

### DX accepted obligations

<!-- autoplan-accepted:dx -->
- Add one complete quickstart in the existing CLI guide, linked from the provider README, Deployment overview and package chooser. State .NET 10, certified gcloud/OS/launcher, inherited credentials/read permissions, trusted published bundle and exact existing execution prerequisites. Include concrete copyable CLI and C# construction snippets, actual fixture output, all state/exit branches and caller-owned acceptance/recovery. Show the existing RenderAsync result path and bounded artifact-byte reconstruction with DeploymentArtifact.Create; add no new public bundle-loader or replay/compatibility command.
- Keep offline sample evidence separately labeled from provider-observed compatibility. Use the existing two neutral examples and bounded injection seam for the offline path; their success never certifies cloud fields. Provide the fixture/worksheet validation checklist through the test project and manifest files, including exact hashes/provenance and named maintainer acceptance; cloud capture remains separately authorized.
- Measure one manual implementation-time first-use session from opening the quickstart on the explicitly prepared machine to understanding a safe result and next action; include reading/version-selection/setup/command time and actual versions/OS/prerequisite blockers. Target 2–5 minutes is aspirational, with no automated release-time threshold, recurring process or telemetry. Time offline demonstration separately and do not substitute it for this clock.
- Run observation-command safe argument admission in AppSurfaceCliApp.RunAsync before generic CliFx parsing, following the existing doctor admission pattern. Test separate/joined values, missing/duplicate/unknown options, malformed resource/path/duration, help interactions and --json position. Never echo rejected values. Preserve C1 single-output-attempt/cancellation rules.
- Add the fixed execution-observation diagnostic table with planned codes ASDEPLOY170–ASDEPLOY189 and stable Deployment/reference.md anchors; recheck collisions after rebase before freezing codes. Cover invalid request/bundle, identity/change, malformed/unsupported/stale evidence, configuration, tool/provider, output/encoding, deadlines, terminal states, caller cancellation, cleanup, delivery and unexpected failures. For each failure give fixed safe problem, exact fix/escalation, recovery/retry bound, cloud side effects none, possible local-process cleanup state, safe context taxonomy, trace applicability, redaction and native channel. Text-mode failures write only safe problem/fix/code/reference to stderr; JSON stdout remains one existing-shape observation. No raw provider/exception/environment/argv data or debug file writer is added; after any output failure suppress further writes.
- Provisionally preserve the exact proposed public observation and code-only JSON shape pending final owner T2: no rich error fields or additional public diagnostic API are assumed. Explicitly retain the CAP6 machine fix/context/trace limitation; documentation/text improvements do not claim those fields are present in machine results.
- Document POSIX sh as C2 recipe's portability target; capture status immediately and branch on 0/1/2/3/130, preserve interruption, and never automatically retry/readjust/cancel/submit a workload. Verify via command-path/installed-tool fixtures and link it from quickstart.
- Organize existing documentation as quickstart -> adoption -> API/state/diagnostic reference -> compatibility matrix/manifest -> troubleshooting; use canonical absolute links in packaged README where source-relative targets are absent. Document PATH/inherited approved gcloud config variable names, certified installation selection and the injectable bounded seam; never expose arbitrary executable or argument extensions. Preserve coordinated upgrade, old-runner compatibility and caller rollback guidance.
- Normative diagnostic allocation is ASDEPLOY170 invalid request; 171 invalid bundle; 172 identity mismatch; 173 UID change; 174 malformed/conflicting evidence; 175 unsupported shape; 176 stale evidence; 177 configuration mismatch; 178 launcher unavailable; 179 provider read failure; 180 output/encoding limit; 181 unknown deadline; 182 clean idle Running deadline; 183 success; 184 workload failure; 185 remote cancellation; 186 caller cancellation; 187 cleanup unconfirmed; 188 delivery failure; 189 unexpected failure. Each code has a stable Deployment/reference.md execution-observation diagnostic anchor. Fixed fix/recovery text must implement the previously specified safe caller actions, including no automatic retries, no remote mutation and local-process reconciliation after cleanup failure.
- Diagnostic ASDEPLOY170: Invalid observation request. Fix: provide canonical IDs and finite positive ordered durations. Recovery: fix input; no provider work.
- Diagnostic ASDEPLOY171: Bundle validation failed. Fix: provide the complete trusted published artifact set. Recovery: repair, then explicit new read; no mutation.
- Diagnostic ASDEPLOY172: Execution identity does not match the bundle. Fix: ask the submission owner for its exact execution resource. Recovery: hold caller acceptance; no substitution.
- Diagnostic ASDEPLOY173: Execution UID changed between reads. Fix: reconcile identity with the submission owner. Recovery: hold; never continue with new UID.
- Diagnostic ASDEPLOY174: Execution evidence is malformed or conflicting. Fix: provide a sanitized reproduction to the maintainer. Recovery: stop; compatibility review.
- Diagnostic ASDEPLOY175: Execution evidence shape is unsupported. Fix: use a manifest-certified tool/OS or ask the maintainer for support. Recovery: stop; no inferred fields.
- Diagnostic ASDEPLOY176: Execution evidence is stale. Fix: inspect reconciliation and selected identity with the owner. Recovery: hold; explicit later observation only.
- Diagnostic ASDEPLOY177: Captured execution configuration differs. Fix: compare the expected bundle and submitted execution with the owner. Recovery: no workload retry or reconfigure.
- Diagnostic ASDEPLOY178: Certified gcloud launcher is unavailable. Fix: install/select a certified version on PATH. Recovery: one explicit new read after repair; no automatic retry.
- Diagnostic ASDEPLOY179: Exact execution read failed. Fix: have the operator inspect gcloud credentials, permissions and connectivity. Recovery: no automatic retry; caller may choose one fresh bounded read after repair, then escalate.
- Diagnostic ASDEPLOY180: Process output exceeded its byte limit or encoding contract. Fix: stop automation and ask the maintainer to inspect the tool format. Recovery: no truncated parse; local cleanup required.
- Diagnostic ASDEPLOY181: Observation budget ended without usable final evidence. Fix: keep acceptance unresolved and decide whether to observe again. Recovery: no automatic retry; no remote mutation.
- Diagnostic ASDEPLOY182: Last observed execution was Running when the clean idle budget ended. Fix: use ObservedAt and explicitly decide whether to wait/observe again. Recovery: last-read evidence only; CLI 3.
- Diagnostic ASDEPLOY183: Matching execution succeeded. Recovery: terminal evidence; CLI 0, not application acceptance.
- Diagnostic ASDEPLOY184: Matching execution failed. Fix: ask the workload owner to inspect its logs and recovery policy. Recovery: CLI 1; observer never retries workload.
- Diagnostic ASDEPLOY185: Matching execution was cancelled remotely. Fix: ask the workload owner to choose recovery. Recovery: CLI 1; distinct from local cancellation.
- Diagnostic ASDEPLOY186: Caller cancelled local observation. Recovery: CLI 130; remote execution unaffected.
- Diagnostic ASDEPLOY187: Local process cleanup could not be confirmed. Fix: reconcile the local child/pipe state before another observation. Recovery: no success; process side effects may remain, cloud mutation none.
- Diagnostic ASDEPLOY188: Observation output could not be delivered. Recovery: stop further writes; CLI 2, or 130 if caller cancelled.
- Diagnostic ASDEPLOY189: Unexpected observation failure. Fix: report the safe stage/type and certified tool/OS to the maintainer. Recovery: stop; reconcile local process, no raw trace.
<!-- /autoplan-accepted:dx -->

DX outcome: completed with concerns, overall 7.75/10, TTHW unmeasured, one T2 final-gate contract choice/CAP6 gap, outside review unavailable. All eight passes and Hall of Fame sections were read and evaluated; no unrelated skill checklist applies. No new durable project learning beyond already-recorded package-README/proof-boundary lessons.

### Engineering Step 0 — scope and source evidence

Fixed target: this #891 implementation plan, including CEO and DX obligations. Full engineering methodology read through EOF: 1–600, 601–1200, 1201–1800, 1801–2253; the small truncated Context Recovery passage was repaired by reading 270–292. Size 156266 bytes, SHA-256 `b781d2d7515907a11241e88cf4987f3ac135fd2cdccd03af7c5c1444ad764067`. Mode FULL_REVIEW; /autoplan prohibits reducing accepted scope. Prior project context is reused with its missing brain-cache limits, not re-inferred from another issue's latest file.

Actual source reads confirm VerifyAsync checks artifact count/schema/hashes/bindings before two read-only Job/IAM calls (GcpCloudRunDeploymentTarget.cs:88–138), cross-artifact identity is private (:141–214), and legacy runner output uses ReadToEndAsync (:593–594). Existing tests cover target/bundle tampering and legacy launcher/timeout/cancellation, not the new execution contract. AppSurfaceCliApp.RunAsync performs doctor admission before CliFx (:69); use that placement for the new command. DeploymentRenderResult has IReadOnlyList artifacts, so the observer must take its own immutable call snapshot rather than retain a caller-owned list. Concrete reads of the referenced source/tests precede the coverage diagram; no implementation or test suite is run.

| Subproblem | Existing behavior | Reuse or separation |
| --- | --- | --- |
| bundle integrity and parity facts | target private cross-artifact checks | extract one internal authority; legacy caller unchanged, observer adds stricter admission |
| identity/image | canonical intent types and image grammar | reuse validation; exact execution path/UID/generation parser is new |
| subprocess launch | safe fixed Unix/Windows launch | reuse token/start rules; separate bounded capture/lifetime |
| timing/cancellation | TimeProvider conventions in CanaryPoll | standard .NET elapsed timestamps and injected timer; no new scheduler framework |
| safe argv | doctor pre-parser admission | mirror local pattern, no global CLI rewrite |
| artifact construction | public DeploymentArtifact.Create and render result | document direct callers and bounded published-byte snapshot |
| release/test proof | xUnit/VSTest, package consumers, coordinated tool | extend, do not replace |

Complexity selector resolved by authorized P1/P5: original responsibility arrangement retained. Estimated implementation touches 20–30 authored files including fixtures/docs/examples, with roughly seven new concrete responsibility types plus public records/interface and test fixtures; this is not a counted final diff. Combining the runner with classification or reusing permissive parsing would obscure trust/lifetime boundaries. A smaller physical arrangement may co-locate tiny records in one file, but no generic framework or extra service is selected. Feature answers: no cuts, exact user C0 scope. Structure: original Approach B, /autoplan auto-decision E0. Accepted scope: all source/CEO/DX obligations. Pending: T1/T2 owner taste, original contracts stand. This scope record was read back before advancing.

Scope Challenge findings, distinct from runtime bug claims:

- E-S1 [P1] (confidence 9/10) GcpCloudRunDeploymentTarget.cs:593, `var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);` — legacy capture is unsuitable for the new hard byte limit. Accepted additive bounded seam under exact C0 approval; do not retrofit or wrap unbounded capture.
- E-S2 [P1] (confidence 9/10) DeploymentContracts.cs:622, `public sealed record DeploymentRenderResult(string Target, IReadOnlyList<DeploymentArtifact> Artifacts);` — the list itself is not immutable. Snapshot at observer entry and derive immutable expectations before any await; test caller list mutation. P1 completion of approved snapshot contract.
- E-S3 [P2] (confidence 9/10) AppSurfaceCliApp.cs:69, `var doctorAdmission = DurableDoctorArgumentAdmission.Inspect(args);` — registration alone cannot ensure safe generic parser errors. Exact approved DX admission must run at the actual early hook, with command-level proof.

Search: [Layer 1] use TimeProvider.GetTimestamp/GetElapsedTime, Task delays and cancellation timers rather than wall-clock arithmetic; [Layer 2] Microsoft Process.Kill documentation says root WaitForExit/HasExited does not prove descendant exit, even with entireProcessTree. [Layer 3] therefore native descendant/pipe tests and truthful cleanup-failed outcomes are required; a root-exit assertion alone is inadequate. [Layer 1] System.Text.Json has finite depth defaults, but inspect the repository's target version before relying on newly added duplicate-property APIs; a Utf8JsonReader duplicate-key guard is portable. Sources: https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.gettimestamp, https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill, https://learn.microsoft.com/en-us/dotnet/api/system.text.json.utf8jsonreader . No new library or infrastructure pattern is needed.

History of the named target/CLI paths shows recent doctor/coverage integration; no relevant revert appeared in the bounded history query. Future observer/runner/fixture paths have no implementation history and are not treated as existing tests. TODOS.md now contains only C6 as a #891 follow-up; it does not block required neutral-consumer acceptance. Distribution remains the existing .NET 10 GCP NuGet package plus coordinated appsurface tool; no separate artifact download channel or service is added. Scope Challenge result: scope accepted as-is; E0–E-S3 resolved mechanically with original requirements retained.

### Engineering voices

Native completed with matching INPUT `eng c7ba9152682f0150ced9200f9cd350eca6f86851273b49ceb850e76e6a76ee7c` (trailing punctuation ignored). Seven findings. Five combo/sub attempts were rejected, then configured Luna completed. Claude Code rechecked not installed; no external process, outside status unavailable. Native findings are not cross-model confirmation.

| Native finding | Disposition |
| --- | --- |
| EN1 bounded cleanup | E1 below: explicit close/join state machine and native descendant tests; do not silently waive reader settlement by detaching active reads |
| EN2 bundle trust | E2: caller expectations, not authenticated publisher; explicit entry docs/negative control, no new signature system |
| EN3 permanently blocked format | existing first feasibility gate retained; validator test rejects a manifest with unavailable R proof; no extra process report |
| EN4 nested deadlines | E3: outer CLI budget authority plus per-call context and conservative remaining allocations; no timeout renewal or caller-cancel confusion |
| EN5 output latch | E4: one serializer/writer owner, at-most-one object attempt and safe failure map |
| EN6 code allocation | E5: scoped code/anchor contract validation after rebase and before package proof; no repository-wide unrelated diagnostic rewrite |
| EN7 v1 mapping | existing R/D/U gate: no exact independently supported mapping means v1 is uncertified/Unknown; no partial fallback |

| ENG dimension | Native | Claude Code | Consensus |
| --- | --- | --- | --- |
| Architecture | cleanup/trust/budget refinements | unavailable | N/A |
| Test coverage | native process and boundary cases required | unavailable | N/A |
| Performance | nested total-budget accounting | unavailable | N/A |
| Security | bundle authenticity must not be implied | unavailable | N/A |
| Errors | writer ownership and diagnostic reservation | unavailable | N/A |
| Deployment | exact-format feasibility gate | unavailable | N/A |

### Engineering Section 1: Architecture

The graph retains one provider-specific interpretation pipeline. The legacy verifier and new observer share only bundle facts and compatible launch-token validation; the strict execution normalizer never consumes permissive deployed-Job parsing as proof. CLI and direct library calls invoke the same observer; the CLI's file/argv/output adapter owns presentation and remaining-budget handoff. Default instances are stateless across requests, with no result cache, hosted worker or remote mutation authority.

```text
neutral Deployment contracts -> GCP bundle validator <--- existing VerifyAsync
                   |                  |
CLI argv/file -> immutable bundle -> observer per-call context
                   API -----------------^  |
                    exact identity + expected config
                                          v
        IBoundedGcloudExecutionReader -> default fixed gcloud describe
                                          |
                             complete capped bytes + typed outcome
                                          v
                       strict normalizer -> pure classifier -> typed observation
                                          |
                                CLI single result writer -> JSON/text + exit
```

E1 [P1] (confidence 8/10, plan risk) design:186 says “stop the process tree and settle both reader tasks.” Root exit alone does not establish that, per Microsoft Process.Kill docs. Use one cleanup owner with states Started -> Reading -> Stopping -> ClosingPipes -> JoiningReaders -> Finished; normal exit also joins both readers before parsing. A failure latch blocks success after any nonzero/overflow/read/deadline/cleanup fault. Kill only the locally owned process/tree, close both local pipes as the bounded escape, and await the now-closed reader tasks within the existing reserve. If root/descendant cleanup cannot be established, return cleanup-unconfirmed/Unknown, never success; retain safe local state for diagnosis. Do not adopt the reviewer's detach-active-read shortcut as satisfying the source's settlement guarantee. Native supported-platform tests must prove no reader remains active at return, descendant pipe closure and bounded completion. Failure to prove a platform blocks its certification; the documented blocked-OS-start/nonconforming-runner/unkillable-process limitations remain explicit.

E2 [P2] (confidence 9/10) design:108 states “Hash consistency is not a signature or proof that an untrusted caller's expected bundle is authentic.” Carry that statement into both entry examples and reference. Observer success means provider evidence matches caller-selected expectations; it never authenticates publisher, source revision or submission provenance. A self-consistent changed expectation can legitimately match its corresponding provider fixture. Test that behavior and exact safe documentation rather than adding a signature/trust-service requirement outside C0.

E3 [P1] (confidence 8/10) design:176 says CLI starts before bundle loading and passes only remaining budget. Implement an internal ObservationBudget in each owning layer using the same TimeProvider timestamp/elapsed calculation; the CLI's original deadline remains the outer authority through loading, API await and delivery. Nested allowances are computed from remaining time and cannot extend that outer limit. Public API creates its context at entry; CLI passes only a conservatively allocated remaining allowance and retains its outer deadline guard. Do not classify the CLI's internal deadline as user cancellation: only the original console token produces 130; internal deadline while a read/cleanup is in flight is Unknown/2. A completed clean idle Running observation retains its receipt timestamp and maps to 3. Recheck elapsed/caller token at each boundary and before accepting a result. This requires no new public deadline type or change to the proposed observer signature. Deterministic tests must pause between the CLI-to-API handoff, bundle I/O, parsing and cleanup to catch any nested reset.

E6 [P1] (confidence 8/10) design:65 leaves the additive seam unnamed. Specify `IBoundedGcloudExecutionReader.ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken)` as the narrow injectable production read seam; it accepts only a canonical exact execution, not an executable or raw extra argv. Its immutable result `BoundedGcloudExecutionReadResult` contains typed outcome, optional exit code and defensively copied complete stdout bytes only. Outcomes distinguish Complete, NonzeroExit, ToolUnavailable, OutputLimit, InvalidEncoding, Deadline, ReadFailure and CleanupUnconfirmed; bytes are usable only for Complete/exit zero with fully settled capture. Raw stderr never enters the public result. The observer rechecks cap/encoding/outcome from custom readers; implementers must honor bounds/cancellation/settlement, and the existing nonconforming-runner limitation applies. Default fixed reader builds safe argv internally. `GcpCloudRunExecutionObserver.Create()` supplies production defaults; its public injection constructor takes the bounded reader and TimeProvider. Document all new public/internal members, preserve the issue's request/result/interface unchanged and never adapt IGcloudCommandRunner.

No cross-request state or new database/network service creates a scaling bottleneck; gcloud startup, cloud quotas and host concurrency are the limiting resources. A missing tool, permission denial or new provider shape returns Unknown with a distinct code. Provider/config/classifier failures cannot schedule recovery. Distribution remains coordinated GCP library + CLI tool with package closure and installed proof. Dispositions: E1/E2/E3/E6 accepted mechanically under P1/P5 and exact C0 bounds; no feature cut. T1/T2 remain final-owner choices.

### Engineering Section 2: Code quality

E7 [P1] (confidence 9/10) DeploymentContracts.cs:622 exposes an IReadOnlyList rather than an immutable list. Snapshot count/entries and artifact bytes once at call entry, then derive immutable expected facts before the first provider await. Reject inconsistent/malformed snapshots safely with zero provider reads. Do not keep references to the request's collection or mutable dictionary values across awaits. This follows the approved immutable snapshot requirement; it is not a new serialization format.

The shared bundle helper belongs in the GCP package as `GcpCloudRunBundleValidator`; its input is the immutable artifact snapshot, its output is validated immutable expected facts plus safe failure. Existing VerifyAsync is an authored caller at target:88; the proposed observer is the second caller, labeled proposed because it is not implemented. Extract the existing identity/hash/cross-artifact logic, preserving existing verifier exception/diagnostic semantics; the observer's stricter request/evidence admission stays at its boundary. Shared failure affects both consumers, so run helper-contract tests and both caller paths. This is a reliability extraction, not a line-savings claim: moved code counts as removed and added, and new tests/integration likely increase total lines. Actual line accounting occurs at implementation, separately for tests and product code; no estimated net deletion is invented.

E4 [P1] (confidence 8/10) design:202 requires one JSON result, while C1 adds cancellation delivery. Give one command-local writer sole ownership of serialization, stdout/stderr write/flush, and exit selection. Its states are Uncommitted -> Writing -> Delivered or Failed; only Uncommitted may choose cancellation instead of the normal result. Cancellation after any object write begins cannot emit a second object; exit is 130 when caller cancellation is observed before return, without claiming an incomplete write was delivered. Any stdout, stderr, serialization or flush failure latches delivery failure and prevents further output; ordinary failures exit 2. Build/serialize from bounded validated safe fields before commitment. Classify serialization/delivery defects as ASDEPLOY188 (unexpected safe stage can be logged only under existing redaction rules), and captured-process encoding/output-limit failure as ASDEPLOY180. Test admission stderr failure, serialization failure, mid-write/flush failure, cancellation after success bytes begin, and cancellation after a completed write. No catch may echo arbitrary exception messages.

E5 [P2] (confidence 8/10) design:214 requires diagnostic collision verification. Add a scoped test that enumerates the new diagnostic definitions, rejects duplicate reserved codes and checks each canonical reference anchor; rebase collision detection must include current production definitions, not ignore another branch's allocation. Reuse existing doc-contract testing conventions and do not create a generic diagnostic governance service. Full API/XML/README documentation is implementation work, including bounded-seam ownership and examples.

Keep methods small by evidence responsibility, using clear failure returns rather than a god switch with dozens of independent branches. No generic provider abstraction, inheritance layer, service locator or reflection test hook is justified. Existing diagrams remain accurate; add the cleanup/output state diagrams next to their implementation and update reference diagrams in the same change. Dispositions: E4/E5/E7 accepted under P1/P5; exact legacy behavior, C1, no raw diagnostics and all required coverage remain fixed.

### Engineering Section 3: Test review

Framework detected from the actual GCP test project: xUnit + Microsoft.NET.Test.Sdk/VSTest + coverlet.collector, net10.0. Repository AGENTS requires branch-focused verification, public/intentionally internal seams, no reflection and regression tests. Dedicated source/test reads were completed before this diagram: target:88–222 and :505–675 with GcloudCommandRunnerTests and target tamper tests; then target:357–429 with normalization tests and doctor admission/renderer tests. Existing tests establish legacy behavior only; none is counted as execution-observer coverage.

```text
CODE PATHS (new proposal)                             USER FLOWS
ObserveAsync                                         direct API / published bundle
 |-null -> ArgumentNullException [planned CP1]         |-invalid bundle -> zero read [CP1]
 |-snapshot/validate [CP1,EC1,EC5]                     |-caller mutates list [EC1]
 |-canonical resource + binding [CP2]                  |-other execution succeeds [CP2,CP14]
 |-budget admission [CP7,EC3]                          |-budget expires before read [CP7]
 |-ReadAsync bounded seam [CP6,CP9,CP10,CP11]           |-no tool/auth -> Unknown [CP11]
 | |-start/argv/launcher -> unavailable [CP11]          |-Ctrl-C at each boundary [CP8,EC4]
 | |-stdout/stderr capture in either order [CP9]       |-concurrent independent calls [EC8]
 | |-overflow/UTF8/read fault -> stop [CP6,CP9]         |-late read failure after Running [CP13]
 | |-exit nonzero -> Unknown [CP11]                    |
 | `-kill/close/join -> cleanup unconfirmed [CP10]     CLI argv -> snapshot -> observer -> writer
 |-Normalize [CP3,EC2,EC6]                             |-malformed flags/help [CP12]
 | |-missing/dup/type/conflict/extra -> Unknown         |-one versioned JSON/text [CP12]
 | `-admitted R/D fields -> immutable snapshot         |-all exits0/1/2/3/130 [CP12,CP13]
 |-Classify identity/config/outcome [CP2-5]             |-broken stdout/stderr [EC4]
 | |-changed UID/stale generation -> Unknown           |-cancel during/after output [EC4]
 | |-config mismatch -> Unknown/false                  `-POSIX sh caller branches [CP14]
 | |-matching terminal -> success/fail/cancel
 | `-positive nonterminal -> delay -> sequential read
 `-return last Running only on clean idle expiry [CP13]

Legacy Render/Verify/runner -> existing regression suite [CP14, retained]
Packed API + installed CLI + two neutral examples [->integration CP12/CP14]
No LLM/prompt behavior, no eval suite; no new browser UI.
```

Coverage status: all proposed observer paths are future tests, not TESTED. Existing legacy tests are behavior+error coverage (★★★ for tamper/normalization/launcher) and are retained, but provide zero implemented new-observer coverage. Planned fourteen critical paths and eight edge groups cover every introduced branch category; these counts describe the authored test plan, not assertions executed or a percentage of implemented coverage. Each path's public observable, credible failure, why existing tests miss it, production-needed seam and no-claim are in the saved artifact. Native process tests address the real boundary that mocking would hide; pure classifier branches stay unit tests. Installed/packed paths cross more than three components, so integration proof is required. No new test framework, live mutation test or blanket extra E2E layer is introduced.

E8 [P1] (confidence 9/10) existing target tests at :570 normalize absent args and string counts, while design:114 explicitly rejects unsupported/missing execution evidence. Reusing those fixtures/parser as execution proof would miss strict presence/identity. Add independent execution fixtures and CP3/CP5 matrix rows, while preserving the old tests unchanged. This is required proof of C0, not an observed new runtime bug.

E9 [P1] (confidence 8/10) design:186's cleanup and C1's one-result behavior need native/command caller-path proof in addition to helper coverage. CP7–CP13 and EC4 explicitly exercise both completion orders, inherited pipes, delivery/serialization failures and caller/internal cancellation distinction. Do not infer settlement from root exit or success delivery from a partial object. Controlled pause/release points use production-needed TimeProvider/reader/output seams; no reflection or export solely for tests is needed. Nonconforming injected reader behavior remains outside the guaranteed deadline, but malformed returned result/cap/encoding must be rejected by the observer.

E10 [P2] (confidence 8/10) diagnostic and format manifest are future public contracts with no existing tests. EC6/EC7 add scoped manifest/code/anchor validation, and CP14 extends packed library/tool proof. No test is obsolete; no threshold/snapshot/timeout/CI/suppression relaxation is authorized. Formatting, focused tests/build/analyzers/doc checks, unchanged render/Verify and practical solution coverage are mandatory implementation work. Run package verification sequentially with solution coverage. Dispositions: E8–E10 accepted as required contract proof under P1, no separate runtime change.

Test Plan saved: `/Users/andrew/.gstack/projects/forge-trust-Runnable/andrew-main-eng-review-test-plan-20261024-issue891.md`. Includes fourteen critical path groups, eight edge groups, value cards, no tests to retire and T1/T2 pending choices. This is future QA input, not completed validation.

### Engineering Section 4: Performance

E11 [P1] (confidence 8/10) design:176's total monotonic budget spans bounded file admission, parse and nested process cleanup. Use E3's outer authority, not independent renewable full timeouts. For API duration validation choose a documented finite range of one positive TimeSpan tick through 4,294,967,294 milliseconds for both timeout/interval, with interval<=timeout; values outside it fail before dispatch. Timer scheduling can round internally, so guard every boundary with elapsed timestamps and never round the requested allowance up for launching. Tiny positive budget with no usable execution allocation returns Unknown with no process. Cleanup reserve remains min(250ms, remaining/10). Use TimeProvider-aware timers/delays and a 64-level JSON parser depth; total input bytes and schema-supported inventories bound memory. No additional arbitrary collection-count cap should exclude a valid bounded supported Job without documented evidence; process arrays incrementally and stop over byte/depth/schema limits.

The API timeout governs observation work; CLI retains its original outer deadline through bundle loading, API and serialization/write initiation. Cooperative output writes/flush receive that deadline and cancellation; no API can promise preemption of a blocked or nonconforming custom stream/OS syscall. Document that same physical limitation without claiming it satisfies a stronger real-time guarantee. Safe serialization is finite because identifiers/result fields are bounded. No p99 latency, total managed-memory byte ceiling or provider throughput number is measured. Retained bytes add decoding/DOM overhead; avoid repeated copies of the same artifacts and reuse one immutable expectation per call. No DB/N+1/cache/index applies; one sequential read per interval, no retries, no shared concurrency pool. At scale the host owns process/quota/concurrency admission.

Dispositions: E11 accepted P1/P5 as concrete implementation of already-approved ranges/bounds, with exact tests; no timeout enlargement or broad performance benchmark. Other performance concerns are acknowledged implementation evidence, not speculative new infrastructure.

### Engineering failure modes, scope and implementation order

| Path | Realistic failure | Handling / required proof | Visible |
| --- | --- | --- | --- |
| snapshot | mutable list or replacement | immutable snapshot, CP1/EC1 | Unknown before read or original expectation |
| bundle | self-consistent untrusted source | expectation-only semantics, EC5 | docs never assert authenticity |
| identity/config | another successful execution | strict path/UID/config, CP2/5 | Unknown/false |
| normalization/state | undocumented/missing fact | fail closed, CP3/4/EC2/6 | Unknown category |
| process | huge output or inherited pipe | cap, kill/close/join, CP6/9/10 | Unknown output/cleanup |
| budget | handoff/delay/read expiry | outer elapsed guard, CP7/13 | 2 or3 by exact contract |
| caller cancel | concurrent timeout | original-token precedence, CP8 | OCE or130 |
| CLI output | stderr/stdout/flush fails | single owner failure latch, EC4 | 2/130, no second write |
| distribution | CLI missing dependency/services | installed/packed proof, CP12/14 | release proof fails |

Zero planned critical silent handling/test gaps; all checks above remain unexecuted. T1/T2 are contract taste decisions, not claims that tests passed. Missing provider evidence still blocks certification/integration.

NOT in scope: source non-goals, minimal-profile redesign unless user chooses T1, richer machine fields unless user chooses T2, new SDK/service/signatures/public loader, unrelated legacy runner cleanup, extra report/certification service. C6 remains the only deferred scope proposal and is already in TODOS.md. No newly discovered TODO needs a second entry.

What already exists: immutable artifact bytes, Job verifier integrity, fixed launch rules, TimeProvider CLI conventions, doctor admission, xUnit/VSTest and coordinated package/tool proofs. Reuse is documented in Step 0/Section 2; strict execution normalization and bounded capture are new because their contracts differ. No net-line savings are promised.

| Step | Modules | Depends on |
| --- | --- | --- |
| evidence feasibility + rebase | GCP tests/fixtures | none; no integration before acceptance |
| library contracts/validator/classifier | GCP library + tests | evidence gate |
| runner + polling | GCP library + tests | library contracts; shared module, sequence |
| CLI + safe output | CLI + CLI tests | stable observer/reader contract |
| docs/examples | Deployment/CLI docs + neutral examples | stable contracts; prose may draft alongside implementation |
| package/coverage proof | package index + consumers | all preceding work |

Sequential implementation, no useful parallel coding lane within the main shared GCP module. After the evidence gate and stable library contract, docs can draft independently of CLI code, but merge/integration and shared-output tests remain sequential. No worktree creation or monitoring is initiated by this planning task. If implementation delegates later, use user-required combo/sub retry policy and bounded observer-only CI watchers; this plan does not authorize external mutation or messaging.

### Engineering implementation tasks

- [ ] **E-T1 (P1, human ~2–4h / agent ~45–90m, estimated)** — library/process — Specify and implement the bounded reader/result seam, immutable per-call expectations, cleanup state machine and outer-budget handoff.
  - Surfaced by: E1/E3/E6/E7/E11.
  - Files: GCP observer/reader/helper files to be added, GCP target for extraction, existing/new tests.
  - Verify: CP1/6–11, EC1/3/8; all required branches and actual declared OS process behavior.
- [ ] **E-T2 (P1, human ~1–2h / agent ~30–45m, estimated)** — CLI — Centralize bounded result serialization/delivery and preserve caller-token precedence without a second object.
  - Surfaced by: E4/E9.
  - Files: future CLI observation command/writer and tests, AppSurfaceCliApp.cs.
  - Verify: CP12/13, EC4, exact exits/safe output after every failure boundary.
- [ ] **E-T3 (P1, human ~2–3h / agent ~45–60m, estimated)** — tests/contracts — Implement the saved critical/edge test matrix, independent manifests and scoped diagnostic/reference anchors.
  - Surfaced by: E5/E8/E9/E10.
  - Files: GCP/CLI test modules, Deployment/reference.md and existing package proof extension.
  - Verify: CP1–14/EC1–8 and unchanged legacy suites; no tests retired; preserve all gates.
- [ ] **E-T4 (P2, human ~30–60m / agent ~15–20m, estimated)** — docs — Explain caller-expectation trust, certified shape limits and exact cancellation/deadline/output limits.
  - Surfaced by: E2 and native EN2/EN3/EN7.
  - Files: GCP README, Deployment reference/adoption and CLI guide/examples.
  - Verify: self-consistent-bundle truthfulness test, no v1 support without full mapping, no provider-observed claim from schemas.

These refine and overlap CEO/DX tasks; aggregation must merge by behavior rather than add all estimates. Estimates reflect OS/concurrency review rather than a boilerplate ratio. None of the work has been implemented or tested.

### Engineering decisions and acceptance record

| ID | Classification | Decision / principle | Rejected alternative |
| --- | --- | --- | --- |
| E0 | mechanical | retain Approach B and all scope, P1/P5 | collapse evidence/lifetime boundaries or cut acceptance |
| E1 | mechanical | explicit cleanup owner, close/join and native supported-platform proof, P1 | treat detached active readers/root exit as settlement |
| E2 | mechanical | caller-expectation trust docs and negative control, P5 | new signature/provenance service |
| E3 | mechanical | outer CLI elapsed budget authority and conservative remaining handoff, P1/P5 | renewable nested full timeouts or new public deadline framework |
| E4 | mechanical | one output commit/failure owner, P1/P5 | second cancellation object or exception-message fallback |
| E5 | mechanical | scoped code/anchor contract check, P1 | new general diagnostic governance service |
| E6 | mechanical | narrow bounded exact-reader seam/result and factory, P5 | unbounded adapter or arbitrary executable/argv API |
| E7 | mechanical | immutable call snapshot, P1 | retain caller-owned IReadOnlyList across awaits |
| E8 | mechanical | independent strict fixtures, old permissive tests unchanged, P1 | reuse Job omissions as execution proof |
| E9 | mechanical | controlled/native/command lifetime and output proof, P1 | helper-only or root-exit-only assertions |
| E10 | mechanical | manifest/code/packed caller proof, P1 | unsupported format declaration or untested package graph |
| E11 | mechanical | finite timer range, depth64 and one budget, P1/P5 | rounding positive allowance upward or unbounded parse |

Approval readiness: PASS for pipeline continuation, E0–E11 each authorized by /autoplan principles and exact C0 runtime/regression obligations. T1/T2 original values remain provisional and go to final owner gate. No new user-direction change is assumed. Required library/CLI signatures remain intact; new bounded seam is additive. Evidence feasibility/OS proof remains an implementation prerequisite rather than a claimed completed result.

<!-- autoplan-accepted:eng -->
- Preserve all original/CEO/DX obligations and the issue's proposed observer request/result/interface. Retain Approach B, one implementation wave, no scope cut, no current-Job fallback and no new orchestration/SDK/signature service. T1/T2 remain final-owner choices with original values provisionally retained.
- Snapshot the caller's artifact list and each immutable artifact's bytes once at observer entry; derive immutable expected facts before the first provider await. Reject malformed/inconsistent snapshots before provider access. Test mutation during admission/provider pause and concurrent independent calls; no expectation, UID, deadline or result may leak across calls.
- Use one process cleanup/result owner with Started/Reading/Stopping/ClosingPipes/JoiningReaders/Finished states and a failure latch. Normal completion joins both readers and exit before parsing. On overflow/read fault/deadline/cancellation kill the owned local tree, close both pipes, join the readers inside the existing reserve and prohibit success after any failure. Do not count root exit or detached active reads as settlement. Native declared-platform tests must prove descendant pipe behavior, no active reader at return, either-stream/exit completion orders and bounded settlement. Unconfirmed local process/tree cleanup yields Unknown; a platform without the required proof is uncertified. Preserve the already disclosed OS-start/nonconforming-reader/unkillable-process limitations rather than asserting a universal OS guarantee.
- Keep the original CLI deadline as outer monotonic authority from before bundle load through observer await and delivery; all nested time allowances are recomputed conservatively from remaining time and never renew the original allowance. Public API creates a per-call ObservationBudget at entry, using TimeProvider timestamps/elapsed time. CLI guards the outer await separately and only the original console token produces caller-cancellation exit130; internal deadline during a read/cleanup yields Unknown/2. A completed clean idle Running result retains its original receipt time and exits3. Check elapsed/caller token at handoff, parse and return boundaries. Test controlled pauses around file admission, CLI/API handoff, read, delay, parse, cleanup and serialization/write initiation; do not add a public deadline type or alter the proposed observer signature.
- Define additive public IBoundedGcloudExecutionReader.ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken) accepting only a canonical selected execution and remaining allowance, no executable or extra argv. BoundedGcloudExecutionReadResult is immutable, holds typed outcome, optional exit code and defensively copied complete stdout bytes only; no public raw stderr. Distinguish Complete, NonzeroExit, ToolUnavailable, OutputLimit, InvalidEncoding, Deadline, ReadFailure and CleanupUnconfirmed. Only Complete/zero exit/settled capture permits bytes to be parsed. The observer rechecks custom-result cap/encoding/outcome; bounded implementers must honor cancellation/lifetime, without an unbounded legacy adapter. Default reader constructs fixed safe describe argv; GcpCloudRunExecutionObserver.Create() supplies defaults and an injection constructor accepts the bounded reader and TimeProvider. Fully document new public/internal contracts and intentionally internal trusted child-launch test seam.
- Make caller-expectation trust explicit in both API/CLI quickstarts and reference: hash-consistent bundles are not authenticated publishers, applied revisions or pre-first-read submission provenance. Test a self-consistent changed bundle against corresponding evidence as caller expectation only. Add no signature requirement or trust-service dependency.
- Give one command-local serializer/writer ownership of all stdout/stderr write/flush and exit selection, with Uncommitted/Writing/Delivered/Failed states. Only Uncommitted may replace the normal result with a cancellation envelope. Never emit a second object after writing begins or completes; caller cancellation observed before return still selects130 without claiming partial delivery. Any serialization/stdout/stderr/flush failure latches delivery failure and suppresses all later writes; ordinary failure exits2. Use ASDEPLOY188 for serialization/delivery and ASDEPLOY180 for captured-process encoding/limit failure. Test admission stderr failure, serialization failure, mid-object/flush failure, cancellation before/during/after delivery and already-broken output. No raw exception text, progress or retry output is allowed.
- Validate the planned new code allocation against current production codes after rebase; add scoped tests for unique ASDEPLOY170–189 definitions and their stable canonical reference anchors before package publication. Do not infer atomic reservation across branches or rewrite unrelated diagnostics.
- Use explicit JSON MaxDepth64 with strict duplicate/type/inventory checks and incremental bounded admission; test depth64/depth65, byte caps and malformed additions. Durations must be positive, interval<=timeout, and no larger than 4,294,967,294 milliseconds; one positive TimeSpan tick is valid but may return Unknown with no child if no usable command time remains. Cleanup reserve remains min(250ms, remaining/10); never round launch allowance up. TimeProvider-aware timers/delays and elapsed checks preserve logical deadlines. Document cooperative stream/OS scheduling limits, without widening timeout/tolerance thresholds or claiming measured p99/memory totals.
- Carry the complete planned test groups CP1–CP14: zero-read invalid admission/null behavior; exact path/UID continuity; independent v1/v2 R/D/U malformed/default/inventory matrix; overall state/generation/counters; every captured configuration field; exact cap/cap+1/UTF8; original monotonic budget across API/CLI; caller cancellation precedence; simultaneous stream/exit orders; descendant/pipe cleanup; safe fixed read-only argv; installed command JSON/text/exits/admission; Running-versus-read-failure deadlines; both packed neutral consumers and unchanged legacy render/Verify/custom-runner compatibility. Use public/intentionally internal production seams, deterministic time and native process proof, with required protects/fails_when/no_claim cards and no reflection. No proposed path is claimed tested by legacy tests.
- Carry edge groups EC1–EC8: mutable artifact lists; depth64/depth65/duplicate/conflicting JSON; tiny/invalid/above-range timers; all single-writer delivery/cancellation failures; caller-expectation trust; unavailable R/unproven D/wrong hash/version/OS/argv/maintainer manifests; diagnostic-code/anchor collisions; interleaved independent observer calls. Each entry in the saved QA test plan has observable/failure/why-new/seam/no-claim evidence. Preserve all old tests; no test retirement, threshold/timeout/tolerance/snapshot/suppression/CI relaxation is approved. Package verification and solution coverage stay sequential in one checkout; report actual commands/exits/counts and separate product/test line additions/removals.
<!-- /autoplan-accepted:eng -->

### Engineering completion summary

| Field | Result |
| --- | --- |
| Scope Challenge | scope accepted as-is, E0; three source-grounded scope findings |
| Reality | eleven applicable rows, one unverified provider-format premise |
| Architecture | four planned refinements E1/E2/E3/E6 |
| Code quality | three planned refinements E4/E5/E7 |
| Tests | full diagram; three proof-detail findings E8/E9/E10; fourteen critical/eight edge groups unimplemented |
| Performance | one refinement E11, no measured benchmark |
| Four-section issue count | eleven, resolved into planned obligations; no runtime fix or test execution claimed |
| NOT in scope / existing code | written; reliability extraction, no net-deletion claim |
| TODOs | one inherited C6 entry, no new duplicate |
| Failure modes | nine rows, zero planned critical silent gaps |
| Unresolved in Engineering | zero new choices; inherited T1/T2 owner gate retained |
| Outside voice | Claude Code unavailable, not installed; native completed with seven findings |
| Parallelization | sequential main module; docs may draft after stable contracts, proof sequential |
| Lake Score | N/A, no human coverage-scored question |
| Test-plan artifact | saved at the required project-state path, future QA input |
| Evidence limits | no certified gcloud shape, provider read, implementation tests, native OS proof or measured TTHW |

Suppressed findings: no additional low-confidence runtime defect is promoted. Native EN3's possible always-Unknown outcome remains the explicit feasibility gate, not proof that gcloud cannot work; EN7 v1 is uncertified until its exact mapping is independently established.

REALITY: premise-table pass inspected sources distinguish verified facts from unverified=1 provider-format feasibility Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:88
REALITY: already-done pass refreshed main; no observer symbol or execution command implementation at review time Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs:88
REALITY: surface-check finding prospective observe-execution command and flags require registration and installed help proof Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs:87
REALITY: binding-decisions pass issue-specific office-hours answers retained; unrelated branch decisions do not change them docs/designs/issue-891-cloud-run-execution-observation.md:17
REALITY: numbers pass caps/defaults are requirements; effort is estimated; no latency or adoption measurement claimed docs/designs/issue-891-cloud-run-execution-observation.md:180
REALITY: deferred-asks pass all issue acceptance remains required; real-adopter measurement is an extra follow-up only TODOS.md:3
REALITY: pr-count pass use "default one PR per wave" because no ship-policy override exists; staged tasks do not split PRs docs/designs/issue-891-cloud-run-execution-observation.md:5
REALITY: prior-art pass diverge because existing package deliberately uses gcloud; official execution schema and exact describe docs retained; no vendored patch Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md:16
REALITY: contract-assumptions pass tool output remains candidate until exact-version fixture acceptance; no SDK or assumed field fallback docs/designs/issue-891-cloud-run-execution-observation.md:109
REALITY: dependency-fallback pass no certified format blocks integration; legacy verifier and application observer remain available docs/designs/issue-891-cloud-run-execution-observation.md:149
REALITY: obligation-tiers pass must ship all issue guarantees in the same wave; adopter outcome measurement deferred; no new infrastructure TODOS.md:3

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
| --- | --- | --- | --- | --- | --- | --- |
| C0 | CEO | Retain approved contract | mechanical | P1 | Exact user D8 scope | scope cuts |
| C1 | CEO | Specify broken-output cancellation | mechanical | P1/P5 | Closes prior R2-1 | ambiguous output failure |
| C2 | CEO | Add five-exit recipe | mechanical | P2 | Direct docs/examples scope | bare table only |
| C3 | CEO | Reject extra IAM doctor | mechanical | P4 | No extra provider operations | new doctor |
| C4 | CEO | Reject support dashboard | mechanical | P4 | Manifest already consumed | duplicate report |
| C5 | CEO | Reject persisted watch/resume | mechanical | P5 | Keep stateless scope | new lifecycle |
| C6 | CEO | Defer real-adopter measurement | mechanical | P3 | Needs actual owner/access | new acceptance gate |
| C7 | CEO | Specify safe diagnostic actions | mechanical | P1 | Make Unknown actionable | automatic retries |
| C8 | CEO | Split helpers by facts/lifetime | mechanical | P5 | Clear small responsibility | generic framework |
| C9 | CEO | Bound parser admission | mechanical | P1 | Finite depth/bytes | unbounded nesting |
| C10 | CEO | Join before classification | mechanical | P1 | Failure cannot race success | root-exit-only check |
| CS1 | CEO | Map all acceptance criteria | mechanical | P1 | Spec round1 traceability | external reference alone |
| CS2 | CEO | Move normative C1 into contract | mechanical | P5 | Spec clarity | tail-only override |
| CS3 | CEO | Label provider shapes candidate | mechanical | P1 | No compatibility claim | schema implies support |
| CS4 | CEO | Operationalize worksheet stop gate | mechanical | P1 | Any missing R blocks shape | always-Unknown shipment |
| T1 | CEO | Retain full parity provisionally | taste | P1 | Match exact user acceptance; final d1 | minimal evidence profile |
| DX0 | DX | Classify library + CLI + docs | mechanical | P6 | README and issue audience | hosted platform |
| DXP | DX | Infer platform developer persona | mechanical | P6 | Owns existing submission/CI | MVP signup persona |
| DXE | DX | Accept source-grounded empathy prediction | mechanical | P6 | Label as predicted not interview | invented runtime outputs |
| DXT | DX | Prepared-machine 2–5 minute target | mechanical | P6 | Explicit prerequisite clock | unmeasured champion claim |
| DXM | DX | Use existing guide/examples vehicle | mechanical | P5 | Lowest-cost same capability | hosted playground |
| DXMode | DX | Select DX POLISH | mechanical | driver | Explicit autoplan override | unbounded expansions |
| DX1 | DX | Complete quickstart | mechanical | P1/P5 | Declared docs scope | full design as onboarding |
| DX2 | DX | One manual first-use walkthrough | mechanical | P1 | Measure named clock | new analytics/timing gate |
| DX3 | DX | Label fixture versus provider proof | mechanical | P1 | Truthful evidence | fixture cloud claim |
| DX4 | DX | Progressive canonical doc links | mechanical | P5 | Existing source and package paths | new docs site |
| DX5 | DX | Document existing API construction | mechanical | P5 | Public artifacts already exist | new loader API |
| DX6 | DX | Early safe argv admission | mechanical | P1 | Actual pre-CliFx hook | raw parser error |
| DX7 | DX | Document certified environment | mechanical | P5 | PATH/config ownership | arbitrary command escape |
| DX8 | DX | Specify fixed diagnostic reference | mechanical | P1 | Safe code categories/actions | raw exception output |
| DX9 | DX | Use POSIX sh recipe | mechanical | P5 | Concrete shell contract | unclaimed shell portability |
| T2 | DX | Retain code-only result provisionally | taste | P5 | Preserve exact API/JSON; CAP6 explicit; final d2 | additive rich machine error contract |
| E0 | Eng | Retain original structure | mechanical | P1/P5 | Distinct trust/lifetime responsibilities | scope cuts |
| E1 | Eng | Explicit cleanup state machine | mechanical | P1 | Close/join native proof | detach active readers |
| E2 | Eng | Caller expectation trust | mechanical | P5 | No publisher authentication claim | signature service |
| E3 | Eng | Outer budget authority | mechanical | P1/P5 | No renewable timeout | public deadline framework |
| E4 | Eng | One output owner | mechanical | P1/P5 | No second object | exception fallbacks |
| E5 | Eng | Scoped diagnostic anchor checks | mechanical | P1 | Stable public discovery | governance service |
| E6 | Eng | Narrow bounded reader seam | mechanical | P5 | Additive production contract | legacy unbounded adapter |
| E7 | Eng | Immutable call snapshot | mechanical | P1 | IReadOnlyList may mutate | borrow request state |
| E8 | Eng | Independent strict fixtures | mechanical | P1 | Legacy parser differs | reuse permissive tests |
| E9 | Eng | Native/command caller proof | mechanical | P1 | Mocking hides pipes/output | helper coverage alone |
| E10 | Eng | Manifest/packed proof | mechanical | P1 | New public contracts | unproven support declaration |
| E11 | Eng | Finite timer/depth limits | mechanical | P1/P5 | Concrete bounded admission | round allowance up |

Audit consolidates the decisions recorded throughout the phase ledgers; it does not invent human answers or turn provisional taste into approval. Total 44 decisions: 42 mechanical, two taste, zero user challenges.

## /autoplan Review Complete

## URGENT, outside this plan

None.

### Plan Summary

The reviewed #891 plan adds one read-only observer and CLI command that prove the selected Cloud Run execution matched the full intended configuration and reached a supported state. First establish a certified gcloud evidence format, then implement the bounded pipeline, two neutral consumers and package proof. CEO, DX and Engineering native reviews are complete; DX is 7.75/10, provider compatibility and onboarding time remain unmeasured, and Claude Code outside coverage is unavailable.

### Decisions Made: 44 total (42 mechanical, 2 taste choices, 0 user challenges)

Auto-decided: see Decision Audit Trail. Gate d1 maps to T1, d2 to T2; p1 is the tool-schema identifier for the logical plan approval. Neither default is a human answer. No implementation/release/cloud access is authorized by this plan-review gate.

### Review Scores and Coverage

| Phase | Result | Native reviewer | Outside provider/status | Consensus / partial coverage |
| --- | --- | --- | --- | --- |
| CEO | full sections, spec 9/10 after two rounds; six scope proposals | Codex-host Luna, completed 13 concerns | Claude Code unavailable, not installed | N/A; native only |
| Design | skipped, no visual UI scope | not run | skipped | scope skip, not completed review |
| DX | 6.375 ->7.75/10 predicted; TTHW unmeasured | Codex-host Luna, completed 10 findings | Claude Code unavailable, not installed | N/A; native only; CAP6 machine detail remains |
| Engineering | full review, eleven refinements and saved QA plan | Codex-host Luna, completed seven findings | Claude Code unavailable, not installed | N/A; native only; no implemented/tested claim |

CYCLE: actual 39m (estimate 41m) | ceo 2m (outside 0s) | dx 8m (outside 0s) | eng 12m (outside 0s)

Timing caveat: CEO phase timer began late, so its 2m is partial; voice seconds were not independently measured and zero is a placeholder, not zero reviewer time. The whole-cycle timestamp measurement includes the elapsed review work. Historical estimates are not deadlines.

### Cross-Phase Themes

- CEO, DX and Eng independently flagged provider-shape feasibility; exact-version worksheets and maintainer acceptance precede integration, with no manufactured R fact.
- DX and Eng flagged error/delivery behavior; C1 and single-writer ownership are concrete, while T2 keeps the machine-detail tradeoff visible.
- CEO and Eng emphasized the process/deadline boundary; byte caps, monotonic budgets and supported-platform cleanup proof stay mandatory.
- All phases distinguish synthetic/parser evidence from provider compatibility and real adoption.

### Deferred to TODOS.md

C6: real-adopter outcome/Unknown-rate measurement, blocked on a consenting actual workflow, certified package and separately authorized access/release. It adds no issue acceptance gate and does not replace the two mandatory neutral consumers.

### Implementation Tasks (aggregated across phases)

Scoped to this invocation as well as branch/recent commits to exclude concurrent reviews of other issues. Exact dedupe preserves distinct tasks; overlapping product work is marked for integration rather than summing estimates.

- [ ] **T2 (P1, human: 4-6h / agent: 1-2h) — API/evidence** — Implement shared validation and strict execution classification
  - Surfaced by: ceo-review — Sections 1/3/5/6
  - Files: Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/GcpCloudRunDeploymentTarget.cs

- [ ] **T4 (P1, human: 3-4h / agent: 1-1.5h) — CLI** — Register safe observe-execution command and exact exits
  - Surfaced by: ceo-review — C1; Sections 2/4/6
  - Files: Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs

- [ ] **T1 (P1, human: 4-6h / agent: 1.5-2h) — evidence** — Prove exact-version execution evidence before integration
  - Surfaced by: ceo-review — PR5, N1/N4/N6/N9
  - Files: to be determined at implementation; no invented current file

- [ ] **T6 (P1, human: 2-3h / agent: 0.5-1.5h) — packaging/verification** — Verify package closure and preserved regressions
  - Surfaced by: ceo-review — Sections 6/9
  - Files: to be determined at implementation; no invented current file

- [ ] **T3 (P1, human: 4-6h / agent: 2h) — process/polling** — Implement bounded runner and total-budget observer
  - Surfaced by: ceo-review — Sections 2/4/7
  - Files: to be determined at implementation; no invented current file

- [ ] **E-T2 (P1, human: 1-2h / agent: 30-45m) — CLI** — Centralize single-result delivery and cancellation precedence
  - Surfaced by: eng-review — E4/E9
  - Files: Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs

- [ ] **E-T1 (P1, human: 2-4h / agent: 45-90m) — library/process** — Implement bounded reader, immutable context and cleanup state machine
  - Surfaced by: eng-review — E1/E3/E6/E7/E11
  - Files: to be determined at implementation; no invented current file

- [ ] **E-T3 (P1, human: 2-3h / agent: 45-60m) — tests/contracts** — Implement critical and edge matrices with manifest and diagnostic proof
  - Surfaced by: eng-review — E5/E8/E9/E10
  - Files: to be determined at implementation; no invented current file

- [ ] **DX-T2 (P1, human: 1-2h / agent: 20-40m) — CLI/diagnostics** — Implement safe admission and fixed diagnostic guidance
  - Surfaced by: devex-review — D-N3/N5/N8/N9/N10; Pass 3
  - Files: Cli/ForgeTrust.AppSurface.Cli/AppSurfaceCliApp.cs, Deployment/reference.md

- [ ] **DX-T1 (P1, human: 1-2h / agent: 20-40m) — docs/examples** — Complete first-use and API construction path
  - Surfaced by: devex-review — DX1; D-N1/N2/N4/N7
  - Files: Deployment/reference.md, Cli/ForgeTrust.AppSurface.Cli/README.md

- [ ] **T5 (P2, human: 2-3h / agent: 1h) — docs/examples** — Document runbook and two neutral consumers with exit recipe
  - Surfaced by: ceo-review — C2/C7; Sections 6/8/10
  - Files: Deployment/reference.md, Cli/ForgeTrust.AppSurface.Cli/README.md

- [ ] **E-T4 (P2, human: 30-60m / agent: 15-20m) — docs** — Explain bundle trust, certified shape and lifetime limits
  - Surfaced by: eng-review — E2; EN2/EN3/EN7
  - Files: Deployment/reference.md, Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md

- [ ] **DX-T3 (P2, human: 30-60m / agent: 15-20m) — environment/adoption** — Document certified environment and upgrade path
  - Surfaced by: devex-review — D-N6; Passes 5/6
  - Files: Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md, Cli/ForgeTrust.AppSurface.Cli/README.md

Possible overlap: CEO-T2/T3 with E-T1/E-T3; CEO-T4 with DX-T2/E-T2; CEO-T5 with DX-T1/DX-T3/E-T4; CEO-T6 with E-T3. Merge requirements into one implementation checklist, preserving every condition; do not add overlapping effort estimates.


### Final implementation sequence

1. Rebase and audit command/diagnostic/package collisions; complete both evidence worksheets, candidate fixtures, manifest/state table and named maintainer acceptance. Stop on unavailable R/unproven D proof; return to design if no candidate works.
2. Implement the immutable validator/strict classifier and additive bounded reader/observer with required regressions and native lifetime proof.
3. Implement safe CLI admission/single output owner, exact JSON/exits, two neutral consumers and all docs/runbooks/first-use check.
4. Run format/build/analyzer/doc/focused tests, practical solution coverage and packed library/tool proof sequentially; report actual commands/counts and separate implementation/test lines. Release/cloud reads remain separately authorized.

### Pre-gate verification

CEO and Eng reality checks both complete, 11/11 rows, unverified=1. Full required sections/registries/diagrams/consensus tables and CEO/DX/Eng summaries are saved. All three applicable native INPUT hashes match their immutable snapshots. Design skip and all missing outside coverage are explicit. Test-plan and 13 aggregated tasks are saved; one C6 TODO exists. Phase packets were read through EOF with truncated ranges repaired and semantically reconciled, then parent phase reports were actually sent before advancing. No product code, implementation tests, cloud resources, PR, release or Git commit was changed.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
| --- | --- | --- | --- | --- | --- |
| CEO Review | /autoplan | Scope and strategy | 1 this invocation | approved with evidence gates | six proposals: two accepted, one deferred, three skipped; T1 accepted as d1a |
| Outside Review | Claude Code preflight | Independent external challenge | 0 completed | unavailable | not installed in CEO/DX/Eng; no external finding count or agreement |
| Eng Review | /autoplan | Architecture and tests | 1 this invocation | reviewed with planned fixes | eleven four-section refinements; three scope/seven native findings dispositioned |
| Design Review | scope detector | Visual UI | 0 | skipped | no UI scope |
| DX Review | /autoplan | Developer experience | 1 this invocation | concerns remain | 7.75/10; T2/CAP6; TTHW unmeasured |

**OUTSIDE COVERAGE:** Claude Code unavailable for CEO, DX and Eng; Design skipped. All applicable native reviewers completed. No cross-model confirmation is claimed.

**VERDICT:** DONE_WITH_CONCERNS. The owner approved all recommendations via `Rec` on 2026-10-24 for run `main-20261024-135210`, gate revision 1. Provider compatibility, native process behavior, implementation tests and package delivery remain future evidence. The approved plan retains the evidence-first stop gate and the accepted machine-diagnostic CAP6 tradeoff.

**COMPLETION RECORD:** Approval and all three applied gate decisions are saved. CEO, DX, Engineering and four phase-voice review records were written successfully; Design is explicitly skipped, and Claude Code outside coverage remains unavailable. One durable bounded-runner learning was saved. These are planning records, not implementation validation.

**OWNER DECISIONS:**
- T1 / d1a: keep full required configuration parity and the evidence-first stop gate.
- T2 / d2a: retain the proposed code-only API/JSON with safe text and a fixed diagnostic reference, accepting the documented CAP6 tradeoff.
- p1a: approve the reviewed plan as written. No owner decisions remain pending.
