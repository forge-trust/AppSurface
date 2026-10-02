# ForgeTrust.AppSurface.Evidence.Coverage

`ForgeTrust.AppSurface.Evidence.Coverage` is a private implementation package shared by the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) and its first-party [Evidence workflow](../ForgeTrust.AppSurface.Evidence.Cli/README.md). It ships only as a transitive support package; it is not a consumer extension point, direct-install package, or separately supported integration package.

The assembly owns one in-process implementation of coverage discovery, execution, watchdog supervision, merge, gate evaluation, patch analysis, and controlled artifact writing. The CLI owns command parsing and presentation; Evidence owns policy and claim translation. This direction ensures Evidence produces coverage claims from the same execution engine as `appsurface coverage run` and `appsurface coverage gate`, without invoking a second process. Both surfaces use the fail-mode default. For VSTest test phases with at least 90 seconds remaining, the engine requests no-dump hang blame and reserves part of the current budget for sequence output and summary. Evidence's producer deadline remains authoritative and includes discovery/build time. The [CLI hang-diagnostics reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) documents the timeout arithmetic, opt-outs, artifact statuses, and migration for healthy long-running tests. Evidence results never include test names or Sequence.xml contents.

Coverage orchestration and transports remain internal. The metadata-only factory below exposes the fixed registration needed for exact first-party host matching; it is not an injectable execution extension. Consumers should use the documented public [coverage commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) and [EvidenceHost workflow](../../start-here/evidencehost.md).

The package depends on [Evidence.Contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#shared-lifecycle-and-output-limits)
for the run-wide received-byte quota shared by subject and reporter output. Every received byte is charged even
when only a bounded prefix is retained. Crossing the limit closes admission and cancels and drains the owned
process before returning a failed result. Callers must use one quota for the full Evidence run; creating a new
quota for each command would incorrectly reset its cumulative limit. This internal dependency supplies output
accounting; it does not grant runtime admission or enable a supported provider.

## Fixed restricted coverage registration

`EvidenceRestrictedCoverageProducerFactory.Create(EvidenceProducerDeclaration)` returns the sealed `EvidenceRestrictedCoverageRegistration`, which implements `IEvidenceProducer`. Both types are in `ForgeTrust.AppSurface.Evidence.Coverage`. Creation accepts only `coverage` version `1.0.0`, snapshots the complete declaration and performs no process, transport or artifact I/O. Its public `Declaration` contains read-only copies of resource, assertion and artifact lists. A host can inspect the concrete type and compare every declaration field with its compiled catalogue before starting an application. Matching metadata supplies no runtime authority.

```csharp
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

EvidenceRestrictedCoverageRegistration registration =
    EvidenceRestrictedCoverageProducerFactory.Create(declaration);
// Inspect registration.Declaration; creation has not admitted or run coverage.
```

Malformed or unsupported metadata rejects with fixed `ASEVD404`; null declaration rejects with `ArgumentNullException`. Creation bounds identifiers to 96 characters, assertion text to 256, artifact roots to 4095 and media types to 128; null, blank and control-containing strings reject. Resource/assertion/artifact counts are limited to 16/128/64, producer timeout to 1–1800 seconds, each artifact limit to 1–256 MiB and serialized declaration bytes to 20 MiB. The existing restricted procedure still validates the declared assertions, slots and coverage gate before subject execution.

`ProduceAsync(EvidenceProducerContext, CancellationToken)` requires the exact admitted plan and declaration plus a [protected writer binding](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#restricted-producer-callback-binding). Public/local writers, absent bindings, callback reuse, stale admission and mismatches reject with `ASEVD410`. The factory accepts no solution path, executable arguments, transport, reporter, worker or capability parameter. Its internal adapter obtains solution, tool and test-output roots from the actual authenticated supervisor and copies the protected planning diff. Caller cancellation is additional to the actual captured stage token.

The entire restricted run, retained report collection, private staging, protected ReportGenerator merge, numeric gate and cleanup is registered with the existing lifecycle before its task is returned. Ignoring that task cannot remove it from owned stop/join. The subject command and protected reporter share one run-wide received-output quota; artifact writes use the existing retained writer and artifact quota. Assertion output requires both the fixed restricted procedure and its declared numeric gate to pass.

The CLI uses this single implementation. The Aspire host can reference the same package and bind it through its protected lifecycle; the factory itself supplies neither application readiness nor a restricted application lease. Core unit tests use intentionally internal fake transports to check procedure behavior and establish no native or Trusted proof. Production acceptance remains closed as recorded in the [consumer acceptance requirements](../../docs/evidence/issue779-consumer-acceptance.md).
