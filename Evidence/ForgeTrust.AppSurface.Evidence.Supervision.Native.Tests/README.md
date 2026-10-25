# Native supervision measurement tests

This test project validates the source-bound Linux measurement that the full solution coverage run merges into the ordinary Coverlet report. It checks that the report contains executed lines in the real Linux root observation path, `LinuxEmptyObservationExecution.cs`, and that the attached native receipt records the seven physical C3 controls. It is a test and measurement surface; it does not add a production application registration, admit a proof, or grant trust.

For the supervision contract and its lifecycle boundaries, see the [supervision project reference](../ForgeTrust.AppSurface.Evidence.Supervision/README.md). The larger C3 fixture and its evidence limits are described in the [checkpoint 3 README](../../.github/issue779-checkpoint3/README.md), and the cutover and full-gate context is in the [Issue 779 C# runtime cutover reference](../../docs/evidence/issue779-csharp-runtime-cutover.md).

## Test surface and selection

`NativeMeasurementTests.LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls` reads the prepared inputs from `TestResults/issue779-native-measurement`. It verifies the source revision against the checked-out Git `HEAD`, checks the SHA-256 bindings, requires the recorded Coverlet version and changed hashes for all seven instrumented assemblies in each CLI/SDK image, validates the native receipt and its seven ordered cases, checks each case's settlement and final group state, and inspects the Coverlet JSON for nonzero root hits in `LinuxEmptyObservationExecution.cs` and authenticated worker connection hits. The first CLI control must bind genuine CLI, Contracts, Coverage and Evidence.Cli worker hit-file handoffs. The SDK control requires CLI, Contracts, Coverage and Aspire handoffs against its own image, and its separate report must contain actual host-bootstrap and restricted-application execution hits. Evidence.Cli remains in the SDK image and report inventories, with its actual handoff or allowed unused outcome checked as described below.

`LinuxMeasurementFactAttribute` selects this test only when the host is Linux **and** `EVIDENCEHOST_NATIVE_MEASUREMENT=1`. macOS, Windows, and ordinary Linux runs without the opt-in report an explicit skip. This keeps unrelated Linux test runs from failing because they do not carry the dedicated workflow's prepared evidence. In the dedicated lane, the workflow sets the flag only after it downloads and stages all application, account and producer inputs below and writes `native.runsettings`. Once selected, a missing input, wrong source or digest, incomplete case set, failed control, or absent native-root coverage hit fails the test. The flag controls test selection and the project runsettings condition only; it is not authority or evidence by itself.

Run the test as part of the complete [solution coverage gate](../../scripts/coverage-solution.sh) in the prepared runtime workflow. The project targets `net10.0` and uses the centrally pinned `coverlet.collector` 10.0.1 as a private test dependency. Its Linux-only `RunSettingsFilePath` is enabled by the same `EVIDENCEHOST_NATIVE_MEASUREMENT=1` property. A local invocation without prepared inputs is expected to skip; setting the flag manually selects the test and therefore requires the complete prepared input set and runsettings.

## Closed seven-module measurement shape

The full-image validators require exactly `ForgeTrust.AppSurface.Cli`,
`ForgeTrust.AppSurface.Evidence.Contracts`, `ForgeTrust.AppSurface.Evidence.Planner`,
`ForgeTrust.AppSurface.Evidence.Coverage`, `ForgeTrust.AppSurface.Evidence.Aspire`,
`ForgeTrust.AppSurface.Evidence.Supervision`, and `ForgeTrust.AppSurface.Evidence.Cli`.
Each image map has their fourteen named DLL/PDB leaves, with changed instrumented
DLL hashes and byte-identical original/restored hashes wherever restoration maps
are part of the contract. Report module basenames must match this same closed set;
an omitted seventh module or a second spelling of another basename cannot replace it.
These checks follow the [private measurement contract](../../.github/issue779-checkpoint4/measurement/README.md)
and [producer-root lineage reference](../../.github/issue779-checkpoint4/measurement/producer-roots-README.md).

The [worker entry's compiled branches](../../Cli/ForgeTrust.AppSurface.Cli/EvidenceWorkerCommand.cs)
select different consumers. The normal CLI calls the shared
[protected CLI execution path](../ForgeTrust.AppSurface.Evidence.Cli/EvidenceProtectedCliExecution.cs).
The private SDK branch calls the [Aspire host](../ForgeTrust.AppSurface.Evidence.Aspire/README.md)
directly and need not load or execute Evidence.Cli. Image inclusion alone does not
establish execution, and a passive filesystem-event count of zero does not prove absence.

| Check | Normal CLI | Normal SDK | Failed SDK |
| --- | --- | --- | --- |
| Seven module names and fourteen DLL/PDB image leaves | Required | Required | Required |
| Seven standard report module basenames | Required | Required | Required |
| Evidence.Cli row's instrumented image hash | Required | Required | Required |
| Evidence.Cli worker/host hit-file handoff | Required | Required only when reported | Required only when reported, also equal to cached bytes |
| Positive `EvidenceProtectedCliExecution.cs` document hits | Required | Not required | Not required |
| Original CLI, Contracts, Coverage and Aspire SDK handoffs | Existing CLI requirements apply | Required | Required |

SDK Evidence.Cli rows may retain the existing `worker-file-absent` or
`prior-root-file-unchanged` outcomes. Those outcomes provide no worker-execution
credit and do not remove the module from any image or report map. A reported
`unchanged-worker-handoff` must still match its instrumented image and worker/host
hashes; the failed-SDK v3 protocol additionally requires identical cached bytes.
The SDK host-bootstrap and restricted-application witnesses remain required.
The failed SDK also retains its exact source-bound failed-producer statement,
original status one, barrier/identity checks, and settlement requirements. A passing
data comparison cannot turn that failed execution into a successful one.

The [detached failed-SDK controls](SdkFailureMeasurementTests.cs) independently
reject unused outcomes and image mismatches for each of the four original required
SDK modules. They accept unused Evidence.Cli rows and missing or zero protected-CLI
document hits while retaining all seven image/report names. The seventh-module
negative cases still reject missing or duplicate rows, missing DLL/PDB leaves,
incorrect image or handoff hashes, and projected reports. Every mutation rebinds
outer serialization digests; these are data comparisons, not native execution.

Six-module packets remain historical evidence for their original source. They fail
this seven-module source contract even if their enclosing report and predecessor
hashes are consistently recomputed. The [SDK](SdkFailureMeasurementTests.cs),
[account](AccountMeasurementTests.cs), [account lineage](AccountMeasurementLineageTests.cs)
and [producer-root](ProducerRootsMeasurementTests.cs) regression fixtures construct
only detached data in memory. Their positive fixtures and rebound malformed packets
provide neither actual measurement inputs in `TestResults` nor native execution,
admission or gate credit. The [qualification image boundary controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/QualificationImageBoundaryTests.cs)
inspect the built helper's seven names only for the existing C3 compilation branch;
ordinary images continue to require the helper's absence.

## Prepared inputs and merge settings

### Reviewed producer-root witness mappings

The [producer-root validator](ProducerRootsMeasurement.cs) pins each witness to a
reviewed document, type, method, source hash and exact line span. The quota witness
uses the overflow statement at line 173 in
[SupervisionRunOutputBudget.cs](../ForgeTrust.AppSurface.Evidence.Supervision/SupervisionRunOutputBudget.cs),
whose reviewed SHA-256 is
`dff0c2e2fbf8f35ffc20aaf9327c154faad32d005f89dccafa7fd5368d4de81b`.
The retained-report mutation witness keeps its separate reviewed mapping.

The [mapping regression controls](ProducerRootsMeasurementTests.cs) compare both
pins and spans with the actual checkout before a native gate runs. Any source edit,
including an exception-filter refactor outside the witness method, requires an
explicit mapping review and pin refresh. A stale hash must fail; deriving an
expected hash from the supplied packet would remove this independent check.
Consistently rebinding packet digests cannot restore the historical quota pin.

These controls validate measurement data only. They do not create native execution,
process ownership, admission or accepted-proof authority. The
[producer-root lineage contract](../../.github/issue779-checkpoint4/measurement/producer-roots-README.md),
positive witness hits, original source capture, predecessor bytes and numerical
coverage thresholds remain independently required.

### Supplementary root marker and subtree controls

The [C3 workflow](../../.github/workflows/issue779-checkpoint3-application.yml) runs a
separate standard collector pass for `LinuxOwnerGuardNativeTests` (19 cases),
`LinuxCustodySupplementNativeTests` (9 cases), and `LinuxProducerFileTreeNativeTests`
(13 cases). These actual Linux x64 root tests
exercise atomic marker consumption, replay, cancellation and object mutation, and
retained nested report names, selected root pins, unknown reads and cancellation.
They establish file/marker behavior only; they create no application admission,
accepted consumer proof or production registration.

The supplementary [producer file-copy controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxProducerFileTreeNativeTests.cs) add 13 actual root cases against [the retained deployment copy API](../ForgeTrust.AppSurface.Evidence.Supervision/LinuxProducerFileTree.cs). They check exclusive destination rejection, audited-source changes, cancellation, expired deadlines and use after close, with real file descriptors and separate valid copies. Their numeric ownership is fixture metadata, not an account reservation, authenticated workspace or producer lease.

The supplemental `root-supplement.trx` and `root_supplement_tests` binding are
separate from the original 39-case `root-files.trx` consumed by the account lineage.
The binding requires exact SHA-256, 41 executed/passed rows with no skips or failures,
and exactly the three fixed classes. The same already verified C3 dependency image
is measured, its DLL/PDB bytes are rechecked after collector restoration, and its
genuine report is merged through Coverlet's standard `--merge-with` before the
coverage hash is updated. No counts are rewritten. The
[runtime workflow](../../.github/workflows/evidencehost-runtime-proof.yml) stages
both TRXs; the current-run measurement test requires both before the unchanged gate.
Ordinary local builds can compile these cases, but their explicit platform/root
selection prevents a macOS or nonroot run from claiming native execution.

The runtime workflow downloads the current application-lane artifact and copies exactly these nine files into `TestResults/issue779-native-measurement`:

| File | Meaning |
| --- | --- |
| `measurement-binding.json` | Schema, source revision, collector identity, original and instrumented DLL/PDB hashes for all seven selected modules, and hashes of the source/receipt/report inputs and the root filesystem TRX. |
| `native-supervision.json` | Current-run native Coverlet JSON emitted by the private Coverlet Console measurement. |
| `native-sdk.json` | Separate standard SDK-image report, hash-bound before the outer collector's standard report merge. |
| `native-sdk-failure.json` | Independently finalized standard report for the genuine failed-test SDK control. |
| `sdk-test-failure.json` | Finalized failed-SDK receipt, restoration and pre-exit worker-cache provenance. |
| `root-files.trx` | Hash-bound current-run passing root filesystem/kernel controls for all four expected classes; zero skips or failures. |
| `root-supplement.trx` | Separate hash-bound 41-case marker/subtree/copy supplement; no replacement of the original root TRX or account lineage. |
| `source.json` | Complete source capture bound to the tested Git revision. |
| `native-receipt.json` | Receipt for the seven native C3 controls, including source, per-case outcomes, and `trusted_eligibility: false`. |

The two account stages each supply `source.json`, `measurement-binding.json`, `native-accounts.json` and `native-accounts-receipt.json` in the fixed `accounts-creation` and `accounts-rollback` child directories. The producer stage supplies `source.json`, `producer-roots-binding.json`, `native-producer-roots.json`, `native-producer-roots-c2.json` and `native-receipt.json` in `producer-roots`. Every source capture must equal the complete original application capture byte-for-byte. These are mandatory inputs, not optional reports discovered by searching the filesystem.

After those copies succeed, the workflow validates the [current worker measurement chain](../../.github/issue779-checkpoint1/measurement-chain-README.md) and writes `native.runsettings` with the standard `XPlat Code Coverage` collector, `Format` set to `cobertura`, and `MergeWith` set to that chain's final, unchanged standard JSON report. The ordered chain is application → account creation → account rollback → inner C2 → outer packaged None → C1 None → N04 → N05 → N06 → N07 → N08 → N09 → N10 → N11 → N12 → N13 → N14 → N15 → N16. Each C1 stage uses a separate disposable Linux runner and consumes its completed predecessor exactly once through standard Coverlet `--merge-with`. The fourteen measured selectors contain all sixteen original native cases: None supplies N01/N02/N03, and every later selector supplies its named case. The separate all-sixteen C1 matrix and aggregate remain independently required. The original C3, account and producer reports remain separate for the consistency checks; the solution collector consumes only the final cumulative report, without separately merging its ancestors again. Coverlet's collector reads `MergeWith` as a supported setting. That behavior is present in the pinned Coverlet source at commit [`d21b5b6a08d48f51405ba2c5c5660f91a565776d`](https://github.com/coverlet-coverage/coverlet/tree/d21b5b6a08d48f51405ba2c5c5660f91a565776d), in [`CoverletSettingsParser.cs`](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/legacy/coverlet.collector/DataCollection/CoverletSettingsParser.cs) and [`CoverageWrapper.cs`](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/legacy/coverlet.collector/DataCollection/CoverageWrapper.cs). The collector remains the driver for the test run and merge; the project does not use `coverlet.msbuild` or edit report counters.

The test treats these files as measurements. It reads each with a size bound, verifies the recorded source and hashes, requires different original and instrumented assembly hashes, and checks that the source capture and native receipt identify the same `HEAD`. The coverage JSON must contain exactly the seven selected modules, an executed root path in `LinuxEmptyObservationExecution.cs`, and an executed worker `ConnectAsync` path. First-worker raw-hit hashes must match the unchanged host handoff and the instrumented module hashes. A JSON file's presence or a plausible percentage alone cannot pass these checks.

All prepared-input paths use the shared [TestPathUtils.PathUnder](../../tests/ForgeTrust.AppSurface.Testing/README.md)
helper, including dynamic folder and filename parameters. It validates lexical containment
under the selected repository or staging directory. It is a test path helper, not a
symlink-resolving filesystem sandbox or native ownership check; the independent source,
artifact and measurement checks remain mandatory.

## Instrumentation and native controls

In the application workflow, Coverlet Console 10.0.1 instruments exactly the private CLI, Contracts, Planner, Coverage, Aspire, Supervision and Evidence.Cli modules before the wrapper starts the original native runner. Every original/instrumented DLL and PDB hash is bound. The [private C3 measurement fixture](../../.github/issue779-checkpoint4/measurement/README.md) retains the actual worker's private temporary directory before descriptor publication, then copies its genuine first-normal-worker hit bytes unchanged after original normal exit. The root tracker and standard collector perform all counter accumulation and interpretation. This fixture is absent from ordinary images; no production environment, grant, deadline or supervision policy changes.

The runner requires root on Linux x86-64 with systemd 255 and executes seven independently bounded cases through the real C# root supervisor:

1. Normal application through the CLI tool.
2. Normal application through the SDK tool.
3. Resource readiness failure with an independently observed HTTP 503.
4. Factory stall before DCP starts.
5. Cancellation while the producer is running.
6. A stuck descendant whose live kernel identity and TERM-handler marker are checked before cancellation.
7. Disposal failure where a retained scratch holder causes the expected `EBUSY` unmount rejection and identities remain quarantined.

The receipt is produced only after the harness checks the original root owner's terminal outcome, actual unit and cgroup state, and the case-specific process observations. The test requires all seven cases in order, each marked passed, empty final groups, and a settled application observation with the two empty-group checks, output-pump joins, and pending-start join. The runner also checks kernel process identity and account custody; normal and applicable negative cases require account removal, while the intentional disposal-failure case requires accounts to remain present. The ordinary output-accounting receipt requires registered stream pairs, reconciled received-byte counts, EOF on both streams, and no read failure. The disposal-failure case is intentionally a failed root outcome with quarantined identities, not a successful evidence publication.

Coverage describes only code actually reached by the measured process. It proves that the source-bound Linux root path ran when the test finds the required hit in `LinuxEmptyObservationExecution.cs`; it does not presume hits from a force-killed process. Worker hits require the explicit source-pinned private-namespace handoff, not ordinary visibility across `PrivateTmp`. SDK hits now require its separate instrumented image, source binding and standard report described above; the initial uninstrumented SDK runs supply no presumed counters. It does not claim every native process is instrumented or covered.

## Relationship to the full gate and trust

The full gate remains the existing [`scripts/coverage-solution.sh`](../../scripts/coverage-solution.sh) command. It merges this native report through the standard collector and evaluates the same aggregate thresholds of 95% lines and 85% branches, plus the configured patch thresholds of 95% lines and 85% branches in Codecov patch-line mode. The gate's default 0.5 percentage-point tolerance applies. The [cutover workflow](../../.github/workflows/evidencehost-runtime-proof.yml) explicitly pins `COVERAGE_GATE_DIFF_BASE=1991280e8af049db592ebcb705cbf66f4b4c6e8b`, verifies its ancestry and records the comparison commit. The unchanged script still defaults to `origin/main` for other callers; a moving remote branch must not silently replace this task's fixed comparison. This sidecar does not report that the gate has passed; the aggregate and patch reports from an executed full gate determine that result.

The private fixture registrations are not production application-table entries. The production table remains empty, and the native receipt records `trusted_eligibility: false`. Passing measurement checks is neither production acceptance nor a Trusted proof. [Run 39146333953](https://github.com/forge-trust/AppSurface/actions/runs/39146333953) completed all seven instrumented Linux application controls on source `771d0538347998bdbcff0257fe5089c350ca68bc`. The [cutover record](../../docs/evidence/issue779-csharp-runtime-cutover.md) binds its artifact digest and complete source verification. Its separate numerical full-gate result was still pending when this observation was recorded; later changes require fresh measurements.

## Root filesystem measurement

The application workflow additionally builds the ordinary root filesystem control
classes with the same C3 project graph, compiler and Release configuration. Before
instrumentation it requires the test image's Supervision DLL/PDB hashes to match
the native application's original module hashes. The pinned Coverlet Console runs
only `LinuxProtectedNodeNativeTests`, `LinuxRetainedReportNativeTests`,
`LinuxKernelIdentityNativeTests`, and `LinuxDcpIdentityNativeTests` as actual
Linux x64 root on their own fresh `/run` trees. These tests exercise existing
mandatory `openat2`, retained inode/name checks, bounded reads and real file
mutation rejection; they create no supervisor, process lease or accepted proof.

The same pinned collector uses its supported `--merge-with` report setting to add
those actual hits to the source-bound application report. No raw worker hit file
is read or reconstructed. The workflow binds the real `root-files.trx` SHA-256 and
requires positive executed/passed counts with zero failed or skipped cases before
publishing the combined report. [`RootFilesystemMeasurement`](RootFilesystemMeasurement.cs)
checks that bound TRX again in the solution lane, including all four expected test
classes. Its [malformed-data controls](RootFilesystemMeasurementTests.cs) use
labelled synthetic TRX solely to test validation and provide no execution credit.
The standard solution command, coverage thresholds, exclusions and patch
comparison remain unchanged.

The kernel identity class owns a fresh generated test unit before starting a
fixed nonroot `/usr/bin/sleep` under the actual host manager. It samples the
original proc handles, complete UID/GID tuples, start time and exact cgroup,
checks cancellation and mismatch rejection, then explicitly stops/KILLs that
selected unit and joins its utility processes and bounded pumps. A 30-second
OS runtime cap is independent of the command deadlines. The tests issue no
AppSurface owner activation, worker admission, producer lease or accepted
consumer proof; they verify kernel primitives only. They explicitly skip
without Linux x64 root and a booted systemd manager; the prepared lane requires
a zero-skip passing TRX, so that skip supplies no measurement credit.

The [DCP identity primitive control](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxDcpIdentityNativeTests.cs)
uses the same bounded helper policy with an application unit name and a fresh root-owned, read-only copy
of `/usr/bin/sleep`. That is an executable-identity fixture, not an SDK DCP payload. The actual kernel
capture must select its PID, reject wrong UID/GID and a different audited file, preserve cancellation
without latching an integrity failure, and permanently reject named-file substitution even after the
original inode is restored. Disposed identities reject. The helper remains owned before start and its
original stop/group/pump joins finish before file deletion; failed teardown preserves the owned directory.
The existing worker helper recipe stays unchanged. This control requires a fresh Linux zero-skip result;
local compilation and an explicit macOS skip supply no native evidence. The seven C3 consumers remain
responsible for real Aspire SDK/DCP execution and application lifecycle acceptance.

## Current verification boundary

[Run 39158246121](https://github.com/forge-trust/AppSurface/actions/runs/39158246121) on source `995d2e1d8675764a4153e220d18b4a68b4f427d9` passed the seven producer controls, seven application controls, and all 28 real root filesystem/kernel controls with no skipped native cases. Complete 3,244-file source/Git-blob/hash/mode bindings and artifact digests were verified. That run predates the six-module worker handoff; it supplies no evidence for the new integration.

The separate helper [run 39161202323](https://github.com/forge-trust/AppSurface/actions/runs/39161202323) verified original pidfd exit, retained private data, identical actual worker/host hit hashes, and a child-only method with both branches executed. Its full source and artifact bindings were verified. It supplied zero AppSurface coverage credit. The AppSurface integration requires a fresh successful native run and unchanged full gate; local C3/None image checks and metadata tests are insufficient.

### Workspace rejection receipt

The [workspace guard measurement](WorkspaceGuardMeasurement.cs) requires the six distinct [real workspace rejection controls](../../.github/issue779-checkpoint3/README.md#native-workspace-rejection-controls) in addition to the original seven application cases and root kernel tests. It checks original failure phase/code, failed root exit, empty original/final groups, retained account quarantine and unchanged outside sentinels. Producer cases additionally require a passing original coverage procedure before the failed seal, with actual unsafe-node observations retained by the fixture. The helper validates source-bound JSON only and can never create an owner, release accounts or register an accepted proof. Its [portable tests](WorkspaceGuardMeasurementTests.cs) establish consistency checking, not genuine native execution or a coverage gate pass.

The selected root-filesystem class also includes five [producer copy component controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxProtectedNodeNativeTests.cs) using actual retained deployments and destination descriptors. They establish copying mechanics and irreversible reservation behavior, never producer/account ownership. The root TRX must include every discovered selected case with zero skipped or failed results; portable skips cannot satisfy this requirement.

### Separate failed-SDK measurement

[`SdkFailureMeasurement.Validate`](SdkFailureMeasurement.cs) requires the [separate genuine failed-test SDK consumer](../../.github/issue779-checkpoint3/README.md#separate-sdk-failed-producer-consumer), in addition to the original consumer matrix. It compares the completed report and finalized receipt hashes, common published originals with distinct failure instrumentation, required v3 worker/cache/host byte handoffs with explicit pre-exit provenance, actual failed-test counts, the original ready-guard observation, no-claim manifest state and physical cleanup/account absence. It also requires hits in the current producer's nonzero-result arm. The main native test locates that arm only after checking the actual producer source against the full current source capture; report labels cannot select another line as a substitute.

The separate negative report remains independently inspectable. The later `native-sdk.json` is aggregate SDK coverage once its supported merge includes that report; only the normal control's retained v1 witness independently attributes its original worker handoff. JSON consistency is not admission or OS authentication. The [constructed receipt controls](SdkFailureMeasurementTests.cs) deliberately run without workers, pidfds, accounts or an accepted proof, and therefore supply no native coverage or qualification claim themselves. Every new input is mandatory in the genuine native measurement test before the unchanged solution gate consumes the merged report.

### Account mechanism data validation

[`AccountMeasurement.Validate`](AccountMeasurement.cs) compares six detached inputs:
the independently selected source commit and compiled variant, full source-capture
bytes, final measurement binding, original native receipt, and unmodified standard
report. Each byte input is bounded at 8 MiB. The caller must compare the full capture
byte-for-byte to its independently verified parent capture; this helper performs no
Git refresh, process launch or ownership construction. Its current v1 contract checks
all fourteen DLL/PDB hashes, source/report/native byte digests, exact five-ID domains,
variant-specific utility/cleanup outcomes, and real account/owner document hits.
`ValidateMerged` retains the same receipt and image checks for v2 packets, allowing
32 MiB only for standard reports. Before using rollback data, the actual native test
also invokes [`AccountMeasurementLineage.Validate`](AccountMeasurementLineage.cs)
with the original C3 binding/report/native/root-TRX and both account packets. It checks
the complete C3 → creation → rollback lineage, hashes, run/attempt identity and each
stage's source-bound current-method witness. Its eleven byte inputs are bounded at
160 MiB in total. The two account reports are connected through standard collector
merges, but their successful local data tests do not establish a native merged run.

The reviewed compiler-witness map pins both the private account partial and
`LinuxRunAccounts.cs`. The [ordinary checkout regression](AccountMeasurementTests.cs)
hashes those actual files before native input is needed, so an unchanged generated
method name cannot hide a stale partial fingerprint. After a deliberate source edit,
review the current method mapping and retained collector report before refreshing
that fingerprint. The merged packet must still match both current fingerprints;
rebinding its outer digests cannot substitute the historical partial. This is a
source-map consistency check, not new native execution or account authority.

The [detached controls](AccountMeasurementTests.cs) cover valid boundary neighbors
and malformed observations without creating accounts or physical-success evidence.
The [actual account driver](../../.github/issue779-checkpoint4/run-accounts.py) owns
only finite fixture preparation and observation; the [private C# account controls](../../.github/issue779-checkpoint4/measurement/accounts-README.md)
perform authenticated account work and strict closure. Source
`488143ea14b791fae6755bec50f33646e262c3fb`,
[run 39194022902](https://github.com/forge-trust/AppSurface/actions/runs/39194022902),
passed both native mechanisms: six settled utilities for normal closure and seven
for the expected collision rollback, with strict name/numeric absence and complete
source/artifact digest verification. That run's SDK worker startup failed and its
full gate was skipped; account success establishes neither consumer acceptance nor
a solution/patch coverage pass.

### Producer root measurement validation

[`ProducerRootsMeasurement.Validate`](ProducerRootsMeasurement.cs) takes eight byte
inputs: the independently verified current source capture, final producer binding,
combined and inner standard reports, producer native receipt, and the exact rollback
binding/report/native bytes already checked by the account lineage validator. Reports
permit 32 MiB each, other inputs 8 MiB, and the combined input bound is 160 MiB. The
helper performs no process launch, filesystem lookup, account creation or report merge.
Its [detached model controls](ProducerRootsMeasurementTests.cs) test comparisons only.

Only the completed v2 [producer-root packet](../../.github/issue779-checkpoint4/measurement/producer-roots-README.md)
is eligible for the final merge. Its external predecessor must bind those exact
rollback bytes and the same workflow run/attempt. Both original/instrumented/restored
seven-module DLL/PDB maps, zero collector/driver exits, seven ordered native controls,
physical settlement/account outcomes and exact current-source witnesses are required.
The C2 mutation method and the packaged None quota arm have separate source mappings;
an earlier positive method cannot stand in for either current witness. Metadata never
creates an owner, imports worker hits or admits a proof.

The standalone v1 packet from [run 39203788097](https://github.com/forge-trust/AppSurface/actions/runs/39203788097)
passed all seven actual Linux producer controls on source
`2dfb35e1155344b155fbe3154c7cb9773f54bf2b`; its artifact digest and full 3288-file capture,
image maps, original restoration, case receipts, log/report hashes and current witnesses
were verified. V1 has no external predecessor and remains ineligible as the final gate
input. The connected v2 chain still requires its own current-source native result and
the unchanged full gate; standalone success supplies neither.

The earlier six-module integration data checks passed 189 source-built cases: 132 producer-root cases
and 57 existing account/lineage cases, with zero failures, skips or compiler diagnostic
lines. Exact-file formatting completed without changing source bytes. The integrated
Python predecessor suite passed all 30 data controls. These local checks verify the
consistency APIs and ordering; they supply no native execution or coverage-gate pass.
