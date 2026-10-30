# ForgeTrust.AppSurface.Evidence.Coverage

`ForgeTrust.AppSurface.Evidence.Coverage` is a private implementation package shared by the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) and its first-party [Evidence workflow](../ForgeTrust.AppSurface.Evidence.Cli/README.md). It ships only as a transitive support package; it is not a consumer extension point, direct-install package, or separately supported integration package.

The assembly owns one in-process implementation of coverage discovery, execution, watchdog supervision, merge, gate evaluation, patch analysis, and controlled artifact writing. The CLI owns command parsing and presentation; Evidence owns policy and claim translation. This direction ensures Evidence produces coverage claims from the same execution engine as `appsurface coverage run` and `appsurface coverage gate`, without invoking a second process. Both surfaces use the fail-mode default. For VSTest test phases with at least 90 seconds remaining, the engine requests no-dump hang blame and reserves part of the current budget for sequence output and summary. Evidence's producer deadline remains authoritative and includes discovery/build time. The [CLI hang-diagnostics reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) documents the timeout arithmetic, opt-outs, artifact statuses, and migration for healthy long-running tests. Evidence results never include test names or Sequence.xml contents.

All coverage orchestration types are internal and visible only to first-party assemblies. Consumers should use the documented public [coverage commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) and [EvidenceHost workflow](../../start-here/evidencehost.md).

The [coverage storage guide](../../guides/coverage-storage.md) documents the in-progress retention contract shared by CLI and Evidence coverage producers. Its `managed` default permits removal only of verified redundant collector XML after canonical publication; `full` retains it. A CLI run using `--no-clean` preserves prior artifacts in either mode, so retained output can include files from earlier invocations. The Evidence declaration's additive `coverageArtifactRetention` property defaults to `managed`; older JSON omitting it remains valid. The [domain identity validation record](../../docs/plans/issue-899-progress.md) records the installed CLI domain-lifecycle proof and its scope. No universal storage-saving percentage is promised.

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

### Store domain identity lifecycle

The ordinary Windows component walk tolerates only native ERROR_ALREADY_EXISTS (183) after a missing-name probe. It then reopens the competing object without following reparse points and validates its kind, retained identity and required private ACL; it never changes existing permissions. This idempotent acquisition is distinct from `CreateArtifactDirectory`, whose fresh-destination no-replace refusal remains strict. Its optional `onCreated` callback runs immediately after native creation, before later validation, so callers can preserve accurate partial-state diagnostics. The `beforeWindowsCreate` acquisition seam is per call and exists only to force a competing native creator in tests. Managed error-contract simulations do not prove Windows admission or ACL behavior.

The schema-v1 store marker records the current host's live directory identity tuple so later opens can compare the retained directory with the persisted marker. `CoverageArtifactStore.Open` and `OpenReadOnly` each accept `Func<CoverageRunOutputLease, string>? observeIdentity = null` as an optional internal seam for deterministic comparison tests. When supplied, it changes only the value compared with the marker: native path traversal, no-follow checks, retained-handle binding, and private-access checks still run. An injected mismatch is simulated managed evidence; it does not demonstrate native remount or restart behavior.

`CoverageArtifactStore.CreateNewDomain(destination, apply, cancellationToken, beforeRegistryPublication = null, onDirectoryCreated = null, beforeDestinationInspection = null, beforeDestinationReopen = null)` is the internal fresh-domain boundary used by the CLI. It requires an absolute destination below an existing safely acquired parent. With `apply: false`, it validates and returns the normalized destination without writing; trailing directory separators are removed before deriving the parent and final component. With `apply: true`, it atomically creates a new private root without replacing an existing path, initializes a fresh schema-v1 marker and empty registry, and does not inspect or migrate the configured store. Existing empty directories and existing domains are refused. The optional per-call `beforeRegistryPublication` action is an interruption-test seam after marker/child initialization and before registry creation; production CLI calls omit it. The optional `beforeDestinationInspection` callback forces parent-replacement races after native parent retention; production omits it. The optional per-call `beforeDestinationReopen` callback runs after the attributes probe and before the retained-directory reopen so tests can force a competing destination; production omits it. The optional onDirectoryCreated observation runs immediately after successful native root creation, before subsequent validation can fail; the CLI uses it to report partial effects accurately. Pre-creation refusal reports no effects. Cancellation or a later initialization error preserves any created private state for inspection; callers must select a different fresh path on retry. The CLI does not print the raw destination, so operators should explicitly configure the same selected fresh path after successful creation. See the [canonical identity recovery contract](../../guides/coverage-storage.md#replacing-a-domain-whose-identity-no-longer-matches).

Store-open diagnostic reasons are `MarkerMissing`, `MarkerInvalid`, `MarkerSchemaUnsupported`, `IdentityMismatch`, `VolumeIdentityMismatch`, `RegistryInvalid`, `PolicyInvalid`, and `PrivateAccessRequired`; unsupported native capability is reported as `CapabilityUnavailable`. They explain the failed validation category without exposing persisted identifiers or authorizing repair. Schema-v1 mismatches remain refused. Matching the file-ID field while the device/volume field differs cannot establish continuity. No native persistent-ID format, schema migration, marker rewrite, or ownership transfer is implemented.

For issue #899, six native lifecycle tests passed on macOS ARM64 for current-mount close/reopen identity stability, cloned-file object identity and copy-on-write separation, replacement of the leased root or ancestor, and linked-root refusal. A separate disposable APFS disk-image experiment showed that detach/remount alone preserved the device value on this host. After a second temporary APFS image occupied the original device number, remounting the first image at the same path changed native `st_dev` from `16777239` to `16777243` while `st_ino` remained `16`. The source-built CLI refused the initialized schema-v1 marker as `VolumeIdentityMismatch`, with unavailable data and no side effects. `coverage storage new-domain` preview and apply succeeded while the old mismatched domain remained selected. Old marker and registry hashes were recorded after the experiment. This demonstrates one possible device-reassignment path on this macOS host, not the original issue's cause or cross-process restart continuity. The six lifecycle tests close and reopen handles within one process. Those initial local observations did not include Windows or Linux execution. The later [native CI observations](../../docs/designs/issue-899-domain-identity.md#evidence-and-limits) record the passing Windows, Ubuntu and macOS coverage-security and EvidenceHost lanes at c281c2ad, including Windows ancestor-rename prevention. These results predate the local PR-feedback fixes; the added Windows component-creation race tests still need native execution. See the [issue #899 design/evidence record](../../docs/designs/issue-899-domain-identity.md) for the platform identity evaluation and limits.
