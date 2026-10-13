# N07 helper process-lifetime correction

This private sidecar copies the output-capture correction into a fresh folder and changes only the process-observation lifecycle plus focused controls. The runner keeps merged stdout/stderr on a nonblocking pipe, stores at most 8 MiB, counts bytes discarded after the cap, and fails closed on overflow, I/O errors, missing EOF, timeout, nonzero exit, or uncertain process-group state. It does not set `RLIMIT_FSIZE`; the reported file-size limit is observational only.

The runner never calls `Popen.poll()`. On Linux it prefers `waitid(WNOWAIT)` to observe leader exit while retaining the child PID. On macOS, it uses bounded `ps` queries to observe the unreaped child as a zombie and inspect non-zombie members of the original process group. It waits for both original-group settlement and pipe EOF before normal reaping. Failure cleanup signals the original group only before `Popen.wait()` can reap the leader. If EOF is still missing at the caller's existing cleanup end, it closes its local pipe, reaps the leader within the remaining cleanup allowance, and reports failure without sending a later group signal.

The focused suite passed 7/7 on macOS 26.7.1 arm64 in 1.312 seconds. The real-child controls cover normal merged output, a regular file larger than the log cap, output overflow, nonzero exit, deadline cleanup with a signal-before-reap assertion, a leader that exits while a same-group descendant remains, and an escaped descendant that keeps the pipe open past cleanup. The escaped child finishes through its own bounded test lifetime after the runner reports missing EOF.

The macOS `ps` fallback was exercised with read-only process-list permission. Without that permission, the guarded observer fails closed. This host does not provide `/proc` or `os.waitid`, so the Linux `waitid(WNOWAIT)` and `/proc` group-scan paths remain untested here. No Linux behavior is claimed. The five-control output-capture suite, source folder, and primary repository were not changed by this sidecar. No .NET, native, Git, CI, or publication action was performed.

## Handoff receipt contract extension

The adjacent consumer-alignment sidecar keeps the helper success receipt's public command rows at exactly 16 fields. Successful rows require the fixed operation order, observed leader exit, settled descendants, EOF, successful deadline and I/O state, zero overflow/discard, and exact integer counters. `group_absent` is not synthesized for helper rows; the separate outer build receipt retains its own group-absence validation.

The helper receipt also pins `helper-build-private-diagnostics.json` by SHA-256. Downstream readers verify the bounded file bytes and retain copies only under private `control/` paths with mode 0600. The diagnostic contents are not placed in public results and do not grant execution authority. These copied legacy source pins are historical until a current source capture and independent review rebind them.
