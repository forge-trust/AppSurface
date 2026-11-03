# Completed coverage gate replay controller

`completed_gate_replay.replay_completed_gate(stage, inventory, stage_directory,
source_capture, source_root, disposable_parent, *, deadline=...)` runs the repository's
public `coverage gate` CLI against the retained Cobertura input from a source-bound
completed run. It is a bounded mechanism replay and report comparison. It does not
establish workflow success, test inventory success, gate eligibility, protected
admission, or qualification; all result flags for those claims remain false.

## Preconditions and fixed behavior

The caller supplies a validated typed stage and inventory, the raw staged directory,
the original source-capture bytes, and a clean disposable checkout. The controller
revalidates the retained gate archive, its report, Cobertura, comparison base, source
capture, HEAD, tree, clean state, and fixed base commit before execution. It requires
the original gate report to have passed and binds the retained report's raw Git diff
digest and byte count to a fresh `git diff` from the checkout. Missing original report
or an unsuccessful report rejects before build.

The source checkout must be mounted at
`/home/runner/work/AppSurface/AppSurface`. This preserves the original absolute paths in
Cobertura and the gate report. The source must have no build outputs or ignored files
before replay, and stage, source, and empty private disposable parent must be disjoint.
The controller uses only Linux with `waitid`/`WNOWAIT` support, .NET SDK 10.0.401,
locked restore of the CLI project dependency closure, and a Release CLI build with
warnings as errors. It invokes the fixed
public arguments at 95% line, 85% branch, 95% patch line, and 85% patch branch using
the Codecov patch-line mode, with GitHub summary disabled. The generated JSON report
is compared against the original by the strict data comparator in
[`completed-gate-README.md`](completed-gate-README.md); the controller does not
reimplement coverage arithmetic.

The caller supplies one absolute monotonic deadline with at most 20 minutes remaining.
The controller reserves 90 seconds for replay, 30 seconds for final checks, and caps
command output. Process-group teardown uses the caller's same absolute deadline. It writes
bounded raw stdout/stderr receipts and replay outputs under the invocation-owned
`coverage-gate-replay` child. It verifies source and retained inputs again before
return. The copied Cobertura file's descriptor remains open for the replay lifetime,
preventing inode-number reuse during replacement attempts; both its open inode and
the final path are checked after the CLI and before comparison. `retained_work_root`
names the resulting evidence and generated build/cache
files; `cleanup_complete` is false. The caller must dispose the isolated source
workspace and private parent after preserving any needed review evidence. No recursive
deletion is attempted, and this controller does not impose a runtime filesystem quota;
the post-run workspace inventory is capped at 16,384 nodes and 2 GiB.

## Decision and limitations

Use this controller only when a matching retained original report exists and the caller
can provide the exact source mount in an isolated Linux environment. Run 109's ASCOV120
artifact lacks the required original report, so it is rejected by preflight and cannot
be used for a replay. The retained transcript is preserved as opaque evidence; it is
not interpreted as proof that the original workflow or tests succeeded. Even matching
reports describe only a local replay observation. A separate trusted workflow and test
inventory validator would be required for those claims.

The source mount requirement is deliberate: rewriting Cobertura paths would change
input bytes and could alter how the public gate resolves source files. A different
mount, SDK, threshold, base, or argument set is a different replay and must not be
reported as this fixed procedure.

## Pitfalls

- Use an empty mode-0700 private parent owned by the current user; the helper will not
  reuse or clean a populated directory.
- Do not run the controller from an ordinary developer checkout. It builds the source
  tree in place and leaves `bin`/`obj`, NuGet, SDK, logs, and reports for the caller's
  isolated-workspace lifecycle.
- Do not infer original workflow success from `original_report_passed`, the opaque
  transcript, or `reports_match`.
- Preserve the returned work root until review evidence is collected. Failure paths
  attach its location to the raised error and leave it intact.

See [`completed-gate-README.md`](completed-gate-README.md) for the retained archive
preflight and report-comparison APIs, and
[`completed-native-replay-README.md`](completed-native-replay-README.md) for the shared
bounded process runner used by fixed Linux replay procedures.
