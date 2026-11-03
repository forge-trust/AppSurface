# Checkpoint two subject failure scenarios

This subject fixture keeps its six calculator data rows unchanged. The root selects one fixed `Scenario.Kind` constant in the copied subject before the subject is sealed. `SubjectFailureScenarios` accepts only the four existing values (`normal`, `test-failure`, `cancel`, `quota`) and the two cleanup controls described here; it does not supply producer authority or evidence claims.

## Cleanup failure

For `cleanup-failure`, the first calculator row creates an owned file and directory under the producer's actual `HOME`, then creates a symlink to that directory under the actual `TMPDIR`. The ordinary case-entered marker is published only after this setup returns. For `test-failure`, `cancel`, and `quota`, the first calculator row writes the marker to an exclusive pending file, flushes and closes it, then atomically renames it to the observed path. Thus the root observer cannot see a partial marker, and the marker is fully durable before any scenario-specific failure, wait, or output branch begins. Producer workspace sealing should encounter the symlink and quarantine cleanup; the root driver remains responsible for confirming the real cleanup result.

## Ignored cancellation

For `ignored-cancel`, the first calculator row launches the fixed `/usr/bin/python3` child with inherited standard streams. After installing its `SIGTERM` handler, the child atomically publishes `checkpoint2-ignored-child-pid` with its actual numeric PID, then the ordinary `checkpoint2-case-entered` marker, and finally `checkpoint2-child-ready` as the awaited readiness marker. The root driver must independently verify all three markers and the live kernel process identity before sending cancellation. The handler records `checkpoint2-term-observed` and returns to `signal.pause()`, so the descendant remains alive and keeps the inherited pipes open until the external root control settles its real cgroup. The parent test awaits the child's actual exit task; marker observation is event driven and does not use sleep-based readiness polling.

The external root driver must correlate the PID marker with the observed process's numeric UID, GID, and cgroup and require terminal settlement there. These fixture markers identify a process lifecycle only; they do not assert that cleanup succeeded.

## Actual quota output

The quota test writes exactly320 newline-terminated64KiB payload rows with distinct fixed numeric prefixes to an exclusive fixed file
under its actual `TMPDIR`, after publishing the case marker. Its fixed `AfterTargets="VSTest"`
subject target invokes a fixed inline SDK task that copies the exact bounded file to the original
SDK standard-output stream. This bypasses MSBuild logger verbosity; high-importance messages would
still be suppressed by the unchanged quiet command. This is untrusted subject code evaluated only inside the armed producer unit; it grants
no execution authority. The previous `Console.OpenStandardOutput()` testhost write was captured by
VSTest for a successful test and did not cross the supervisor's output budget. The root control must
still observe more than16MiB in the original shared ledger, a failed root outcome, physical settlement
and actual account removal. File size or testhost output alone cannot satisfy the control.
