# ForgeTrust.AppSurface.Evidence.Coverage

`ForgeTrust.AppSurface.Evidence.Coverage` is a private implementation package shared by the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) and its first-party [Evidence workflow](../ForgeTrust.AppSurface.Evidence.Cli/README.md). It ships only as a transitive support package; it is not a consumer extension point, direct-install package, or separately supported integration package.

The assembly owns one in-process implementation of coverage discovery, execution, watchdog supervision, merge, gate evaluation, patch analysis, and controlled artifact writing. The CLI owns command parsing and presentation; Evidence owns policy and claim translation. This direction ensures Evidence produces coverage claims from the same execution engine as `appsurface coverage run` and `appsurface coverage gate`, without invoking a second process. Both surfaces use the fail-mode default. For VSTest test phases with at least 90 seconds remaining, the engine requests no-dump hang blame and reserves part of the current budget for sequence output and summary. Evidence's producer deadline remains authoritative and includes discovery/build time. The [CLI hang-diagnostics reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) documents the timeout arithmetic, opt-outs, artifact statuses, and migration for healthy long-running tests. Evidence results never include test names or Sequence.xml contents.

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
Human numeric fields accept JSON numbers only; absent, differently typed, or
unrepresentable values use the field's documented zero or unknown fallback. This
keeps presentation of partial results from throwing after the operation has
already recorded its outcome and side effects. Operation implementations still
validate their own metadata before producing this result; presentation fallbacks
do not certify an unknown inventory or make an incomplete result successful.

Secure Unix opens share an internal native-flag selector for Darwin, Linux x64,
and Linux ARM64. [Linux ARM64 native open flags](https://github.com/torvalds/linux/blob/master/arch/arm64/include/uapi/asm/fcntl.h) use different directory-only and no-follow bits
from x64; output traversal, collector artifact reads, and storage inspection must
use the matching pair. Unverified Linux architectures fail before opening a
candidate. The selector has decision tests, while the native no-follow tests in
the [platform matrix](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml) verify actual host behavior.

Recovery invoked by an existing coverage session reuses that session's live exclusive output lease only when its physical identity matches the stored registration. Shared, disposed or unrelated leases grant no recovery authority. The manager keeps ownership of any separately acquired leases and never releases a borrowed session lease. See the [storage lifecycle and recovery guide](../../guides/coverage-storage.md#recovery-recipes).

On Windows, deletable archive and staging candidates coordinate through one empty private `.appsurface-coverage-candidates.lock` file in the domain root. A deterministic byte-range offset derived from the candidate's physical identity keeps independent candidates concurrent. The file stays zero-length because locking beyond its end does not allocate payload storage. Source-output leases keep their existing protocol. Every candidate operation verifies both retained directory and lock-file identities; a hash collision causes safe contention. The fixed file is never removed to force admission. See [Windows byte-range locking](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex).

Applied maintenance also reconciles missing, unpinned historical archive registrations after an interrupted expiry. It verifies absence through a retained private archive parent under the store metadata lease, records a bounded missing-archive receipt, and clears only stale archive accounting. It never deletes an existing entry or reports reconciliation as a new physical deletion. Current, pinned and pending-reservation registrations are excluded. See the [recovery guide](../../guides/coverage-storage.md#recovery-recipes).

The internal `CoverageFileSystemInterop.WindowsCreateFile` boundary normalizes an absolute filesystem path and uses the Windows extended drive or UNC spelling before `CreateFileW`. This supports deeply nested collector output beyond legacy `MAX_PATH`; see [Windows path length limits](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation). Path spelling does not grant access: callers retain the same containment, reparse-point, file-kind, identity and private-access checks. Relative navigation must be normalized before the extended prefix is applied.
