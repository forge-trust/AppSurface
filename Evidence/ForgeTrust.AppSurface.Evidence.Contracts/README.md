# ForgeTrust.AppSurface.Evidence.Contracts

`ForgeTrust.AppSurface.Evidence.Contracts` is the stable vocabulary for a CI run that says what changed, which evidence was required, what actually ran, and whether a downstream gate may consume the result.

Start with the [EvidenceHost guide](../../start-here/evidencehost.md) and the [migration reference](../../docs/evidence/evidencehost-migration.md) before installing a package. Use this package directly only when you are authoring a consumer-owned producer, policy tool, or gate integration. It starts no process, discovers no test code, provisions no resources, and sends no telemetry.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Contract shape

An `EvidencePolicy` resolves into an immutable `EvidencePlan`. A run records declared `EvidenceProducerResult` values and then uses `EvidenceManifestBuilder.Build(...)` to produce an `EvidenceManifest`. Both plan and manifest have canonical JSON and SHA-256 digests, so a gate can verify a result without rerunning the test suite.

| Type family | Purpose |
| --- | --- |
| `EvidencePolicy`, `EvidenceProfile`, `EvidencePolicyRule` | Checked-in change-risk policy and explicitly selected profile. |
| `EvidenceResourceDeclaration`, `EvidenceProducerDeclaration`, `EvidenceObligation` | Closed declaration of resource readiness, producer assertions/artifacts, and the risk obligation each assertion may close. |
| `EvidencePlan`, `NormalizedDiffPath` | Deterministic resolved input. The plan binds policy identity, diff, selected profile, and matched rules. |
| `EvidenceProducerResult`, `EvidenceManifest` | Terminal producer outcomes and the resulting gate claim. |
| `EvidenceCanonicalJson`, `EvidenceDigest`, `EvidenceManifestBuilder` | Canonical serialization, digesting, claim calculation, and manifest verification. |

### Bounded JSON input

`EvidenceCanonicalJson.Deserialize<T>(ReadOnlySpan<byte>)` preserves the existing in-memory entry point and enforces a protected **20 MiB** input ceiling. Use `DeserializeAsync<T>(Stream, maximumBytes, cancellationToken)` when reading a file or another stream: it counts bytes while reading, does not depend on seekability or advertised length, and stops after observing the first byte over the limit, before parsing or allocating a full document. The stream remains owned by the caller. `maximumBytes` may lower the ceiling (including zero) but cannot raise it. The span overload with an explicit limit is available for callers that already hold bytes.

All deserialization entry points reject malformed JSON, duplicate or case-colliding property names anywhere in the document, unknown enum strings, and numeric enum values. Documents may contain at most 64 nested object or array levels. Invalid UTF-8 property names and unpaired surrogate escapes also produce a generic `JsonException` without exposing the underlying decoder exception or input. Valid Unicode names and paired surrogate escapes remain supported. Known enum names remain case-insensitive. Required constructor members must be present; non-nullable members and contract collection items cannot be `null`. Optional constructor defaults and explicitly nullable fields remain supported. Unknown object properties are ignored so additive optional fields remain compatible; they are also omitted when the parsed contract is serialized again. `EvidencePlan` and `EvidenceManifest` must declare contract version `1.0`, the only version currently emitted by the planner and manifest builder. Diagnostics use bounded generic messages: size failures throw `InvalidDataException`; stream reads normalize `IOException`, `UnauthorizedAccessException`, `NotSupportedException` and `ObjectDisposedException` to the same safe exception. Schema and JSON failures throw `JsonException`, invalid lower-limit arguments throw `ArgumentOutOfRangeException`, and stream cancellation propagates `OperationCanceledException`. Other exceptions from a caller-supplied custom stream propagate unchanged; the caller must handle or sanitize them at its own boundary.

`Serialize<T>` retains the existing canonical output, including numeric output for undefined enum values supplied by in-process callers. Such numeric enum input is rejected by deserialization; producing bytes does not grant evidence authority. The strict enum converter is used only when reading.

Schema and contract-collection checks run for every typed contract object that is deserialized, including values inside arrays, lists, dictionaries, or caller-defined wrapper objects. Wrapping an `EvidencePlan` or `EvidenceManifest` does not bypass its supported-version check. A raw `JsonElement` remains untyped JSON; deserializing it does not validate it as an Evidence contract or grant authority.

The span overload cannot limit memory already allocated by its caller. Prefer the counted stream overload at an untrusted file or stream boundary, and do not use a stream length check as a substitute for counted reading. These methods validate structure and format; they do not authenticate who supplied the JSON or make a plan trusted.

`EvidenceClaimKind.TargetedComplete`, `ReleaseComplete`, and `NoEvidenceRequired` describe structural manifest outcomes. They do not, by themselves, admit a protected pull-request or release gate. `ObservationOnly` is informational and never gate-eligible. A protected downstream consumer must call `EvidenceProtectedGate.Allows(...)` with current expected facts and plan/manifest artifacts obtained through its protected channel; see [runtime admission and gate evaluation](#runtime-admission-and-gate-evaluation). `NoEvidenceRequired` is valid only when the selected profile declares no resources, producers, or obligations.

## Claim rules

A complete claim is deliberately conservative:

- every selected producer must report `Passed`;
- every obligation's required producer and assertion must be present;
- producer results may not name undeclared producers or assertions;
- a release profile requires a registered CI-envelope verifier; and
- the manifest digest and plan digest must verify unchanged.

An unavailable capability, timeout, skipped producer, incomplete test profile, or failed assertion therefore produces `None`, not a partial success. This is how EvidenceHost distinguishes an observation from evidence that can mediate risk.

Required artifact slots are part of successful producer completion. A `Passed` result that omits a required slot makes the manifest `Invalid`. An unsuccessful producer may omit outputs it could not produce; valid absent or partial outputs leave its manifest `Incomplete`, with no closed obligation or gate eligibility. All returned metadata must still name declared slots and satisfy the byte, media type, digest and containment rules, regardless of outcome. Malformed partial outputs remain `Invalid`.

An explicit producer `Invalid` outcome always makes the execution verdict `Invalid`, even if artifact finalization removes its rejected metadata. This preserves a contract violation while ordinary failed, cancelled, unavailable or timed-out work remains `Incomplete`.

The public `EvidenceArtifactValidation.AreValid(producer, artifacts)` always checks every required slot, including when `artifacts` is null or empty. Use it to check complete artifact sets. The manifest builder and structural verifier share an internal overload that relaxes only required-slot presence for unsuccessful outcomes; it neither grants runtime admission nor skips metadata validation. Use the [runtime admission and gate evaluation APIs](#runtime-admission-and-gate-evaluation) to construct or consume a claim.

## Artifact paths and storage layout

`EvidenceArtifactSlot.RelativeRoot`, `EvidenceArtifactWriter.WriteAsync` paths, and
`EvidenceArtifactResult.RelativePath` are relative to the producer's artifact directory.
Protected hosts place each producer beneath its normalized producer ID in the run output directory.
Resolve a returned artifact as `Path.Combine(outputDirectory, producerResult.ProducerId, artifact.RelativePath)`.
Plan, manifest, and summary files remain directly beneath the run output directory.

For producer ID `coverage` and report metadata path `coverage/merged/coverage.cobertura.xml`, the layout is:

```text
<run-output>/
  evidence-plan.json
  evidence-manifest.json
  evidence-summary.json
  coverage/                           # producer directory
    coverage/merged/coverage.cobertura.xml  # metadata RelativePath
```

The repeated `coverage` component is valid: the producer ID and declared slot root are independent.
Keep the producer ID out of paths passed to `WriteAsync`; the protected writer adds that storage prefix
and verifies bytes at the same location. Its returned metadata preserves the path checked against the
declared slot root. The public `EvidenceArtifactWriter(producer, rootPath)` constructor writes directly
beneath the supplied `rootPath`; a host using that constructor supplies the producer directory itself.
Use the [protected artifact collection channel](../../docs/evidence/evidencehost-migration.md#protected-gate-and-artifacts)
when consuming protected output; resolving a filesystem path does not authenticate its bytes or provenance.

### Private allocation diagnostics

The internal `EvidenceLinuxArtifactRoot.Allocate(parentPath, expectedParentIdentity, slotName, expectedUid, expectedGid, out operation)` overload retains an `EvidenceLinuxArtifactAllocationOperation` value before argument/platform validation, descriptor opens, identity checks, exclusive slot creation, and descriptor transfer. It returns `Completed` only on success. The original overload delegates to it and discards the operation. Both overloads preserve the same ownership, mode, symlink, mount, identity and freshness guards and exception types; there is no observer callback or exception-data channel.

Use the operation only to locate a failed host operation, never to authenticate an output or grant admission. A failed operation can include multiple guarded checks; it does not by itself prove an errno or filesystem cause. The protected CLI combines it with a closed phase and failure classification through its [private worker diagnostic](../ForgeTrust.AppSurface.Evidence.Cli/README.md#private-worker-allocation-diagnostic). Successful allocation still requires separate admission activation and all existing stopped-work, verification and collection checks.

Unsupported `openat2` failures retain the direct known errno (1, 22, 38 or 95)
as a `Win32Exception` inner exception while preserving
`PlatformNotSupportedException` and the same fixed message. The private
allocation diagnostic accepts those four numeric values only for the closed
`openat2` operations in the allocation phase; missing, nested, unexpected or
unrelated errors retain a null errno. This adds diagnostic evidence and supplies
no fallback or capability. See the [private worker diagnostic reference](../ForgeTrust.AppSurface.Evidence.Cli/README.md#private-worker-allocation-diagnostic).

## Runtime admission and gate evaluation

Runtime claim construction is separate from JSON structure and from downstream authorization:

| API | Contract |
| --- | --- |
| `EvidenceExecutionMode` | Explicit `Trusted` or `Observation`; environment, branch, and Boolean defaults do not select it. |
| `EvidenceExecutionRequest(EvidenceExecutionMode Mode, string ControlChannel)` | Carries the requested mode and absolute Unix control socket supplied by the protected Linux launcher. It is request data, not proof or authority. The execution entry authenticates the independent worker and protected descriptor before admission. |
| `EvidenceAdmissionResult` | Publicly visible single-use runtime capability with no public constructor or JSON factory. Only shared protected admission can issue it. |
| `EvidenceManifestBuilder.Build(plan, results, admission, ...)` | Accepts a completed admission bound to the exact plan. The capability is consumed once, after owned work has stopped, artifacts have been verified, and cleanup has completed. |
| `EvidenceProtectedGateExpectation` | Required protected expected facts plus a final release opt-in that defaults to `false`; exact positional constructor shape and root-field mapping follow. |
| `EvidenceProtectedGate.Allows(EvidencePlan plan, EvidenceManifest? manifest, EvidenceProtectedGateExpectation expected)` | Throws for null `plan` or `expected`; returns `false` for a null manifest or failed check. Requires Trusted mode, passed verdict, `ValidatedNotAttested`, completed cleanup, no terminal failure or unmediated obligations, a matching plan policy digest, a valid assertion, exact expected facts, and valid structural bindings. It does not rerun work or create runtime admission. |

The public expectation record has this positional constructor. All string parameters are required; the final release
opt-in is the only parameter with a default:

```csharp
public sealed record EvidenceProtectedGateExpectation(
    string RunId,
    string BaseRevision,
    string SubjectRevision,
    string WorkflowIdentity,
    string PolicyDigest,
    string AcceptanceProofDigest,
    string OutputIdentity,
    string VerifierId,
    string VerifierVersion,
    string Provider,
    string CatalogueDigest,
    string CapabilitiesDigest,
    string AllocationPolicyDigest,
    string ToolRootIdentity,
    string SubjectRootIdentity,
    string OutputParentIdentity,
    bool AllowReleaseValidatedNotAttested = false);
```

The plan, manifest, and expectation are not their own provenance. Obtain the expectation, including all three root
identities, and collected artifacts through the consumer's protected gate channel; do not derive expected values
from uploaded subject files or an untrusted artifact pair. `ToolRootIdentity` identifies the protected tool root,
`SubjectRootIdentity` identifies the separately restricted subject root, and `OutputParentIdentity` identifies the
protected allocation parent of the output handle. The gate compares each expected root with the corresponding
assertion field. The positional record has 16 required string arguments and no constructor validation; `Allows`
checks exact values against the protected assertion and requires a nonblank output identity. Its other assertion
constraints include schema `1.0`, a nonblank run ID of at most 256 characters without controls, nonblank verifier,
provider, workflow, revision, and root identities of at most 256 characters, pairwise-distinct root identities,
64-character hexadecimal allocation, catalogue, capability, and acceptance-proof digests, and a nondefault verifier
time. A release gate must explicitly set `AllowReleaseValidatedNotAttested` to `true`; its default is `false`. This
opt-in accepts `ValidatedNotAttested`, which records registered-verifier validation and does not claim independent
attestation.

Observation admission requires an allowlisted dependency-free profile, its exact selected producer IDs, and an
exact internally registered producer class `(Kind, Version)` for every producer. The current class map admits only
`coverage@1.0.0`; selecting an ID or profile at the repository root cannot authorize an unknown or privileged
producer kind. The class map is internal and adds no public API.

The legacy `EvidenceManifestBuilder.Build(plan, results, bool observationOnly, EvidenceEnvelopeStatus, ...)`
overload always fails with `ASEVD400`; false/true Boolean values and status enums cannot construct authority. The
Aspire early-builder API and legacy `RunAsync(bool)` migration diagnostics are listed in the [migration reference](../../docs/evidence/evidencehost-migration.md#diagnostics-and-recovery).

### Shared lifecycle and output limits

Protected registrations may lower stage deadlines, but cannot raise these limits. Declared serial work, collection,
and cleanup reserves are admitted against one frozen allowance using a monotonic clock. Admission is conservative:
all stage caps plus collection and cleanup must fit before work starts. Stopping is included in cleanup and is not
counted twice. These are implementation ceilings, not a support claim: the production Trusted proof allowlist is
empty, and the production Aspire host rejects configured application factories with `ASEVD407`. See the migration
reference's [support status](../../docs/evidence/evidencehost-migration.md#support-status).

A callback, tracked write, or collector must settle **before** its monotonic deadline. Completion exactly at
the deadline is a timeout, even if the timer notification has been delayed. Stop acknowledgements and disposal
are subject to the same elapsed-time check before cleanup or collection may advance. A late disposer or stop
acknowledgement takes the fatal termination path; it cannot restore eligibility or permit further finalization.

| Limit | Maximum |
| --- | ---: |
| Admission | 30 seconds |
| Host/resource start | 120 seconds |
| Final collection | 60 seconds |
| Cleanup, including stop | 10 minutes |
| Stop grace within cleanup | 30 seconds |
| Aggregate artifacts and restricted reports | 256 MiB |
| Process/reporter output bytes received per run | 16 MiB |
| Retained output prefix | 1 MiB per stream |

The restricted coverage path additionally limits each Cobertura report to 20 MiB and the report count to 64; the
same 256 MiB aggregate bounds apply. See the [restricted coverage implementation and packaging notes](../../docs/evidence/evidencehost-migration.md#restricted-coverage-path-and-packaging).

## Internal restricted application protocol v2 prerequisite

[`EvidenceLinuxApplicationProtocol`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxApplicationProtocol.cs) adds a closed application descriptor and typed startup/readiness acknowledgements to the existing root-authenticated Linux supervisor. It is internal protocol preparation. The production Trusted proof resolver remains false, the production application catalogue remains empty, and public application factories remain rejected with `ASEVD407`. See the [closed catalogue prerequisite](../ForgeTrust.AppSurface.Evidence.Planner/README.md#internal-closed-application-catalogue-prerequisite) and [Aspire support status](../../docs/evidence/evidencehost-migration.md#support-status). A parsed descriptor or receipt supplies no proof or runtime admission by itself.

### Restricted producer callback binding

The shared lifecycle's internal `RequireJoinedCleanupPhase()` is a void guard for closing local application state from a registered disposer. It succeeds only after the supervisor has acknowledged physical owned exit, admitted callbacks/writes/pumps have joined, and `StopAndDisposeAsync` has entered disposal. It issues no admission or execution capability. A registered disposer cannot use `OwnWorkStopped` for this check because that final predicate also includes the disposer task itself. The [restricted Aspire host](../ForgeTrust.AppSurface.Evidence.Aspire/README.md#explicit-supervised-bootstrap) uses this phase guard without adding a stop timer; a premature local close remains failed on retry. Final collection continues to require completion of all disposer tasks and successful cleanup.

The [fixed coverage registration](../ForgeTrust.AppSurface.Evidence.Coverage/README.md#fixed-restricted-coverage-registration) receives runtime access only from a callback-scoped internal `EvidenceRestrictedProducerLease` attached to the existing `EvidenceArtifactWriter`. This is an implementation binding for the first-party CLI/Aspire hosts, not a new public admission context or consumer transport API.

The internal binder has this exact shape:

```csharp
EvidenceRestrictedProducerLease BindRestrictedProducerLease(
    EvidenceLinuxWorkerSupervisor worker,
    EvidenceAdmissionResult admission,
    EvidencePlan plan,
    byte[]? diffBytes,
    EvidenceRunByteQuota processOutputQuota,
    EvidenceWorkerExecution execution,
    CancellationToken stageToken);
```

Call it only inside the current tracked producer callback, using the actual connected worker, the same activated admission and lifecycle used to create that protected writer, the complete protected plan, counted diff bytes and the one run-wide received-output quota. The root's UID/GID must match the authenticated worker. A public/local writer cannot bind. `CancellationToken.None` cannot stand in for the real stage token. Dispose the returned lease in the callback's finally block; a writer cannot be rebound after closure.

The lease verifies the complete admitted plan, exactly one matching full producer declaration and the supervisor's run identity. It copies the plan/declaration/diff and checks copied diff bytes against the descriptor digest. It permits one attempt, including a failed metadata/execution attempt; callback closure and stale/latched admission reject permanently. `GetRestrictedProducerLease()` is an internal accessor used only by the shared adapter and returns fixed `ASEVD410` when no binding exists. Pure internal metadata and atomic-attempt helpers used by unit tests issue no lease, supervisor or admission.

`RunAsync` registers the entire fixed procedure through `EvidenceWorkerExecution.TrackOwnedWork` before returning its task. Its cancellation links the actual stage token with additional caller cancellation and adds no independent timer. Owned work stays registered until actual completion even when the caller ignores its task. Admission/arming/closure/quota checks are repeated around the procedure, so a late successful result cannot reopen eligibility. Artifact verification, cleanup and manifest completion retain their existing separate requirements. This binding does not enroll a consumer proof or enable Trusted execution.

### Authenticated outer worker descriptor

`EvidenceLinuxWorkerSupervisor.ConnectAsync` keeps actual Linux root `SO_PEERCRED` authentication and the current nonroot worker PID/UID/GID comparison. Its pure internal `ParseWorkerDescriptor(JsonElement)` parser accepts exactly `evidence-worker-linux-v1` or `evidence-worker-linux-v2` as metadata. V1 rejects any `application` field, including explicit JSON null. V2 requires the non-null application object described below. Parsing alone does not construct a supervisor, create admission or accept consumer proof.

The actual root-launcher grammar has these **38 required fields**, including `broker_pid` and `descriptor_path`:

```text
schema run_id worker_pid broker_pid worker_uid worker_gid subject_uid subject_gid
unit cgroup job_deadline_utc tool_root subject_root output_parent output_slot
dotnet_path test_output_root policy_file mode socket_path descriptor_path entry_sha256
base_revision subject_revision workflow_identity provider platform proof_digest
policy_sha256 output_parent_identity observation_profile_ids observation_producer_ids
paths admission_seconds start_seconds collection_seconds cleanup_seconds stopping_seconds
```

V2 adds required `application` as field 39. The only optional fields are `diff_file`, `diff_sha256` and `solution`; the current [root launcher](https://github.com/forge-trust/AppSurface/blob/main/scripts/evidencehost-linux-launcher.py) always emits these three, with nullable values when absent. Unknown, duplicate or case-aliased fields reject at the descriptor, identity and ready-wrapper levels. Required fields must be non-null with exact JSON types. The sole permitted empty string is `proof_digest`; a nonempty value must be lower-case SHA-256 and remains request data, not proof authority.

Worker/broker PIDs are positive Int32 and distinct. Worker and producer (`subject_uid`/`subject_gid`) identities are positive UInt32 with separate UIDs and primary GIDs. `output_parent_identity` has exactly `device_major`, `device_minor`, `inode`, `uid`, `gid`; the inode is positive UInt64, all other components UInt32, and its owner/group must equal the worker map. These are comparison facts, not retained filesystem handles.

`run_id` is bounded to 256 characters with exactly two nonempty ASCII name components separated by `/`; the parser preserves synthetic fixture run names and actual numeric CI run/attempt IDs. `unit` is a bounded ASCII `*.service` name and `cgroup` must equal `/system.slice/<unit>`. Root and file paths are normalized absolute Unix paths bounded to 4095 UTF-8 bytes; the socket limit is 100 bytes. Control paths are exactly `<control-root>/worker-control.json` and `<control-root>/broker/control.sock`. The control root is disjoint from tooling, subject and output roots, which are themselves pairwise disjoint. Policy and non-null diff are contained beneath tooling; non-null solution is contained beneath subject. The canonical global `dotnet_path` may be outside tooling. No filesystem ownership or mount proof is inferred from these textual paths.

`mode` is exactly `observation` or `trusted`; provider/platform remain `github-actions`/`linux-x64`. Revisions and workflow identity are nonblank control-free text bounded to 256 characters. Entry/policy/diff digests are lower-case SHA-256. A non-null diff requires its digest, and a digest without its diff rejects. Observation profile and producer ID arrays contain at most 32 unique ASCII names of at most 96 characters. `paths` contains at most 4096 unique normalized relative paths, each at most 4095 UTF-8 bytes. Lists permit no nulls, traversal, empty segments or backslash aliases and are copied read-only.

The five positive stage caps retain the existing ceilings: admission 30 seconds, startup 120, collection 60, cleanup 600 and stopping 30; stopping cannot exceed cleanup. The ready success wrapper contains exactly `ok: true`, `descriptor` and numeric `job_remaining_seconds`. `ParseWorkerRemainingAllowance(JsonElement)` requires a finite positive allowance at most 3600 seconds and rejects values that round to a zero-duration allowance. Connect retains the monotonic timestamp from before the handshake; parsing never renews the job budget.

`ValidateWorkerRuntimeBinding(descriptor, socketPath, authenticatedBrokerPid, currentPid, currentUid, currentGid, utcNow)` is an internal pure comparison that returns no capability. Connect supplies these observations from its actual socket and process: broker PID must match the authenticated root peer, worker PID/UID/GID and socket must match the current execution, and the descriptor wall deadline must remain future. Malformed descriptor/allowance data produces fixed `ASEVD402` diagnostics without raw values or an inner exception. Linux/platform and existing missing-channel behavior remain separate checks. Root v2 emission and real application handlers remain pending; accepting typed v2 metadata does not enable Trusted admission or public factories.

### Closed descriptor schema

`ParseApplicationDescriptor(JsonElement, workerUid, workerGid, producerUid, producerGid)` receives only the `application` object from the authenticated worker descriptor. The outer worker handshake supplies provider/platform/protocol and actual worker/producer identities. Those outer checks, root byte inspection and compile-owned catalogue matching are independent requirements. The application object has exactly these 14 required, case-sensitive snake-case fields; no field has a default:

| Fields | Meaning and bounds |
| --- | --- |
| `application_id`, `application_version`, `build_id` | Nonblank ASCII identifiers of at most 128 characters, using letters, digits, `.`, `_` and `-`. Immutable compiled identity, never a runtime registry selector supplied by a subject. |
| `catalogue_digest`, `entry_digest` | Exactly 64 lower-case hexadecimal characters. Structural data must match protected compile-owned expectations; a supplied digest does not authenticate itself. |
| `aspire_sdk_version` | Exactly `13.4.4`. |
| `resources`, `producers` | Nonempty complete declaration arrays, at most 16 resources and 32 producers. IDs are unique; dependencies name declared resources and resource self-dependencies reject. |
| `bundle_files` | Complete immutable inventory, 6–256 files, at most 128 MiB per file and 512 MiB aggregate. |
| `capabilities` | Exact finite application grants below. |
| `application_uid`, `application_gid`, `results_gid`, `resource_access_gid` | Unsigned 32-bit kernel identity values. Worker, producer and application UIDs are positive and pairwise distinct. Worker, producer, application, results and resource-access GIDs are positive and pairwise distinct. Actual supplementary groups and filesystem access remain root-enforced requirements. |

Every object at every depth rejects missing, unknown, duplicated or case-aliased fields, null required values and wrong JSON types. Application metadata is bounded to 1 MiB of UTF-8 JSON. Arrays are copied into read-only collections; records retain no `JsonDocument` lifetime dependency. Descriptor errors produce fixed `ASEVD402` text without echoing supplied values.

Resource objects require `id`, `readiness` (`aspire_health` or `completion`), `deadline_seconds` (1–120) and `requires` (at most 16 unique resource IDs). Producer objects require `id`, `kind`, `version`, `required_resources` (at most 16 unique IDs), `assertion_ids` (at most 128 unique nonblank strings, each at most 128 characters), `artifact_slots` (at most 128 unique logical names), `timeout_seconds` (1–600) and `coverage_gate`. A declaration's kind/version is data; only the protected catalogue may authorize an implementation.

Artifact slots require `logical_name`, `relative_root`, `media_type`, `required` (JSON Boolean) and `maximum_bytes` (0–256 MiB). A non-null coverage gate requires all six fields: `min_line_percent`, `min_branch_percent`, nullable `min_patch_line_percent`, nullable `min_patch_branch_percent`, `patch_line_mode` (`measurable` or `codecov`) and `tolerance_percent`. Percentages are JSON numbers from 0 through 100. `coverage_gate` itself may be explicit JSON null; omitting it is invalid. No declaration field is silently defaulted.

Each bundle file requires `relative_path`, `role`, `length_bytes`, `sha256` and `mode`. Relative names use ASCII letters/digits, `.`, `_`, `-` and `/`, with no absolute path, empty segment, `.` or `..` segment; maximum length is 256 characters. Hashes are lower-case SHA-256. `mode` is numeric Unix permission bits: decimal `292` (`0444`) or `365` (`0555`). DCP and extensions require `0555`; every other role requires `0444`. All paths are validated before collision checks. Duplicate names, case aliases and file/directory prefix collisions reject.

The nine exact role strings are `apphost`, `apphost_runtime_configuration`, `resource`, `resource_runtime_configuration`, `dcp`, `dcp_extension`, `dependency`, `declared_input` and `dependency_manifest`. AppHost/resource roles end in `.dll`; runtime configurations end in `.runtimeconfig.json`; DCP is exactly `dcp/dcp`; extensions are beneath `dcp/ext/`; dependency manifests end in `.deps.json` and remain read-only. AppHost, its runtime configuration, resource, its runtime configuration, DCP and declared input each occur exactly once. Dependencies, extensions and dependency manifests may have multiple entries. The root/Planner mapper must preserve every role's suffix and mode checks, including the ninth dependency-manifest role.

Capabilities require `read_only_inputs` (exactly one normalized declared-input bundle name), `scratch_bytes` and `memory_bytes` (each 1–1 GiB), `maximum_tasks` (1–128 process/thread tasks), `maximum_output_bytes` (1–1 MiB), `start_seconds` (1–120) and `stopping_seconds` (1–30). The closed shape supplies no secrets, external-network, privileged-group or protected/result-root projection grant. It contains no bundle host path, command arguments, environment map, delegate or factory.

### Requests, acknowledgements and lifecycle

`StartApplicationAsync(appId, entryDigest, token)` checks local admission closure and monotonic arming before I/O, requires the exact descriptor ID/digest, and permits one startup attempt per supervisor. A failed or cancelled attempt cannot be retried in that supervisor. It sends only:

```json
{"op":"application-start","application_id":"native-http-app","entry_digest":"<64 lower-case hexadecimal characters>"}
```

Its root response must contain exactly `ok: true`, `lease_id` (32 lower-case hexadecimal characters), `apphost_pid` (1–2147483647), exact `application_uid` and `application_gid`, exact `cgroup` `/system.slice/issue779-app-<lease_id>.service`, and `owned: true`. The immutable `EvidenceLinuxApplicationStartReceipt` reports owned-process metadata and makes no readiness claim.

`WaitForApplicationResourceAsync(leaseId, resourceId, token)` requires local admission/arming, the previously validated startup lease and an exact descriptor resource ID before I/O. It sends only:

```json
{"op":"resource-wait","lease_id":"<32 lower-case hexadecimal characters>","resource_id":"http"}
```

Its root response must contain exactly `ok: true`, the exact `lease_id`, `resource_id`, `application_uid` and `cgroup`, plus `kernel_peer_checked: true`, `http_status: 200`, `healthy: true` and `received_bytes` from 0 through 4096. Boolean text, AppHost output or a healthy value without the independent peer check cannot establish readiness. `ParseApplicationStartReceipt` and `ParseApplicationResourceReceipt` are intentionally internal pure validators for these acknowledgements; validation failure produces fixed `ASEVD410` text. No supplied value or subject exception is echoed.

Application requests preserve an authenticated-channel `ASEVD402` failure, including replacement of the pinned root broker, before classifying general wire errors. `NormalizeApplicationRequestFailure` is an internal data-only helper: it preserves existing admission exceptions, maps broker output rejection (`ASEVD420`) to fixed `ASEVD410`, and gives other malformed wire failures fixed `ASEVD410` without retaining supplied values or an inner exception. The ordering matters because `EvidenceAdmissionException` derives from `InvalidOperationException`; a general catch must not erase the channel identity diagnostic. This helper creates no supervisor or admission capability.

Both operations reuse the existing `RequestAsync` credential checks against the pinned root broker PID. Cancellation remains cancellation; a broker denial, malformed acknowledgement or transport failure cannot upgrade eligibility. Existing stop/wait requests use fresh connections. The root implementation still must extend owned stop/join to the application, all descendants and both output pumps before collection, and independent resource observation must inspect actual kernel peer identity and bounded HTTP bytes. Typed metadata does not perform those root operations. Root v2 emission/handlers and Aspire adapter integration remain pending; pure parser controls and macOS compilation are not Linux/systemd or Trusted-positive acceptance.

## Pitfalls

- Do not construct a `NoEvidenceRequired` result merely because a local run omitted tests. It is a policy outcome, not a convenience override.
- Do not treat a coverage collection artifact as a gate pass unless its producer has closed the assertion declared by the selected policy.
- Do not claim independent attestation in v1. An accepted envelope is represented as `ValidatedNotAttested`.
- Do not treat `EvidenceManifestBuilder.Verify(...)` or `appsurface evidence verify` as provenance checks. They detect inconsistent or edited fields by recomputing structural digests only; gates must obtain plan, manifest, and expected facts through a protected CI channel.
- Do not finalize while a child process, output pump, callback, or writer may still be active. An unconfirmed stop is a fatal path: no final manifest or summary is written, and recovery starts only in a fresh protected allocation after the supervisor confirms owned exit.

Read next: the [planner README](../ForgeTrust.AppSurface.Evidence.Planner/README.md), [Aspire lifecycle README](../ForgeTrust.AppSurface.Evidence.Aspire/README.md), [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md), and [migration/support guide](../../docs/evidence/evidencehost-migration.md).
