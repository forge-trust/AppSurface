# Fixed native validator input staging

`completed_native_inputs.stage_native_inputs(stage, inventory, stage_directory,
source_capture, private_parent, deadline=...)` extracts the fixed 24 data leaves
read by the existing public
[`NativeMeasurementTests.LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls`](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/NativeMeasurementTests.cs).
The inputs are sourced only from the bound C3 application, account creation,
account rollback, and C2 archives in the matching completed-run stage.

The function checks the [typed stage/inventory binding](completed-validation-README.md), exact staged archive
inventory, API-bound archive hashes and sizes, the actual ZIP projection against
the inventory projection, each archive's role retention bounds, and byte-for-byte
source capture agreement before creating output. It enforces individual bounds
of 1 MiB for source captures, 32 MiB for designated reports, 1 MiB for TRX files,
8 MiB for other leaves, and 160 MiB total. It writes fixed relative leaf names
under `private_parent/native-data-inputs`, using private directory and file modes.

The returned frozen `NativeInputs` contains each relative path, byte count,
SHA-256, and owned inode identity. Its `validator_executed`, `native_acceptance`,
and `qualification_complete` fields are always false. This operation stages data;
it does not invoke the public validator, execute native operations, verify the
source checkout, or decide qualification. Callers must supply source-capture
bytes independently verified against the completed source checkout.

`verify_native_inputs(result, stage, deadline=...)` repeats the fixed tree inventory,
owned directory/file identities, sizes and content hashes, and binds the stored run/head
attribution back to the caller's typed stage and the source bytes. Staging calls it before
returning. A future execution controller must also call it before copying and
after execution; it rejects extra leaves, replaced inodes, linked directories and
same-inode content changes. The check grants no execution or acceptance claim.

## Public test path and lifecycle

The existing public test reads from its fixed
`TestResults/issue779-native-measurement` directory. This module deliberately
does not write to that path or alter the C# test. A later, separately controlled
execution step may copy these fixed relative leaves into that exact directory
inside a fresh checkout after verifying the checkout against the source capture.
The returned paths are relative to `NativeInputs.root`; do not derive target
paths from archive members or caller-supplied path mappings.

Successful staging intentionally persists for the caller's later controlled
copy. Call `cleanup_native_inputs(result)` once the caller no longer needs the
staged data. Cleanup checks the recorded owned-node identities and removes only
this invocation's `native-data-inputs` tree; it never removes the caller's
private parent. If a node has been replaced, cleanup fails closed and preserves
the substituted node. Failed staging attempts clean only nodes they created.

## Test invocation and settings

The method is marked with the repository's native-measurement test attribute and
is gated by `EVIDENCEHOST_NATIVE_MEASUREMENT=1` on Linux. The future execution
controller should build the exact captured source, then invoke only the fully
qualified public method with a test-case filter. Do not use the repository's
`dotnet test` runsettings for this bounded replay: the runsettings attach the
coverage collector and merge settings, which are not part of this input-staging
contract. Staging itself does not launch `dotnet`, `vstest`, or the test method.
The [fixed result contract](completed-native-result-README.md) defines the single-method
argument shape and strict TRX result parser for that future controller.

This is a data-input prerequisite, not evidence of a completed native replay.
