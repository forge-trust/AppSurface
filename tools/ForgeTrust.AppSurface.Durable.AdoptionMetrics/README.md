# Durable adoption metrics

This repository-only tool verifies the executable-contract adoption evidence
owned by [AppSurface Durable](../../Durable/evidence/executable-contract-adoption.md).
It is intentionally dependency-free and measures source shape; it does not load
consumer assemblies or infer application policy.

Run it from the AppSurface repository root:

```console
dotnet run --project tools/ForgeTrust.AppSurface.Durable.AdoptionMetrics -- \
  --spec Durable/evidence/executable-contract-adoption.measurements.json \
  --consumer-root /path/to/skoolit \
  --output Durable/evidence/executable-contract-adoption.results.json
```

The consumer checkout must be at the exact `baselineCommit` in the
specification, and every selected consumer file must match that commit. Start
and end tokens are compared to trimmed physical lines and must each be unique
inside their file. Token lines are excluded; every nonblank physical line
between them is counted, including comments and braces.

The specification contains one baseline and one proposed entry for each of the
three fixed regions. Baselines are observations rather than size gates.
Proposed regions pass only when their count is at or below `limit`. Expected
counts and pass values are checked before the deterministic result is written,
so source drift fails instead of silently refreshing the evidence. Git
verification is bounded to 30 seconds per command and Ctrl-C cancels the active
measurement; a canceled or timed-out child process is terminated best-effort.

The internal `GitConsumerRevisionVerifier` accepts an optional `TimeProvider` for
command-deadline tests; normal execution uses `TimeProvider.System` and the same
30-second command limit. Both output-reading and quiet source probes use that
clock. Descendant and pipe cleanup retains its separate two-second system-time
budget, so a paused test clock cannot suspend cleanup. Real-process regression
tests observe the child PID before advancing the original one-second timeout or
caller-cancellation clock. They also force delayed shell startup and verify that
each recorded child exits. The real-time deadline tests remain in place. See the
[.NET time-provider testing reference](https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing)
for the distinction between advancing a test clock and changing a production limit.
