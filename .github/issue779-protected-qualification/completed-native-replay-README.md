# Fixed native-data procedure replay

[`completed_native_replay.py`](completed_native_replay.py) runs the public
`LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls` test
against the four fixed role archives projected into 24 files by
[`completed_native_inputs.py`](completed_native_inputs.py). It is a local
procedure check only. Its report fixes `validator_executed`, `native_acceptance`,
`qualification_complete`, and `gate_eligible` to false; a passed TRX remains an
observation and does not establish native acceptance or a qualification result.
`validator_executed` remains false in the projection as a conservative fixed
nonclaim; the outcome and hashes record the observed local procedure separately.
The standalone TRX parser also leaves that field false because parsing a file
alone cannot prove execution.

## API and ordering

Call `run_native_replay(stage, inventory, stage_directory, source_capture,
source_root, private_output_parent, *, deadline)` with the authentic typed stage
and inventory, a complete source capture, and a clean disposable checkout at the
captured commit. `private_output_parent` must already exist, be empty, mode 0700,
and owned by the invoking user. The single monotonic deadline is capped at 20
minutes; the controller reserves 30 seconds for cleanup.

The controller verifies the complete source capture with the trusted current
binding before running source-controlled restore/build/test code. It stages and
checks the fixed public inputs, copies them exclusively into the previously
absent `TestResults/issue779-native-measurement` path, and checks the copied
hashes and inode identities. It verifies the input files and source capture
again after the process run. Build uses the fixed Native.Tests project, locked
restore, and a Release build with warnings as errors. Native opt-in is absent
during SDK discovery, restore, and build; only the fixed VSTest process receives
`EVIDENCEHOST_NATIVE_MEASUREMENT=1`. The test command has a fixed assembly,
fully-qualified method, TRX logger, and result directory. No runsettings or
collector is accepted.

The controller retains one no-follow, close-on-exec read descriptor for each of
the 24 copied leaves until all process and owned cleanup checks finish. This keeps
the original inodes allocated, so unlink/recreate cannot reuse an input inode and
evade the final identity check. All 24 descriptors close on success and failure;
they are never inherited by the build or test process.

`_run_fixed_process` is an internal process seam for other fixed replay modules.
Callers must supply closed argv and a small explicit environment. It uses no
shell, bounds stdout and stderr independently, enforces the same monotonic
deadline, starts a new process group, and kills and joins the group on normal
leader exit, timeout, or output overflow. Linux uses `waitid(...WNOWAIT)` to keep
the leader PID pinned while it checks that no live group member remains and
joins both output readers. The default stream cap is 8 MiB each; the helper
allows a fixed caller-selected cap up to 20 MiB for other bounded consumers.
Callers pass the command deadline and the overall cleanup deadline separately.
It returns `(return_code, stdout_bytes, stderr_bytes)` and raises a fixed
diagnostic category without exposing subprocess text.

## Host and cleanup requirements

Run this controller on Linux with `waitid`/`WNOWAIT`, visible procfs and
`dotnet --version` exactly `10.0.401`. The
version check does not prove the SDK image digest; deployments that require
image provenance must run it inside their separately verified pinned SDK
environment. Restore uses the repository lock files. The disposable checkout
may retain ordinary `bin/` and `obj/` build outputs after return. The controller
removes only its inode-recorded input stage and newly created `TestResults` tree.
It retains bounded command logs and the original TRX under `retained_logs` and
`retained_test_output`. Its isolated `native-replay-state` directory is retained
because restore may create cache files not individually created by the controller.
The result exposes all retained roots and sets
`cleanup_complete` to false; the caller must eventually discard the disposable
private parent. If any owned node is replaced or unexpected contents appear,
cleanup fails closed and no result is returned. Use a quota-limited private
filesystem/container for the disposable checkout and output parent because
locked restore can populate NuGet caches.

Process-group teardown covers helpers that remain in the invocation's group.
A child that creates another session can escape that group; the caller must run
the replay in a disposable isolated container or equivalent outer containment
and settle that environment before treating its artifacts as completed evidence.
This local helper does not supply the native supervisor's stronger process ownership.

Use this procedure when checking whether the captured public measurement test
can run from retained typed evidence. It does not replay the original CI job,
the C1/C2/C3 source callbacks, the numerical coverage gate, or protected
admission. Missing artifacts, unsupported SDK/platform, a failed test, or a
cleanup failure leave no successful procedure report.

## Tests

[`test_completed_native_replay.py`](test_completed_native_replay.py) checks the
bounded process seam, output overflow and deadline teardown, descendant process
cleanup, platform and deadline preflight, source/copy custody, failure ordering,
environment isolation, and fixed non-authorizing result fields. Its public
controller fixture uses synthetic archive contents and a mocked build/test
process seam; it tests controller mechanics only and does not simulate or prove
a passing native data validator.
