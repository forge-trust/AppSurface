# Private temporary filesystem lifetime probe

This dependency-free **diagnostic test executable** observes an OS prerequisite for
measuring the existing [C# worker](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md).
It is not a runtime supervisor, an admission provider, an application registration,
or an accepted consumer proof. The normal AppSurface executable still owns both
reserved roles; no production command or unit recipe changes here.

The [workflow](../../workflows/issue779-worker-measurement-probe.yml) builds the
trusted helper before root execution and stages immutable binaries outside home.
The root parent owns one fresh named transient unit before launching its nonroot
copy with `PrivateTmp`, strict system/home protection, empty capabilities and an
independent 30-second OS runtime limit. It binds the actual PID, start time,
complete UID/GID tuples and exact kernel cgroup around retaining that process's
private `/tmp` directory. A retained pidfd—not a new lookup of a departed PID—
observes the original leader's exit. Its retained unit metadata must report the
same ExecMainPID, normal exit code and zero status before data is inspected.
Manager metadata may lag the pidfd notification, so those fields are polled
under one five-second allowance shared with each bounded query.

The child waits for a sealed root marker, then its synchronous process-exit
callback writes fixed **diagnostic bytes**, not Coverlet counters. The parent
checks whether those bytes remain readable through the retained namespace handle
before stopping the unit. Either observed reachability result is useful data.
A reachable file must be single-link, nonroot-owned, regular `0600`, unchanged
across a bounded read, and contain the exact expected bytes. Missing data is
recorded as missing; it is never replaced by zero counts. Cleanup attempts stop,
KILL and actual group emptiness even after startup or sampling failure.

`parent <absolute-private-evidence-directory>` is root/Linux-x64 only. `child
<32-lowercase-hex-generation>` is a fixed internal test mode for UID/GID 65534.
Unsupported entry exits 65 before process launch. `receipt.json` is exclusive
`0600` within a private directory, at most 16 KiB, and includes the source-test
schema, generation, actual sampled identity, namespace distinction, original
pidfd exit, retained terminal status, reachability and cleanup outcome. It always
records `native_acceptance: false` and `coverage_credit: false`. Errors expose
only a closed lifecycle stage and exception type, never raw proc bytes or output.

A positive prerequisite result would still not prove a worker coverage bridge.
That would additionally need exact pinned instrumented DLL/PDB mapping, genuine
tracker-file capture and unchanged-byte handoff to the pinned collector, plus a
worker-only executed path in the resulting report. No such hits or coverage gain
are claimed by this probe. The [solution/patch gate](../../../scripts/coverage-solution.sh)
and all production isolation, deadlines and trust requirements remain unchanged.
