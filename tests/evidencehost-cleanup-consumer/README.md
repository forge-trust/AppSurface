# EvidenceHost cleanup package consumer

This standalone .NET 10 console application consumes the packed
[`ForgeTrust.AppSurface.Evidence.Aspire`](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md)
package through its only direct `PackageReference`. It has no project references,
and explicitly sets `IsTestProject` to `false` because the repository classifies
every project under `tests/` as a test project by default.

Run the proof from the repository root:

```bash
./scripts/verify-evidencehost-cleanup-package.sh
```

The verifier copies the Aspire and Evidence.Contracts pack projects into its new
temporary workspace, packs both at one candidate version into a local feed, copies
this consumer into a separate directory, and restores it using the local feed plus
NuGet.org for runtime dependencies. Its isolated assets check requires exactly one
direct package dependency and no project references. The consumer checks that a
passing producer with noncompleting disposal produces no claim, marks cleanup
incomplete, and reports only a safe diagnostic; direct host disposal fails with
`ASEVD306`; and an explicitly registered
[`IEvidenceExecutionLifetime`](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/IEvidenceExecutionLifetime.cs)
is called and joined before ordinary disposal. Error unwinding also preserves the original registration failure and exposes the bounded `CleanupDiagnostic` on the host. All fixture gates are released in
`finally` blocks.

The host defaults are an `ExecutionTimeout` of one hour and a separate
`CleanupTimeout` of 30 seconds. The fixture supplies a one-second cleanup allowance
to keep the failure cases deterministic and checks terminal host output within
three seconds. Each run archives the packed feed, copied source, package cache,
stage logs, and a readable `result.txt` in the printed work directory. The whole
operation has a 15-minute default deadline, configurable with
`--total-timeout-seconds`.

The verifier's deadline bounds its own build and consumer subprocesses; it does
not establish generic process-tree attestation for arbitrary evidence producers.
Keep the CI job's outer timeout as an independent guard. See the package's
[execution and cleanup guidance](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#execution-and-cleanup-budgets)
and the [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md#bounded-host-cleanup)
for the ownership boundary and non-cooperative work requirements.
