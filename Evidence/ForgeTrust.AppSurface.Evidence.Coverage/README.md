# ForgeTrust.AppSurface.Evidence.Coverage

`ForgeTrust.AppSurface.Evidence.Coverage` is a private implementation package shared by the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) and its first-party [Evidence workflow](../ForgeTrust.AppSurface.Evidence.Cli/README.md). It ships only as a transitive support package; it is not a consumer extension point, direct-install package, or separately supported integration package.

The assembly owns one in-process implementation of coverage discovery, execution, watchdog supervision, merge, gate evaluation, patch analysis, and controlled artifact writing. The CLI owns command parsing and presentation; Evidence owns policy and claim translation. This direction ensures Evidence produces coverage claims from the same execution engine as `appsurface coverage run` and `appsurface coverage gate`, without invoking a second process. Both surfaces use the fail-mode default. For VSTest test phases with at least 90 seconds remaining, the engine requests no-dump hang blame and reserves part of the current budget for sequence output and summary. Evidence's producer deadline remains authoritative and includes discovery/build time. The [CLI hang-diagnostics reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) documents the timeout arithmetic, opt-outs, artifact statuses, and migration for healthy long-running tests. Evidence results never include test names or Sequence.xml contents.

Coverage orchestration and transports remain internal. The metadata-only factory below exposes the fixed registration needed for exact first-party host matching; it is not an injectable execution extension. Consumers should use the documented public [coverage commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) and [EvidenceHost workflow](../../start-here/evidencehost.md).

## Compiler-map input preservation

The ordinary `CoverageRunReportGenerator` passes explicit, staged report files through internal
`CoverageCompilerMapInputs.PrepareAsync(coverageFiles, outputDirectory, token)` before invoking the same
packaged ReportGenerator. This also applies to the public [coverage merge command](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-merge).
It addresses a ReportGenerator 5.5.10 input-order defect: different C# async state-machine classes for
one source method can overlap at a source line, and folding them within one input can replace an
executed observation with a later zero-hit observation.

The adapter retains each colliding class map in a separate fresh XML input, keeps all other classes
together, and selects every original class exactly once. It preserves the complete original class,
method, line and condition nodes; it does no counter arithmetic and does not equate condition IDs
from different builds. Untaken conditions remain untaken in their original maps. The standard reporter
then parses these inputs and computes its usual output. Coverage filters, gate rules and the original
reports stay unchanged. A retained map inventory is not a deduplicated inventory of semantic source
branches, and this preservation cannot recover information already conflated by an earlier collector.

Partition files are retained under a fresh `compiler-map-inputs-*` directory inside the owned merge
output. Explicit reports need absolute class filenames, or a single absolute `<sources><source>`
anchor with canonical relative filenames. The filename and source-anchor nodes remain unchanged.
Non-colliding inputs, ambiguous relative-filename reports and direct reporter glob
expressions pass through unchanged. XML parsing ignores external DTDs and has no resolver; malformed
input rejects with a fixed `InvalidDataException`. Cancellation uses the original merge token during
reading, partitioning and writing, with no new timeout. This internal adapter grants no execution,
native ownership or coverage-gate authority. The protected reporter transport continues to consume
its separately retained subject reports through its existing custody contract.

The package depends on [Evidence.Contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#shared-lifecycle-and-output-limits)
for the run-wide received-byte quota shared by subject and reporter output. Every received byte is charged even
when only a bounded prefix is retained. Crossing the limit closes admission and cancels and drains the owned
process before returning a failed result. Callers must use one quota for the full Evidence run; creating a new
quota for each command would incorrectly reset its cumulative limit. This internal dependency supplies output
accounting; it does not grant runtime admission or enable a supported provider.

## Protected reporter output relay

The fixed protected coverage producer runs the packaged ReportGenerator through the internal
[`CoverageRunProcessRequest`](../../Cli/ForgeTrust.AppSurface.Cli/CoverageRun.cs) process runner only while its
authenticated worker is armed. For that reporter request, `RawOutputMirror` is a caller-owned `Stream` opened from
the root process's standard error. The runner relays every actual stdout and stderr read to that stream, serializes
the two pumps at chunk boundaries, and awaits both the write and flush with the original caller cancellation token
before it charges the local process-output quota or discards any excess buffered capture. Its default is `null`, so
ordinary [`appsurface coverage run`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) keeps its
existing capture and logging behavior.

The mirror combines both channels without framing and does not promise their relative ordering. The protected root
supervision path drains worker stderr into a bounded root-private prefix; raw reporter bytes must stay in that private
diagnostic path and must never be echoed to public CLI output. The generator flushes the stream before returning and
owns its `Console.OpenStandardError()` handle; the process runner never closes a caller-supplied mirror. A mirror
write/flush failure cancels the child and fails the joined process operation, and quota overflow remains a failed
run. Callers must supply a protected destination and must not use this internal relay as a general logging sink.

The transport changes output accounting only. It adds no worker request, callback, deadline, runtime authority, or
proof claim. Native acceptance still depends on the independent Linux procedures in the
[EvidenceHost acceptance record](../../docs/evidence/issue779-consumer-acceptance.md).

## Fixed restricted coverage registration

`EvidenceRestrictedCoverageProducerFactory.Create(EvidenceProducerDeclaration)` returns the sealed `EvidenceRestrictedCoverageRegistration`, which implements `IEvidenceProducer`. Both types are in `ForgeTrust.AppSurface.Evidence.Coverage`. Creation accepts only `coverage` version `1.0.0`, snapshots the complete declaration and performs no process, transport or artifact I/O. Its public `Declaration` contains read-only copies of resource, assertion and artifact lists. A host can inspect the concrete type and compare every declaration field with its compiled catalogue before starting an application. Matching metadata supplies no runtime authority.

```csharp
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

EvidenceRestrictedCoverageRegistration registration =
    EvidenceRestrictedCoverageProducerFactory.Create(declaration);
// Inspect registration.Declaration; creation has not admitted or run coverage.
```

Malformed or unsupported metadata rejects with fixed `ASEVD404`; null declaration rejects with `ArgumentNullException`. Creation bounds identifiers to 96 characters, assertion text to 256, artifact roots to 4095 and media types to 128; null, blank and control-containing strings reject. Resource/assertion/artifact counts are limited to 16/128/64, producer timeout to 1–1800 seconds, each artifact limit to 1–256 MiB and serialized declaration bytes to 20 MiB. Because the restricted command is fixed to Debug, serial execution and no managed test results, `coverageExecution` must be omitted or specify exactly `Debug`, `parallelism: 1` and `testResults: none`; `coverageArtifactRetention` must be `managed`. `full` retention and other valid ordinary-producer execution options are rejected with `ASEVD404` before a worker lease can launch native work. The existing restricted procedure still validates the declared assertions, slots and coverage gate before subject execution.

`ProduceAsync(EvidenceProducerContext, CancellationToken)` requires the exact admitted plan and declaration plus a [protected writer binding](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#restricted-producer-callback-binding). Public/local writers, absent bindings, callback reuse, stale admission and mismatches reject with `ASEVD410`. The factory accepts no solution path, executable arguments, transport, reporter, worker or capability parameter. Its internal adapter obtains solution, tool and test-output roots from the actual authenticated supervisor and copies the protected planning diff. Caller cancellation is additional to the actual captured stage token.

The entire restricted run, retained report collection, private staging, protected ReportGenerator merge, numeric gate and cleanup is registered with the existing lifecycle before its task is returned. Ignoring that task cannot remove it from owned stop/join. The subject command and protected reporter share one run-wide received-output quota; artifact writes use the existing retained writer and artifact quota. Assertion output requires both the fixed restricted procedure and its declared numeric gate to pass.

The CLI uses this single implementation. The Aspire host can reference the same package and bind it through its protected lifecycle; the factory itself supplies neither application readiness nor a restricted application lease. Core unit tests use intentionally internal fake transports to check procedure behavior and establish no native or Trusted proof. Production acceptance remains closed as recorded in the [consumer acceptance requirements](../../docs/evidence/issue779-consumer-acceptance.md).
All coverage orchestration types are internal and visible only to first-party assemblies. Consumers should use the documented public [coverage commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) and [EvidenceHost workflow](../../start-here/evidencehost.md).

The [coverage storage guide](../../guides/coverage-storage.md) documents the in-progress retention contract shared by CLI and Evidence coverage producers. Its `managed` default permits removal only of verified redundant collector XML after canonical publication; `full` retains it. A CLI run using `--no-clean` preserves prior artifacts in either mode, so retained output can include files from earlier invocations. The Evidence declaration's additive `coverageArtifactRetention` property defaults to `managed`; older JSON omitting it remains valid. These docs describe the branch implementation and do not claim an installed package proof or measured storage savings.

### Native operation verification

Artifact safety keeps managed decisions and native acceptance separate. Internal,
per-call observations cover file-state decoding, private Unix ownership/mode
checks, Windows rename-buffer encoding, lock-result classification, and
filesystem/mount classification. Windows access policy uses normalized SID and
access-mask facts; candidate-file decisions retain explicit handle ownership and
reject nonregular or unrepresentable sizes before returning a stream. Candidate
handles remain owned by the inspector until a stream is successfully returned;
metadata failures release them on every unwind path. Production defaults call the host APIs and retain
normal no-follow path, identity, and publication checks. The observations are test
seams, not consumer configuration; they never establish another operating system's
ABI or filesystem guarantees. Native acceptance still runs on the supported
Windows/NTFS, macOS/APFS, and Linux/ext4 lanes described in the
[storage validation record](../../docs/designs/coverage-artifact-storage-validation.md).

Observation state uses a single bounded publication slot per private observation
directory. Writers hold that directory's exclusive operation lease while they
reconcile the known slot, flush the new state, and atomically replace the current
state. A failed or interrupted write can therefore retain at most one known slot;
unknown files and changed identities are preserved for inspection. Registry input
validation rejects malformed observation and expiry-receipt entries before they
reach status or maintenance, keeping optional metadata failures separate from the
primary coverage outcome. See the [storage recovery guide](../../guides/coverage-storage.md#recovery-recipes).

The CLI's internal storage-command base renders schema-v1 operation data into human
and JSON results using the [storage result contract](../../guides/coverage-storage.md#status-json-and-exit-behavior).
Its optional domain identifier observation returns `domain-unknown` for nonfatal
failures so rendering retains the primary result and safe diagnostic. Gate commands
validate input and destination policy before acquiring output coordination; invalid
inputs therefore do not create report directories or contend with active writers.
Human numeric fields accept JSON numbers only; absent, differently typed, or
unrepresentable values use the field's documented zero or unknown fallback. This
keeps presentation of partial results from throwing after the operation has
already recorded its outcome and side effects. Operation implementations still
validate their own metadata before producing this result; presentation fallbacks
do not certify an unknown inventory or make an incomplete result successful.

Current-run sidecar publication cleans up its own staging file on failure in both
collection and independent gate publication. Cleanup requires the staged object's
identity to match; a replaced entry is preserved and cleanup failure retains the
original diagnostic. This is separate from private observation-state staging above.

Secure Unix opens share an internal native-flag selector for Darwin, Linux x64,
and Linux ARM64. [Linux ARM64 native open flags](https://github.com/torvalds/linux/blob/master/arch/arm64/include/uapi/asm/fcntl.h) use different directory-only and no-follow bits
from x64; output traversal, collector artifact reads, and storage inspection must
use the matching pair. Unverified Linux architectures fail before opening a
candidate. The selector has decision tests, while the native no-follow tests in
the [platform matrix](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml) verify actual host behavior.

Unix metadata calls select libc symbols for the process ABI because callers read
native `stat` and `statfs` structures at fixed offsets. Intel Darwin dispatches both
`fstat` and `fstatfs` to their `$INODE64` symbols; Darwin arm64 and Linux use the
unsuffixed symbols. Dispatch follows the current OS and process architecture. Callers
must still provide a sufficiently large writable buffer and interpret only the layout
for that host. The [XNU `stat.h` declarations](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h)
define Darwin's inode-versioned symbol mapping; [XNU `mount.h`](https://github.com/apple/darwin-xnu/blob/main/bsd/sys/mount.h)
defines the 64-bit-inode `statfs` structure, and [XNU's syscall table](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/syscalls.master)
distinguishes the legacy and 64-bit `fstatfs` interfaces. Injected dispatch tests cover
Intel and arm64 routing on any host; native tests validate the running host's call and
layout. They do not replace native Intel Darwin validation.

Recovery invoked by an existing coverage session reuses that session's live exclusive output lease only when its physical identity matches the stored registration. Shared, disposed or unrelated leases grant no recovery authority. The manager keeps ownership of any separately acquired leases and never releases a borrowed session lease. See the [storage lifecycle and recovery guide](../../guides/coverage-storage.md#recovery-recipes).

On Windows, deletable archive and staging candidates coordinate through one empty private `.appsurface-coverage-candidates.lock` file in the domain root. A deterministic byte-range offset derived from the candidate's physical identity keeps independent candidates concurrent. The file stays zero-length because locking beyond its end does not allocate payload storage. Source-output leases keep their existing protocol. Every candidate operation verifies both retained directory and lock-file identities; a hash collision causes safe contention. The fixed file is never removed to force admission. See [Windows byte-range locking](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex).

Applied maintenance also reconciles missing, unpinned historical archive registrations after an interrupted expiry. It verifies absence through a retained private archive parent under the store metadata lease, records a bounded missing-archive receipt, and clears only stale archive accounting. It never deletes an existing entry or reports reconciliation as a new physical deletion. Current, pinned and pending-reservation registrations are excluded. See the [recovery guide](../../guides/coverage-storage.md#recovery-recipes).

The internal `CoverageFileSystemInterop.WindowsCreateFile` boundary normalizes an absolute filesystem path and uses the Windows extended drive or UNC spelling before `CreateFileW`. This supports deeply nested collector output beyond legacy `MAX_PATH`; see [Windows path length limits](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation). Path spelling does not grant access: callers retain the same containment, reparse-point, file-kind, identity and private-access checks. Relative navigation must be normalized before the extended prefix is applied.
