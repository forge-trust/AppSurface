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
