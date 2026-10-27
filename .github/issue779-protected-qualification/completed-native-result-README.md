# Fixed native validator result contract

`completed_native_result.parse_native_validator_trx(raw, deadline=...)` accepts a bounded VSTest TRX only when it contains exactly one passing result for the existing public method `NativeMeasurementTests.LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls`. It binds the `UnitTestResult`, `TestDefinition`, `TestMethod`, `TestEntry`, execution ID, test ID, test-list ID, assembly path suffix, and summary counters. It rejects foreign namespaces, extra results, nonzero failure/skip counters, malformed XML, DTD/entity declarations, and files larger than 1 MiB. The data consumed by the method is described in [fixed native input staging](completed-native-inputs-README.md), which requires a fully bound completed-run inventory.

The immutable parsed projection carries the TRX digest and fixed `validator_executed`, `native_acceptance`, and `qualification_complete` values, all false. A TRX is an observation from a test runner; parsing it does not verify the source checkout, build provenance, staged inputs, runtime custody, or qualification.

## Fixed invocation shape

`fixed_validator_argv(canonical_source_root, private_output_root)` returns a closed `dotnet vstest` argument tuple for the built test DLL, the one fully qualified public method, a fixed `native-validator.trx` logger output, and the private results directory. The output root must be an owned mode-0700 empty directory. The helper accepts no caller-selected test, assembly, collector, or runsettings path.

This is an argument builder, not a process executor. Before using it, a separate controller must verify the complete captured source against the exact checkout, build with pinned .NET SDK 10.0.401 while `EVIDENCEHOST_NATIVE_MEASUREMENT` is absent, stage the authenticated fixed 24 input leaves under the test's exact `TestResults/issue779-native-measurement` path, then set the opt-in only for this one filtered VSTest invocation. Do not pass runsettings or collector options: they can start the coverage collector and alter the intended bounded test invocation. The controller must join the test process before reading the bounded TRX and perform custody cleanup even after failure.

## Feasibility observation

On run 40483696166 attempt 1 at head `64e25cb406243a648edee31a9cf76935034759a7`, the four original provider-bound C3 application, C4 account creation, C4 account rollback, and C2 producer archives supplied all 24 fixed leaves. A pinned-SDK prototype built the exact captured checkout with opt-in unset and ran the one method through `dotnet vstest`; the retained TRX reported one pass, zero skips, and zero failures. The parser accepts that exact TRX with digest `3cb75724f423d2987bf0e1b69a663f125cfb82e5275faf0b64130fff3c7bf1ae`. That prototype used a mechanism-only four-role staging path because run 109 did not have a successful complete 37-artifact stage. Its result demonstrates public-method feasibility for those retained input bytes; it does not establish a successful protected run, full-stage validation, a completed workflow, native acceptance, or a gate pass. The receipt and exact logs are retained under `artifacts/issue779-checkpoint4/evidence/linux-attempt-109/measurement-watch/` in the primary workspace; the prototype execution log and TRX live in `/private/tmp/issue779-sdk401-cli-build-prototype-109/native-feasibility/`.

Run the portable parser tests with:

```sh
python3 -B .github/issue779-protected-qualification/test_completed_native_result.py
```
