# Native supervision measurement tests

This test project validates the source-bound Linux measurement that the full solution coverage run merges into the ordinary Coverlet report. It checks that the report contains executed lines in the real Linux root observation path, `LinuxEmptyObservationExecution.cs`, and that the attached native receipt records the seven physical C3 controls. It is a test and measurement surface; it does not add a production application registration, admit a proof, or grant trust.

For the supervision contract and its lifecycle boundaries, see the [supervision project reference](../ForgeTrust.AppSurface.Evidence.Supervision/README.md). The larger C3 fixture and its evidence limits are described in the [checkpoint 3 README](../../.github/issue779-checkpoint3/README.md), and the cutover and full-gate context is in the [Issue 779 C# runtime cutover reference](../../docs/evidence/issue779-csharp-runtime-cutover.md).

## Test surface and selection

`NativeMeasurementTests.LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls` reads the prepared inputs from `TestResults/issue779-native-measurement`. It verifies the source revision against the checked-out Git `HEAD`, checks the SHA-256 bindings, requires the recorded Coverlet version and an actually changed supervision assembly hash, validates the native receipt and its seven ordered cases, checks each case's settlement and final group state, and inspects the Coverlet JSON for nonzero hits in `LinuxEmptyObservationExecution.cs`.

`LinuxMeasurementFactAttribute` selects this test only when the host is Linux **and** `EVIDENCEHOST_NATIVE_MEASUREMENT=1`. macOS, Windows, and ordinary Linux runs without the opt-in report an explicit skip. This keeps unrelated Linux test runs from failing because they do not carry the dedicated workflow's prepared evidence. In the dedicated lane, the workflow sets the flag only after it downloads and stages all five files below and writes `native.runsettings`. Once selected, a missing input, wrong source or digest, incomplete case set, failed control, or absent native-root coverage hit fails the test. The flag controls test selection and the project runsettings condition only; it is not authority or evidence by itself.

Run the test as part of the complete [solution coverage gate](../../scripts/coverage-solution.sh) in the prepared runtime workflow. The project targets `net10.0` and uses the centrally pinned `coverlet.collector` 10.0.1 as a private test dependency. Its Linux-only `RunSettingsFilePath` is enabled by the same `EVIDENCEHOST_NATIVE_MEASUREMENT=1` property. A local invocation without prepared inputs is expected to skip; setting the flag manually selects the test and therefore requires the complete prepared input set and runsettings.

## Prepared inputs and merge settings

The runtime workflow downloads the current application-lane artifact and copies exactly these five files into `TestResults/issue779-native-measurement`:

| File | Meaning |
| --- | --- |
| `measurement-binding.json` | Schema, source revision, collector identity, original and instrumented supervision module hashes, and hashes of the source/receipt/report inputs and the root filesystem TRX. |
| `native-supervision.json` | Current-run native Coverlet JSON emitted by the private Coverlet Console measurement. |
| `root-files.trx` | Hash-bound current-run passing root filesystem controls for all three expected classes; zero skips or failures. |
| `source.json` | Complete source capture bound to the tested Git revision. |
| `native-receipt.json` | Receipt for the seven native C3 controls, including source, per-case outcomes, and `trusted_eligibility: false`. |

After those copies succeed, the workflow writes `native.runsettings` with the standard `XPlat Code Coverage` collector, `Format` set to `cobertura`, and `MergeWith` set to the absolute path of `native-supervision.json`. Coverlet's collector reads `MergeWith` as a supported setting. That behavior is present in the pinned Coverlet source at commit [`d21b5b6a08d48f51405ba2c5c5660f91a565776d`](https://github.com/coverlet-coverage/coverlet/tree/d21b5b6a08d48f51405ba2c5c5660f91a565776d), in [`CoverletSettingsParser.cs`](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/legacy/coverlet.collector/DataCollection/CoverletSettingsParser.cs) and [`CoverageWrapper.cs`](https://github.com/coverlet-coverage/coverlet/blob/d21b5b6a08d48f51405ba2c5c5660f91a565776d/src/legacy/coverlet.collector/DataCollection/CoverageWrapper.cs). The collector remains the driver for the test run and merge; the project does not use `coverlet.msbuild`.

The test treats these files as measurements. It reads each with a size bound, verifies the recorded source and hashes, requires different original and instrumented assembly hashes, and checks that the source capture and native receipt identify the same `HEAD`. The coverage JSON must contain the supervision module and at least one executed line in `LinuxEmptyObservationExecution.cs`. A JSON file's presence or a plausible percentage alone cannot pass these checks.

## Instrumentation and native controls

In the application workflow, Coverlet Console 10.0.1 instruments the private CLI tool's supervision assembly before the measurement wrapper starts the native runner. The wrapper records the instrumented module hash, then the runner stages that private tool for the Linux cases. The original and instrumented hashes are retained in the binding so the test can distinguish actual instrumented execution from an uninstrumented or substituted module. This measurement does not change the production build or deployment, add a runtime hook, change grants, or alter the protected supervisor's policy.

The runner requires root on Linux x86-64 with systemd 255 and executes seven independently bounded cases through the real C# root supervisor:

1. Normal application through the CLI tool.
2. Normal application through the SDK tool.
3. Resource readiness failure with an independently observed HTTP 503.
4. Factory stall before DCP starts.
5. Cancellation while the producer is running.
6. A stuck descendant whose live kernel identity and TERM-handler marker are checked before cancellation.
7. Disposal failure where a retained scratch holder causes the expected `EBUSY` unmount rejection and identities remain quarantined.

The receipt is produced only after the harness checks the original root owner's terminal outcome, actual unit and cgroup state, and the case-specific process observations. The test requires all seven cases in order, each marked passed, empty final groups, and a settled application observation with the two empty-group checks, output-pump joins, and pending-start join. The runner also checks kernel process identity and account custody; normal and applicable negative cases require account removal, while the intentional disposal-failure case requires accounts to remain present. The ordinary output-accounting receipt requires registered stream pairs, reconciled received-byte counts, EOF on both streams, and no read failure. The disposal-failure case is intentionally a failed root outcome with quarantined identities, not a successful evidence publication.

Coverage describes only code actually reached by the measured process. It proves that the source-bound Linux root path ran when the test finds the required hit in `LinuxEmptyObservationExecution.cs`; it does not presume hits from a force-killed process or from a worker hidden by `PrivateTmp`. The SDK tool image stays uninstrumented, so this report does not claim its execution hits. It also does not claim every native process is instrumented or covered.

## Relationship to the full gate and trust

The full gate remains the existing [`scripts/coverage-solution.sh`](../../scripts/coverage-solution.sh) command. It merges this native report through the standard collector and evaluates the same aggregate thresholds of 95% lines and 85% branches, plus the configured patch thresholds of 95% lines and 85% branches in Codecov patch-line mode. The gate's default 0.5 percentage-point tolerance applies, and the default patch comparison base is `origin/main` when no explicit base is supplied. This sidecar does not report that the gate has passed; the aggregate and patch reports from an executed full gate determine that result.

The private fixture registrations are not production application-table entries. The production table remains empty, and the native receipt records `trusted_eligibility: false`. Passing measurement checks is neither production acceptance nor a Trusted proof. [Run 39146333953](https://github.com/forge-trust/AppSurface/actions/runs/39146333953) completed all seven instrumented Linux application controls on source `771d0538347998bdbcff0257fe5089c350ca68bc`. The [cutover record](../../docs/evidence/issue779-csharp-runtime-cutover.md) binds its artifact digest and complete source verification. Its separate numerical full-gate result was still pending when this observation was recorded; later changes require fresh measurements.

## Root filesystem measurement

The application workflow additionally builds the ordinary root filesystem control
classes with the same C3 project graph, compiler and Release configuration. Before
instrumentation it requires the test image's Supervision DLL/PDB hashes to match
the native application's original module hashes. The pinned Coverlet Console runs
only `LinuxProtectedNodeNativeTests`, `LinuxRetainedReportNativeTests`, and
`LinuxKernelIdentityNativeTests` as actual
Linux x64 root on their own fresh `/run` trees. These tests exercise existing
mandatory `openat2`, retained inode/name checks, bounded reads and real file
mutation rejection; they create no supervisor, process lease or accepted proof.

The same pinned collector uses its supported `--merge-with` report setting to add
those actual hits to the source-bound application report. No raw worker hit file
is read or reconstructed. The workflow binds the real `root-files.trx` SHA-256 and
requires positive executed/passed counts with zero failed or skipped cases before
publishing the combined report. [`RootFilesystemMeasurement`](RootFilesystemMeasurement.cs)
checks that bound TRX again in the solution lane, including all three expected test
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
