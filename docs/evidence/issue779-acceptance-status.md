# Issue #779 acceptance map — 2026-10-02

This is a bounded test and fixture audit against all 57 groups in the [approved plan](../plans/issue-779-evidencehost-test-plan.md). It maps current test methods and fixture cases; it is not a production-code review or acceptance decision. Test links point to files in the live, shared worktree, which has concurrent uncommitted changes. **Presence is not a pass:** only the explicitly recorded focused results below establish execution of their selected cases; no complete acceptance decision is made.

Receipt paths under `TestResults` are local-only ignored execution records. They are not included in a published
checkout, and their recorded results do not verify later source edits. Public CI observations link to their
recorded run through the consumer acceptance record.

## Execution boundary

- The supplied `integration-pass2` ledger reports CLI 289 passed, repaired Aspire 41 passed, five isolated lifecycle cases, and 16 control-protocol cases from the predecessor. These are historical run results, not verification of later/current edits. The stored `resume-status.json` is an earlier checkpoint (CLI 289; Aspire 35 passed/4 failed; lifecycle 5; control 16) and predates the reported Aspire repair.
- The five lifecycle cases use an isolated POSIX process-group fixture; the 16 control cases exercise protocol mechanics and explicitly exclude systemd/cgroup/admission. Neither is an actual systemd run.
- A separate actual GitHub Ubuntu 24.04 mechanism run used systemd 255/cgroup v2 and recorded nine lifecycle, twelve subject-boundary, and fourteen allocation cases. Its artifact says `admission: none`; it is mechanism evidence on its recorded older source/checkout, not proof of the current dirty implementation, CLI/Aspire admission, or protected downstream acceptance. See [the observed run record](issue779-consumer-acceptance.md#actual-ubuntu-mechanism-observation-2026-10-02). Local-only captured metadata: `TestResults/issue779-recovery-20261002/actual-ubuntu-850/mechanism-verification.json`.
- Production Trusted provider/platform allowlists remain intentionally empty pending the required acceptance proofs.

### Earlier focused verification

The results in this subsection bind earlier captured revisions. The measured-gap
recovery below records the newer focused checks; neither set is an acceptance
group total or verification of subsequent source changes.

The [execution record](../plans/issue-779-execution.md#single-use-admission-recovery-and-linux-sdk-diagnosis-2026-10-02)
records **81/81** current native macOS Aspire Evidence tests, exit **0**, with no
failures, skips or warnings, including the new protected broker fixture class.
It includes single-use rejection after authentication/admission failure, cancellation
before ownership, concurrent Run/Dispose with a held producer, and disposal before
execution. The current native macOS CLI Evidence filter passed **328/328**, exit
**0**, with no failures, skips or warnings, and compiles the new direct broker cases, including the mode-conflict cleanup regression.
The parent recovered its completed process exit from the runtime command record.
These selections overlap prior
runs and are not acceptance-group totals.

The portable Python launcher controls pass **39/39**, and the runtime proof-driver
controls pass **2/2**, exit **0**. A [new root-broker fixture](../../tests/evidencehost-consumer/ExecutionBroker-README.md)
and paired [CLI](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedCliExecutionTests.cs)/[Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceProtectedAspireExecutionTests.cs)
cases exercise actual consumer entries with synthetic subject results and cgroup metadata.
Their Linux production paths remain unverified. The x64 SDK and direct VSTest attempts
both failed before test results under QEMU on the ARM64 Docker engine. Native Linux
x64 validation is now available through the confirmed AppSurface GitHub Actions
owner. Its first run stopped at a launcher fixture path error before C# or coverage;
the fixture is repaired and a revised run is required. Native macOS controls do
not replace the Linux execution paths.
The unchanged solution coverage gate remains unverified on current source.

The current CLI package `0.1.0-issue779-current.20261002.1` built and installed from
an isolated local feed, both exit **0**. Seven installed-tool checks passed: three
help commands, sample generation, doctor with synthetic `GITHUB_ACTIONS=true`, and
two rejected run modes. Doctor left the trusted envelope unverified; omitted mode
and missing authenticated control rejected with `ASEVD401` and `ASEVD402`, empty
stdout and no output files. These are macOS package checks, not packed SDK or
supported Linux execution acceptance. The local-only completion receipt
`TestResults/issue779-recovery-20261002/current-packed-cli/completion.json` binds the package and source hashes
for that recorded run; it does not verify later source edits.

Statuses below describe test presence and scope, not pass state. “Unit/fixture” means a local test seam or test-owned process; it does not imply a supported runner, protected credentials, or real systemd enforcement.

### Measured-gap recovery and current native boundary

The full 2725-file candidate reached the unchanged solution gate in
[run 37037592365](https://github.com/forge-trust/AppSurface/actions/runs/37037592365):
15269 tests passed, zero failed and two existing tests were skipped. The gate
failed with `ASCOV020` because patch coverage was 82.852% lines and 76.6374%
branches. Aggregate coverage was 94.88% lines and 88.522% branches, within the
existing tolerance. This is failed gate evidence for that frozen candidate.

The next test-only recovery batch uses public or intentionally exposed APIs and
deterministic lifecycle controls. Scoped macOS source builds passed:

- Worker execution: **73/73**, including late completion, stop/join, collector
  failure and cleanup ownership controls.
- Run budgets: **36/36**, with exact-fit, overflow and reserve controls. The first
  source build stopped on a sibling producer test's decimal-threshold compile
  error; the corrected source and successful rerun are recorded separately.
- Restricted producer and Linux artifact root selection: **57/57**. Producer
  declaration, report metadata and optional capacity controls executed. Linux
  artifact syscall cases used their unsupported-platform checks on macOS; the
  actual Linux filesystem behavior remains required.
- Host bootstrap: **42/42**, including registration drift before callbacks,
  partial configuration cleanup, malformed results, completion failure and
  concurrent single-use controls.

Each final command exited zero with no failures, skips, warnings or errors.
These selections are test counts, not acceptance-group totals or a recalculated
patch gate. Ignored source/hash receipts are under
`TestResults/issue779-recovery-20261002/native-validation/coverage-v5b-*-fix/`.

The subsequent complete snapshot failed both
[systemd Observation](https://github.com/forge-trust/AppSurface/actions/runs/37039914626)
and all five [Aspire mechanism controls](https://github.com/forge-trust/AppSurface/actions/runs/37039916803).
The corrected [child fixture](../../tests/evidencehost-consumer/AspireChild/README.md)
now builds without warnings and passes forty portable controls, including bounded
identity diagnostics. Its DCP execution still requires a fresh native run. The
[consumer acceptance record](issue779-consumer-acceptance.md#full-source-gate-and-next-startup-failures-2026-10-02)
records the exact failures and limits. No new Trusted entry or acceptance group is
closed by this recovery batch.

## Group map

### Latest bounded verification (2026-10-03)

The [latest consumer record](issue779-consumer-acceptance.md#restricted-aspire-mechanism-and-production-failure-diagnostics-2026-10-03)
records **5/5 native restricted Aspire mechanism controls passed** on their exact
2736-file candidate. It also records the separate production Observation failure
with `ASEVD211` and incomplete execution; the producer cause remains unknown.
Scoped macOS source tests passed **209 catalogue and 129 application-protocol
cases**, including the selected 128-task bound and rejected 129-task neighbor.
The new root application module and integrated CLI/Aspire shared path still
require native consumer execution. These results do not close all 57 groups or
enable a Trusted provider/platform entry. The complete current-source solution
and patch coverage gate remains required.

| Group | Current test / fixture anchors | Current verification scope | Remaining gap |
| --- | --- | --- | --- |
| A01 | [CLI mode](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceCommandModeTests.cs): `MissingUnknownOrConflictingModeRejectsWithoutEcho`, `ExplicitModeOrLegacyTrueHasOnlyItsDeclaredMeaning`; [CLI entry](../../Cli/ForgeTrust.AppSurface.Cli.Tests/ProgramEntryPointTests.cs): `EntryPoint_EvidenceRunModeBindingRejectsImplicitAndConflictingModesBeforeSideEffects`; [Aspire caller](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAdmissionCallerTests.cs): legacy run cases | Partial: CLI and Aspire rejection fixtures exist; current tree unverified. | No paired caller matrix proving every rejected mode precedes all CLI and Aspire callbacks. |
| A02 | [Caller ownership](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAdmissionCallerTests.cs): held-producer concurrent Run/Dispose, disposal-before-run, and failed-admission retry; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): shared disposal | Unit cases passed in the 76-case focused run. The host consumes one attempt before authentication/admission; the broker rejects ready after stop/wait/exit in handler controls. | The paired production same-host/control retry case remains unverified on native Linux; real supervisor ownership remains a separate proof. |
| A03 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): missing/mismatched supervision; [paired broker callers](../../tests/evidencehost-consumer/ExecutionBroker-README.md): actual root peer, PID/UID/GID and declared policy | In-memory controls and portable exact-peer checks pass in their focused suites. Direct broker caller cases are present, not yet verified on Linux. | Actual launcher-to-production-caller proof with independently armed systemd supervision remains required; the root-broker fixture deliberately uses a synthetic cgroup. |
| A04 | [Worker unit](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `ExecuteAsync_SynchronousPreTaskStallDoesNotBlockDeadlineControl`; [isolated fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): `test_synchronous_stall_before_task_return_uses_failfast` | Partial: isolated subprocess/watchdog behavior is covered; five older fixture cases were reported passing. | Exercise configure, verifier, and factory stalls through the current launcher with an independent outer watchdog. |
| A05 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): `CoherentHeadSubstitutionOrHashDriftCannotAdmit`; [planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): conservative resolution and rename cases | Present: unit fixtures cover head substitution/hash drift and conservative planner behavior. | Two-checkout protected-base and same-diff admission remains external; current tests unverified. |
| A06 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): `CliWorkflow_ShouldRejectUnresolvableSnapshotsAndMismatchedPlanBindings`, `EvidenceDiffSnapshot_ShouldDefensivelyCopyBytesAndRejectInvalidDigests` | Present: snapshot, binding, and changed-input unit cases. | No current protected caller run proving exact source snapshot is the one admitted and executed. |
| A07 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `Registration_ShouldRequireOneDistinctEntryForEachExplicitCapability`, `RunSharedCore_RejectsMismatchedCompleteRegistrationBeforeReadinessOrProducer`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): exact assertion case | Partial: distinct/mismatched registrations and exact assertion fixture exist. | Full missing/extra/duplicate/stale replacement matrix is not evidenced as a current executed suite. |
| A08 | [Protected inputs](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedWorkerInputsTests.cs): `AcceptedConsumerProof_RemainsFalseForValidlyShapedDescriptor`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): `TrustedRequiresAcceptanceAndExactlyMatchingRegisteredAssertion` | Partial: local descriptor and acceptance matching fixtures; current proof deliberately remains false. | No protected consumer CI proof binding run, workflow, base/head, roots, and capability digest to a trusted descriptor. |
| A09 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): Observation/verifier and producer-class cases; [Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): dependency-free success and resource-backed rejection | Partial: Observation verifier bypass and resource restriction have unit coverage. | Complete projection, privilege, release, and undeclared-subset matrix across both public entries. |
| A10 | [Runtime subject fixture](../../tests/evidencehost-consumer/RuntimeSubject/Program.cs): `Subject_reads_its_declared_input_and_cannot_read_or_modify_protected_state`; [consumer record](issue779-consumer-acceptance.md) | Partial: older systemd mechanism run recorded 12 subject-boundary assertions; no current protected consumer execution. | Actual protected-secret map and disposable subject credentials must be demonstrated in the consumer workflow. |
| B01 | [Linux root unit](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceLinuxArtifactRootTests.cs): retained-handle, collision, parent-substitution, link, and hardlink cases; [Linux fixture](../../tests/evidencehost-consumer/test_linux_launcher.py): allocation cases | Partial: unit fixtures plus 14 allocation cases in the earlier actual Ubuntu mechanism artifact. | Re-run against current allocation/writer path and admitted parent on each supported platform. |
| B02 | [Aspire ownership](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAspireApplicationOwnershipTests.cs): `StartOwned_ClosesAfterBuildWithoutLosingOwnership`, `StartOwned_FailedBuildDoesNotRegisterOrStart`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): activation completion | Partial: build/cancel and activation unit fixtures are present, unverified on current tree. | Explicit both-order allocation-versus-cancel race with no resource or subject activation after rejection. |
| B03 | [Aspire ownership](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAspireApplicationOwnershipTests.cs): `StartOwned_RegistersBeforeStartingAndRetainsLeaseUntilCleanup`, `StartupFailure_JoinsWorkBeforeDisposingPartiallyStartedApplication` | Partial: startup ownership fixtures exist; repaired Aspire 41-pass result is historical. | Re-run latest host changes and prove admission/binding precede real factory/build/start in consumer execution. |
| B04 | [Runtime subject fixture](../../tests/evidencehost-consumer/RuntimeSubject/Program.cs): denied tooling/output/control access and declared-input read | Partial: subject-boundary fixture and older actual Ubuntu mechanism assertions; not current production worker/subject integration. | Prove current restricted subject cannot load trusted code or alter final output under the actual protected consumer job. |
| C01 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `RunAsync_ShouldProtectResourceOrderingAndRequiredArtifactIntegrity`, `RunAsync_ShouldProtectLifecycleBoundsSharedDependenciesAndCallerCancelledProducers` | Present: ordering, shared dependency, cycle/bounds, and artifact cases are represented in unit fixtures. | Current exact suite execution remains unverified. |
| C02 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): resource deadline, unavailable-resource, caller-cancel, and producer-failure cases | Present: readiness success/failure/cancel/deadline paths are represented in unit fixtures. | Confirm every readiness failure closes producer launch in the latest joined lifecycle. |
| C03 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): explicit producer registration, failed producer, and invalid output cases; [manifest](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): missing required artifact | Partial: producer outcomes/artifact obligations are tested locally. | Direct spoofed subject pass/known assertion versus registered producer outcome through the restricted producer path. |
| C04 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): claim tampering and missing required artifact; [gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): failed producer/open obligation | Partial: invalid claim and obligation cases exist. | Full stale, extra, duplicate, and mapping-mismatch result matrix has no current run evidence. |
| C05 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): `ArtifactWriter_ShouldRejectUndeclaredDuplicateAndOversizedWrites`, `ArtifactWriter_ShouldReleaseItsReservationWhenWritingFails`; [quotas](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs) | Partial: slot/media/size and reservation unit cases; current aggregate integration unverified. | Cross-producer aggregate limit and required-slot closeout through collection. |
| C06 | [Linux root](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceLinuxArtifactRootTests.cs): name/size, namespace drift, symlink, hardlink, and replacement cases; [launcher fixture](../../tests/evidencehost-consumer/test_linux_launcher.py): path rejection cases | Partial: Linux unit/fixture cases and older 14-case systemd-run artifact. | Windows/macOS reparse/path semantics are absent; current Linux code has not been proved by a new systemd run. |
| C07 | [Writer](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): reservation release; [budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): concurrent reservation; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): joined write | Partial: reservation and join fixtures are separate. | Concurrent retained writers racing finalization, including failed-write release, need one lifecycle assertion. |
| C08 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `RunAsync_ShouldInvalidateAProducerWhenItsWrittenArtifactChangesBeforeCollection`; [Linux root](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceLinuxArtifactRootTests.cs): retained descriptor verification | Partial: mutation and retained-handle unit cases exist. | Prove bounded final hashing uses the originally opened handle through current writer/collector integration. |
| G01 | [Gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): targeted, no-evidence, and release cases; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): accepted Trusted assertion | Partial: claim shapes and closed-obligation rules have unit coverage. | Production Trusted remains disabled; no accepted current protected run or packed consumer proof. |
| G02 | [Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): dependency-free Observation and resource rejection; [gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): Observation rejection | Present: informational Observation and ineligible-gate controls are represented locally. | Current exact tree unverified; no consumer fork Observation execution. |
| G03 | [Gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): `Verify_IsStructuralAndDoesNotMakeAForgedAssertionGateEligible`; [JSON](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): canonical bytes | Partial: digest/structural verification limits are tested locally. | No protected downstream proof that Verify cannot confer runtime or uploaded-pair authority. |
| G04 | [Protected gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): expected-fact, run, receipt, and claim rejection cases | Partial: unit gate fixtures cover mismatches; no actual protected gate workflow run. | Exercise current-run/base identities, stale receipt, missing manifest, and failed proof in consumer-owned CI. |
| G05 | [JSON input](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): byte limits, duplicate/case-colliding keys, versions/enums, additive fields; [protected input](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedWorkerInputsTests.cs): bounded policy/diff | Partial: generic bounded/schema unit fixtures and protected policy/diff fixtures exist. | Verify assertion JSON and both actual entry callers reject before parsing/allocation; latest additions unverified. |
| G06 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): `CliWorkflow_Summaries_ShouldExplainTargetedObservationAndReleaseClaims`; [gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): release opt-in | Present: release opt-in and summary claims have unit coverage. | No current consumer output/docs run confirming registered ID/version/scope without correctness overclaim. |
| U01 | [CLI workflow](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): starter creation, overwrite, malformed inputs; [doctor](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceDoctorTrustTests.cs): protected facts remain unverified; local-only installed tool receipt: `TestResults/issue779-recovery-20261002/current-packed-cli/completion.json` | Current macOS installed CLI generated a starter and rejected implicit mode/missing control before output. Synthetic `GITHUB_ACTIONS=true` left trust unverified. | Supported Linux live execution and fresh-user usability remain required. |
| U02 | Local-only exact-version CLI receipt: `TestResults/issue779-recovery-20261002/current-packed-cli/completion.json`; local-only packed SDK fixture snapshot: `TestResults/issue779-recovery-20261002/packed-sdk/consumer/Program.cs.fixture` | Current CLI package install/help/starter and negative mode/control checks passed on macOS; the SDK snapshot is not a verification result. | Packed SDK proof and supported Linux execution against the exact current candidate remain required. |
| U03 | [Paired broker callers](../../tests/evidencehost-consumer/ExecutionBroker-README.md); [CLI mode](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceCommandModeTests.cs); [Aspire caller](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAdmissionCallerTests.cs); [shared admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs) | Paired production-entry Observation/rejection cases are present; current native macOS controls do not execute their Linux paths. | Run paired cases on native Linux, then prove equivalent admission/claim behavior under the actual protected consumer supervisor. |
| U04 | [Doctor](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceDoctorTrustTests.cs): environment-value secrecy; [coverage](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageEvidenceProducerTests.cs): nonfatal failure secrecy; [Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): stable diagnostics | Partial: selected canary/diagnostic paths are covered locally. | End-to-end diagnostic code/stage/cause/fix/link/exit/manifest secrecy matrix remains unverified. |
| U05 | [Linux fixture](../../tests/evidencehost-consumer/test_linux_launcher.py): bounded wait/handler settlement; [lifecycle fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): owned-process stop cases | Missing: no same-root rapid retry/concurrent allocation recovery fixture identified. | Prove retry only after owned exit with a new root; reject concurrent reuse of the old root. |
| U06 | [CLI mode](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceCommandModeTests.cs): explicit/legacy meaning; [JSON](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): supported and rejected versions | Partial: local migration/schema fixtures exist. | Packed old bool/alias/enum-builder compatibility and no automatic Trusted need exact-version consumer proof. |
| U07 | No manual acceptance trace found. | Missing: no fresh-user planning and live-execution record. | Record the declared planning endpoint separately from actual supported execution. |
| U08 | [Starter workflow tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): starter and summary cases; no issue-779-specific snippet assertion identified | Missing: generic snippet infrastructure is not evidence that these docs match the built CLI. | Verify claim tables, snippets, canonical links, generated starter, and non-attestation wording against the packed version. |
| R01 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): conservative fallback, precedence, rename, unknown path, limits | Present: regression cases cover the plan's stable selection rules. | Re-run current planner changes; current run status is unverified. |
| R02 | [Coverage adapter](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageEvidenceProducerTests.cs): planning snapshot/numeric gate; [restricted producer](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRestrictedCoverageProducerTests.cs): subject result and numeric gate | Partial: unit coverage exists; the reported 11 coverage-runner tests are historical. | Packed-consumer proof that coverage gains no mode-specific alternate trust. |
| R03 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `DisposeAsync_ShouldDisposeRegisteredResourcesAndProducersOnce`, cleanup failure; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): shared disposal | Present: deduplication, repeat/shared disposal, and cleanup-failure fixtures exist. | Re-run the repaired/current suite; no cleanup success may be inferred from an older pass. |
| R04 | [JSON](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): canonical V1 bytes and additive fields; [shape](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonShapeTests.cs): nested round-trip | Present: canonical and compatible-shape unit fixtures exist. | Current golden output and supported-schema matrix have not been reverified. |
| R05 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): marked starter, unmarked overwrite refusal, malformed inputs | Present: requested starter/overwrite paths have local unit tests. | Current CLI suite unverified; no change needed to the retained test intent. |
| R06 | [Protected input fixture](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedWorkerInputsTests.cs): shaped descriptor stays unaccepted; [acceptance record](issue779-consumer-acceptance.md#versioned-support-allowlist) | Missing: no matching provider/platform/build/proof matrix enables Trusted. | Keep all production allowlists empty until immutable current acceptance entries exist; Windows/macOS proofs are absent. |
| L01 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): cooperative cancel and original cause; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): caller cancellation | Partial: cancel/deadline latch cases are present in unit fixtures. | Demonstrate first-cause preservation at every admission/start/readiness/producer/collection boundary in current lifecycle. |
| L02 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `ExecuteAsync_LatePassedResultDuringGraceCannotUpgradeTimeout`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): late pass latch | Present: deterministic late-pass/grace cases exist. | Current test additions remain unverified; include completion on both sides of the deadline. |
| L03 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): joined in-flight write and terminal registration closure; [output quota](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProcessOutputQuotaTests.cs): drain on overflow | Partial: selected callback/write/pump paths have unit or isolated process fixtures. | One current lifecycle test must show every callback, child, pump, and write settled before disposal. |
| L04 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): active-stage cancellation/cleanup; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): cooperative stop | Partial: cancellation reserve mechanics are represented. | Assert fresh stop-token usability and grace bounded by remaining cleanup/job time. |
| L05 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `StopAndDisposeAsync_ExposesJoinAndCleanupBeforeCollection`; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): reverse/deduplicated disposal and cleanup failure | Present: joined cleanup ordering and failure-manifest unit paths exist. | Current exact suite and real consumer collection remain unverified. |
| L06 | [Isolated fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): synchronous stall, ignored cancel, blocked write/pump, stuck disposer, cooperative timeout; [worker unit](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): fatal path | Partial: five older isolated POSIX-process cases passed; they are not a systemd runtime. Earlier systemd mechanism run is separate and admission-free. | Run the current worker/launcher under the actual systemd supervisor; fatal cases must have no dispose/hash/manifest markers. |
| L07 | [Worker unit](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `ExecuteAsync_ChildWithoutExitAcknowledgementUsesFatalPath`; [Linux mechanism record](issue779-consumer-acceptance.md#actual-ubuntu-mechanism-observation-2026-10-02) | Partial: unit fake models missing child acknowledgement; earlier actual systemd mechanism artifact exists but does not admit current production integration. | Link a current systemd case proving active/unacknowledged descendants keep output quarantined. |
| L08 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): stalled disposer blocks the next; [isolated fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): `test_nonsettling_disposer_uses_failfast`; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): cleanup fault | Present: unit and isolated process cases cover stuck/throwing disposal. | Current worker revision and systemd fatal termination remain unverified. |
| L09 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): critical/nonfatal producer failures; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): unjoined fatal path | Partial: unit exception/fatal handling is represented. | Prove fatal runtime exception through current entry produces no success manifest. |
| L10 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): canceled/unknown modes; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): single-use and caller cancel | Partial: separate cancellation and single-use cases exist. | One entry/lease matrix for cancel-before, during admission, and after terminal result is not evidenced. |
| P01 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): exact limit, overflow latch, concurrent reservations; [writer](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): oversized writes | Partial: quota boundary and concurrency are unit-tested. | Prove the 256 MiB run-wide reservation is shared by every producer through collector closeout. |
| P02 | [Output quota](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProcessOutputQuotaTests.cs): received-byte accounting, shared streams/commands, overflow drain; [coverage](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageEvidenceProducerTests.cs): buffered coverage output | Partial: 1 MiB prefix/16 MiB aggregate logic has unit fixtures; current run unverified. | Actual child output must count discarded/streamed bytes on both paths under the current launcher. |
| P03 | [Output quota](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProcessOutputQuotaTests.cs): `ProcessRunner_OverflowCancelsAndDrainsChildBeforeReturning`; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): quota failure closes registration | Partial: overflow and stop/join are tested separately. | End-to-end overflow must latch failure, join, dispose, and prevent an eligible truncated result. |
| P04 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): exact-fit, one-tick-short, invalid settings, and cleanup reserve | Present: deterministic budget unit cases exist. | Current caller must reject the summed job budget before invoking any callback. |
| P05 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): monotonic elapsed time, later-stage recheck, concurrent start, cancellation | Present: wall-clock shift and reservation unit cases exist. | Re-run latest lifecycle integration and prove no concurrent reservation extends the job. |

## Latest exact-source mechanism failures

Frozen snapshot `8a964188e85561d57119133d26b552f9412e2445` failed both
[systemd Observation](https://github.com/forge-trust/AppSurface/actions/runs/37047555055)
and [Aspire mechanism execution](https://github.com/forge-trust/AppSurface/actions/runs/37047560583).
All 2725 source hashes, dispatched workflow bindings and downloaded artifact
digests matched. Runtime readiness and wait completed, but output allocation or
activation reported `ASEVD409` without its underlying exception; no final
manifest or runtime proof was produced. Aspire passed native restore/build,
then failed all five controls: four resource startups failed during thread
creation, and the factory-stall identity guard rejected an observed root UID.
No healthy resource or valid readiness-failure control was established.

These results leave the native groups incomplete. The next diagnostics preserve
closed allocation facts, completed executable startup and bounded cgroup task
and memory counters. See the [exact observation and its limits](issue779-consumer-acceptance.md#frozen-source-allocation-and-resource-startup-failures-2026-10-02).
The [unchanged solution coverage run](https://github.com/forge-trust/AppSurface/actions/runs/37047553718)
also exited **1**, with `ASCOV120`: its fixture-path policy rejected dynamic
`Path.Join` in the Bootstrap artifact test. The solution recorded 15,393 passes,
one failure and two existing skips; 53 of 54 projects exited zero, with no
compiler warnings or errors. Measured aggregate coverage was 94.98% line and
88.79% branch, but the patch gate was not reached. The local correction uses
`TestPathUtils.PathUnder`; a fresh unchanged native gate remains required.
All 57 groups and the protected CLI/Aspire positive remain required.

## Five highest-value remaining gaps

1. **Protected consumer acceptance:** run the real AppSurface protected/fork workflow through CLI and Aspire admission, restricted subject, and current-run downstream gate (A10, B04, G04, U03). Existing unit/fixture cases do not prove protected secret mapping or current run/base binding.
2. **Current admission-to-ownership integration:** connect a real armed supervisor and protected descriptor to both callers, then cover cancellation/allocation races in both orders (A03, A08, B02). Present lease fixtures are not a production launcher proof.
3. **Current systemd lifecycle integration:** exercise the current worker under the actual systemd/cgroup supervisor for stuck callback/write/pump, child acknowledgement, stop/join, and no-finalization behavior (A04, L03, L06, L07, P03). The five passing POSIX process-group fixture cases and older nine-case Ubuntu mechanism record are distinct evidence.
4. **Trusted allowlist acceptance:** add immutable matching provider/platform/build/proof records before registering Trusted (R06). Linux mechanism evidence alone is insufficient; Windows and macOS remain unproved, and production allowlists are intentionally empty.
5. **Consumer usability proof:** run exact-version packed CLI/SDK migration checks and the fresh-user/manual plus docs/snippet/starter trace (U02, U06, U07, U08). No matching current acceptance result was found.
