# Coverage artifact storage: adoption guide

> **Verification status.** This branch implements shared coverage history, storage commands, warnings, **--artifact-retention managed|full**, **coverage merge --no-clean**, and the additive Evidence **CoverageArtifactRetention** property. Local source tests and manager resource trials are recorded in the [implementation validation record](../docs/designs/coverage-artifact-storage-validation.md); installed upgrade and supported-platform release evidence must be complete before claiming release readiness. Example output below is illustrative.

This guide explains how an AppSurface upgrade is intended to manage its own coverage artifacts across worktrees while leaving current reports readable at their established paths. It also gives consumers a way to find direct test lanes that collect coverage implicitly. Storage warnings and cleanup are separate from coverage quality: keep the existing [coverage run](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run), [coverage merge](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-merge), and [coverage gate](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) configuration, exclusions, metrics, and thresholds.

## Quickstart: upgrade, inspect, and use

After a release containing this feature is available, update the pinned CLI through the repository's normal tool-manifest and release process. For example, a repository that tracks prereleases may use:

~~~sh
dotnet tool update ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface coverage storage status
dotnet tool run appsurface coverage storage inspect --root .
dotnet tool run appsurface coverage storage maintain
~~~

The first status call reports the effective user-local domain, policy, observed totals and whether accounting is complete. It does not create a missing domain or probe write capabilities. Native capabilities are verified when a participating write opens the store; a read-only status may report them as unknown. Inspect adds a bounded scan of the named repository; it observes unmanaged paths without registering or deleting them. Maintain without --apply previews eligible history. Review the candidate list, then pass --apply to expire eligible manager-owned history. It does not sweep TestResults or current output.

After reviewing the preview, apply it explicitly:

~~~sh
dotnet tool run appsurface coverage storage maintain --apply
~~~

The canonical status spelling is coverage storage status. A CLI may also route the bare coverage storage group to status if its command router explicitly chooses that alias. Every storage operation accepts --json. Storage commands run without a terminal and never ask an interactive question.

Use the existing [coverage clean preview/apply workflow](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-clean) only when you intend to clean its marker-owned output. Storage history maintenance and worktree cleanup have different ownership and effects.

## What the manager owns

The manager tracks artifacts created or overwritten by participating AppSurface invocations. Each run record identifies the physical output, worktree, run, producing version, outcome, inventory, and accounting state. It does not infer ownership from a directory name, timestamp, remote URL, or matching XML filename.

The lifecycle is:

1. A run holds an exclusive lease while changing its output. A competing compatible writer to the same physical output fails before mutation; work in distinct outputs can proceed concurrently.
2. Current reports remain at their established uncompressed paths. The shared history budget never evicts current output.
3. Before a normal clean run or clean merge replaces the same output, AppSurface attempts one verified snapshot of the previous inventoried run.
4. A completed archive is published only after its manifest, original byte lengths, and SHA-256 hashes have been verified. Failed or interrupted runs retain their recorded outcome and the files actually inventoried; an incomplete run is not represented as a successful complete run.
5. Eligible unpinned archives may expire by age or budget. A restored archive is historical material, not fresh evidence for the current source revision.

Local provenance includes a fingerprint of a verified Git HEAD and a fingerprint of the observed working-tree state, including untracked files, alongside the caller’s coverage-policy fingerprint. Two bounded Git probes share a 500 ms deadline; unavailable, malformed, oversized or timed-out observations remain unknown. Raw Git status, paths and arguments are never stored in provenance. This informational snapshot does not certify source freshness.

Archive contents use lossless gzip payloads and original relative paths. The archive inventory is produced by participating artifact writers; arbitrary pre-existing files, source trees, links, special files, and external paths are excluded. A changing or unreadable inventoried file prevents complete archive publication. Failure to save history is secondary to the coverage run or gate result; it never makes incomplete evidence pass.

A standalone gate can extend an existing current inventory with its newly written reports. Before doing so, it verifies that the other inventoried files still have their recorded byte lengths and hashes. An older or external writer changing those files prevents the inventory update and emits [ASCOV201](#ascov201-store-degraded); it does not change the gate’s numeric result or adopt the changed bytes under the earlier run’s provenance.

## Retention modes and replacement boundaries

Coverage run and merge accept the exact selector **--artifact-retention managed|full**; the default is managed. For a clean coverage run, managed removes raw Cobertura duplicates only after the canonical report is published and byte-verified identical; full retains those raw copies. `--no-clean` skips that run cleanup. Coverage merge validates the selector but does not use it to prune its staged inputs: both managed and full retain every selected shard copied under `reportgenerator-input/`. Neither mode changes test selection, instrumentation, coverage configuration, exclusions, metrics, or gate thresholds. Coverage merge also accepts **--no-clean** with its existing preservation meaning.

The public Evidence declaration exposes the init-only string property **CoverageArtifactRetention**, serialized as **coverageArtifactRetention**. Omission defaults to managed; the accepted values are exactly managed and full. Evidence does not add a no-clean option. The following command shapes are illustrative; use the consumer's actual solution, projects, inputs, and gates:

~~~sh
appsurface coverage run --solution ./MyApp.slnx --artifact-retention managed
appsurface coverage merge --source ./shards --output ./TestResults/coverage-merged --artifact-retention full --no-clean
~~~

| Invocation or setting | Prior-run snapshot | Current-invocation duplicate handling | Boundary |
| --- | --- | --- | --- |
| Clean coverage run with --artifact-retention managed | Attempt one verified snapshot immediately before existing cleanup/replacement | Remove only raw Cobertura duplicates verified byte-identical to the published canonical report | Cleanup is best-effort; it does not change coverage results |
| Clean coverage run with --artifact-retention full | Same snapshot attempt | Preserve raw Cobertura duplicates | Full retains duplicates from this run, not all future history |
| Clean coverage merge with --artifact-retention managed or full | Same snapshot attempt | Retain every selected shard staged under `reportgenerator-input/`; the selector does not prune these public-contract inputs | Both values have the same staged-input retention behavior |
| Run or merge with --no-clean | No automatic prior-run snapshot | Skip pre-run cleanup; run preserves raw duplicates, while merge retains its staged inputs in either retention mode. Selected canonical files may still be replaced. | Preservation mode is not an immutable backup |
| Standalone coverage gate | No automatic archive of the coverage run | Gate report updates extend the current inventory under the output lease | A gate cannot infer that external readers are finished |
| First-party Evidence coverage | Same managed default through the existing declaration; full is an optional declaration value | Same artifact policy; no new Evidence no-clean option | Nested run and gate reuse the same execution context; see the [EvidenceHost guide](../start-here/evidencehost.md) and [private coverage engine guide](../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md) |
| Explicit coverage clean | Does not create a snapshot | Retains its existing authorized deletion behavior and reports whether a verified archive already exists | Cleanup does not promise to save history |
| Explicit coverage clean --all | Does not create a snapshot | Broad worktree sweep remains an explicit action and may include outputs from nonparticipating writers | Never run automatically |
| coverage storage maintain | Not applicable | Preview only by default; --apply deletes eligible owned history only | Current, pinned, unknown, and unmanaged data are skipped |
| coverage storage pin --run ID | If ID resolves to current output, create and verify an archive first | Protects the archive after verification | A pin protects an archive, not a future replacement of the current path |
| coverage storage unpin --run ID | Not applicable | Removes the pin from a verified archive | It may become eligible on the next maintenance pass; no grace period is promised |
| coverage storage restore --run ID --output DIR | Not applicable | Restores to a fresh destination using private staging and atomic no-replace publication | Never overwrites current or partial output |

Managed coverage run retains canonical per-project and top-level Cobertura reports, project and run manifests, summaries, gate reports, timings, JUnit, logs, VSTest sequences and attachments, and ReportGenerator output required by consumers while removing only verified raw duplicates. Coverage merge retains all selected public-contract inputs staged under `reportgenerator-input/` in both modes; managed does not prune merge copies. Savings depend on the actual artifacts, and merge has no guaranteed percentage reduction, including no universal 50% reduction. The [PackageIndex consumer proof](../tools/ForgeTrust.AppSurface.PackageIndex/README.md) is a compatibility boundary for staged inputs and project manifests.

A normal clean run can continue if its best-effort snapshot exceeds a limit, runs out of space, or otherwise fails. In that case, emit the history-not-saved diagnostic, publish no completed archive, and do not promise recovery of the replaced files. The existing primary run or gate outcome remains authoritative.

## Storage commands

The commands below are implemented on this branch. Installation and release compatibility remain subject to the [validation record](../docs/designs/coverage-artifact-storage-validation.md).

| Command | Reads or changes | Contract |
| --- | --- | --- |
| coverage storage status [--json] | Reads the current domain and registrations | Reports policy, current/history/pinned/protected/unmanaged observations, counts, completeness, limits, and management capability. No mutation. |
| coverage storage inspect --root DIR [--json] | Reads the named root and directly registered outputs | Cancellable bounded inspection, including unmanaged TestResults. It neither adopts files nor deletes them. |
| coverage storage maintain [--json] | Reads eligible archive inventory | Preview by default. Lists eligible candidates, protected/corrupt skips, and remaining work. |
| coverage storage maintain --apply [--json] | Deletes eligible archived history | Revalidates each candidate under its exclusive lease. It never adopts or removes current or unmanaged data. Partial deletion is reported with exact completed effects. |
| coverage storage restore --run ID --output DIR [--json] | Reads one verified archive and writes a new destination | Destination must be fresh and non-linked. Validate first, extract to a private sibling stage on the destination volume, verify every file, then publish atomically without replacing an existing path. |
| coverage storage pin --run ID [--json] | Reads current output or a verified archive; updates archive protection | Resolve the run through the registry. If it is current, snapshot and verify it before marking the archive pinned. |
| coverage storage unpin --run ID [--json] | Updates archive protection | Reports the actual final protection state. Unpinned history may expire at the next maintenance pass. |

An explicit coverage storage maintain --apply is the history-expiry control. It is not a replacement for [coverage clean](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-clean), which targets local worktree outputs under its own ownership rules.

## User-local policy and fixed limits

The shared domain lives under the user's local application-data area at ForgeTrust/AppSurface/artifacts/v1, outside the repository. APPSURFACE_ARTIFACT_HOME may select a separate domain for an isolated local or CI environment; it receives its own registry and budget. The policy file is policy.json in that validated domain.

Policy is user-local. A repository-level setting cannot override the policy for the user or another repository. Do not add repository-level storage settings. A different CI budget requires an explicitly selected separate domain, not a checked-in repository override.

The policy.json schema is version 1:

~~~json
{
  "schemaVersion": 1,
  "historyMaxBytes": 2147483648,
  "historyMaxAgeDays": 7,
  "repositoryWarningBytes": 1073741824,
  "domainWarningBytes": 2147483648,
  "warningCooldownHours": 24
}
~~~

Omitted fields use the defaults below. Byte fields accept checked nonnegative integers no greater than 1 TiB; warning thresholds must be positive. historyMaxBytes: 0 disables new automatic history and makes eligible unpinned history subject to expiry; it does not remove current, pinned, unknown, or unmanaged data. Age is 1–365 days. Warning cooldown is 1–168 hours.

| Policy field | Default | User-editable range or meaning |
| --- | ---: | --- |
| historyMaxBytes | 2 GiB (2147483648) | 0 through 1 TiB; shared target for eligible unpinned archive and metadata bytes |
| historyMaxAgeDays | 7 | 1–365 days since archival |
| repositoryWarningBytes | 1 GiB (1073741824) | Positive integer through 1 TiB |
| domainWarningBytes | 2 GiB (2147483648) | Positive integer through 1 TiB |
| warningCooldownHours | 24 | 1–168 hours |

Routine scans persist a bounded observation cursor so later commands inspect pending directories before revisiting the same branch. The cursor retains at most 128 relative directories per scope and 32 recent scopes in 64 KiB, expiring after seven days. It grants no ownership; resumed paths are checked again for links and nested repositories, and continued results remain lower bounds rather than accumulated totals. `coverage storage inspect` starts a fresh explicit scan.

The shared history budget is a target, not a machine-wide hard cap. Current outputs, pinned archives, protected/unknown data, and unmanaged material are counted separately and are not evicted to meet it. Eligible history expires by age or sooner under byte pressure; oldest eligible successful runs expire before failed or interrupted runs. A finite budget cannot guarantee that every failed run remains forever. Pin a verified archive when investigation or release evidence must survive normal expiry.

The following bounds are fixed safety/resource limits and are not user-configurable policy:

| Bound | Limit |
| --- | ---: |
| Routine repository inspection | 1 second and 10,000 entries per invocation, cumulative across preflight and terminal inspection |
| Routine maintenance | 5 seconds cumulative per invocation, including metadata waits, enumeration, compression, verification, staging cleanup, and eviction |
| Automatic snapshot | 256 MiB original bytes and 256 MiB staging bytes |
| Shared staging reservations | 512 MiB across the domain, including crash remnants |
| Explicit pin or restore | 64 GiB expanded bytes and 10,000 files, subject to manifest, metadata, and staging limits |
| Registrations | 10,000 records or 16 MiB total metadata |
| One run inventory | 10,000 files or 2 MiB |
| Warning state | 1,000 conditions or 1 MiB |
| Expiry receipts | 10,000 entries or 4 MiB; receipts at most seven days old |
| Registration overflow observation | 64 KiB and 32 recent observations, included in the 16 MiB metadata allowance |

A snapshot over an automatic limit is skipped as a whole; it does not limit collection or change gate results. At registry or inventory capacity, optional registration/history publication is skipped with an explicit warning. Active, current, or pinned ownership metadata is never dropped to make room. Overflow observations are lower bounds, not additive exact totals; repeated scans must not be counted twice. A capacity warning is due even when the observed lower bound is below the usual growth threshold.

The 512 MiB reservation covers history-store staging and crash remnants. Restore stages beside the fresh destination on its volume and accounts for that space separately from history-store reservations; the 64 GiB expanded-byte and 10,000-file bounds still apply.

Registry publication uses one private staging slot bounded to 16 MiB under the exclusive metadata lease. A write failure cleans only that slot’s matching file identity; explicit or routine maintenance reconciles a crash remnant before further history work. A snapshot reservation created before its stage directory is released only after exclusive source authority proves the snapshot is inactive and the recorded identities and reservation still match. Unknown stages and unmatched reservations remain preserved and accounted for.

## Status, JSON, and exit behavior

Status separates registered current bytes, total archive bytes, pinned archive bytes, protected bytes, metadata, staging reservations, and incomplete/overflow accounting. History includes pinned archives; pinned bytes are an overlapping category, not an extra amount to add to history. Unmanaged files are measured by inspect rather than invented as a zero in status. The response identifies observation scope, timestamp, and completeness. A partial scan is a lower bound, never a fresh zero. Status displays the effective policy values and local policy path. Unknown or unavailable values must remain unknown rather than be represented as zero.

JSON schema version 1 includes:

- schemaVersion, operation, and status
- opaque domain identity and, when applicable, safe run/output identifiers
- observation timestamp, scope, and completeness
- byte and count categories, limits, and reasons data is protected or skipped
- stable diagnostic records and recovery guidance
- sideEffects, reconciliation guidance when the result is uncertain, and maxRetries

Allowed status values are complete, partial, cancelled, failed, and unsupported. sideEffects is none, partial, complete, or unknown. Commands must work without a TTY and never prompt.

The following status shape is illustrative. Byte categories and limits are returned under data; unknown observations remain unknown.

~~~json
{
  "schemaVersion": 1,
  "operation": "status",
  "status": "complete",
  "domainId": "opaque-user-local-id",
  "observedAt": "illustrative timestamp",
  "observation": {
    "scope": "registered-domain",
    "complete": true
  },
  "data": {
    "currentBytes": 0,
    "historyBytes": 0,
    "pinnedBytes": 0,
    "protectedBytes": 0,
    "metadataBytes": 0,
    "stagingReservedBytes": 0,
    "historyMaxBytes": 2147483648,
    "historyMaxAgeDays": 7
  },
  "diagnostics": [],
  "sideEffects": "none",
  "recoveryAction": null,
  "reconcileAction": null,
  "maxRetries": 1
}
~~~

Explicit-operation exit codes are 0 for success, 1 for an operation failure or incomplete/unsupported status or inspection, 2 for invalid input, and 130 for caller cancellation. Status or inspection succeeds when its requested read completes. Read-only status reports unprobed native write capability as unknown; a read does not establish write support. Installed command-host verification is part of the release evidence. Automatic storage diagnostics never replace the existing run, merge, gate, or Evidence exit code.

A busy explicit operation makes one nonblocking lease attempt and reports waitMs=0; it never waits indefinitely. Retry once after the named operation finishes, or choose a fresh output where the destination is involved. Reconcile first when side effects are partial or unknown. maxRetries is 1. After one retry, report the specific unresolved resource condition for the caller to resolve; do not ask an interactive question.

## Warnings and diagnostic anchors

Warnings go to stderr with a short linked summary; --json returns bounded structured diagnostics. Each record includes a stable code, operation, safe identifiers, precise cause, observed and allowed values where relevant, effective version and policy, environment-variable names without their values, side effects, a safe fix, retry/reconciliation guidance, and a bounded sanitized trace or private trace reference. Diagnostics never grant deletion authority.

Routine inspection is limited to known coverage/test-output locations in the current repository and directly registered outputs. It skips Git metadata, build dependencies, links, special files, and nested repositories; it does not scan the home directory or arbitrary worktrees. Effective collector inspection uses the selected project, framework, configuration, and recognized invocation overrides. It identifies a configured enabled collector as a risk, not proof that a given test invocation actually collected coverage.

| Code | Meaning |
| --- | --- |
| [ASCOV200](#ascov200-history-not-saved) | A best-effort history snapshot was not completely saved. |
| [ASCOV201](#ascov201-store-degraded) | The optional private store is unavailable, unsafe, or degraded. |
| [ASCOV202](#ascov202-operation-busy) | A conflicting compatible reader or writer holds the output/archive lease. |
| [ASCOV203](#ascov203-inspect-incomplete) | Inspection is partial or incomplete; totals are lower bounds. |
| [ASCOV204](#ascov204-maintain-partial) | Maintenance stopped with eligible work remaining or partial effects. |
| [ASCOV205](#ascov205-restore-failed) | Restore did not publish a verified completed destination. |
| [ASCOV206](#ascov206-pin-state) | A pin/unpin operation could not establish the requested final protection state. |
| [ASCOV207](#ascov207-metadata-capacity) | A registration, inventory, warning, receipt, or overflow bound was reached. |
| [ASCOV208](#ascov208-invalid-storage-input) | An explicit storage-command input is invalid. |
| [ASCOV209](#ascov209-implicit-collector-risk) | Effective settings configure a collector that may collect during routine tests. |
| [ASCOV210](#ascov210-storage-growth) | Observed storage meets a repository or domain warning threshold. |
| [ASCOV211](#ascov211-settings-inspection-unavailable) | Effective settings could not be determined safely from configuration or invocation overrides. |

Warnings repeat at most once per condition per 24 hours, except after at least 25% and 256 MiB growth or a new inspection failure. The default growth thresholds are 1 GiB per repository or 2 GiB across the registered domain. Incomplete scans report a lower bound and capacity warning rather than presenting zero or hiding uncertainty.

### ASCOV200: History not saved

The old output is about to be replaced, but no complete verified history archive is available. This warning does not change the primary coverage result. If normal replacement proceeds, the replaced history may be unrecoverable. Use an explicit pin before replacement when preserving the exact run matters; pin succeeds only after a complete verified archive exists.

### ASCOV201: Store degraded

Optional storage management is disabled or reduced because the store is read-only, busy, corrupt, full, unsafe, on unsupported storage, or has failed a privacy/capability check. Required coverage work continues when its output can still be prepared. Correct the named user-local store issue and check status again. The manager must not silently create an unmanaged fallback store or repair arbitrary permissions.

### ASCOV202: Operation busy

A compatible operation already holds the needed output or archive lease. No mutation has started when a same-output writer conflict is found. Wait for the reported operation to finish and retry once, or select a dedicated output. Do not remove a lock file or infer inactivity from a PID or timestamp.

### ASCOV203: Inspect incomplete

The traversal or time budget ended, cancellation occurred, or one or more paths could not be inspected. Treat observed bytes as a lower bound. Restore access or rerun inspect once for the same root; inspection never adopts or deletes the discovered data.

### ASCOV204: Maintain partial

Maintenance applied only the reported completed deletions, skipped protected/corrupt candidates, or stopped on cancellation/denial. Read the result and run status to reconcile what remains before one retry. Current output and unmanaged files are never maintenance candidates.

### ASCOV205: Restore failed

Validation, extraction, integrity checks, cancellation, or atomic publication failed. No completed destination should be presented as restored. The result reports whether a proven-owned stage remains or publication is uncertain. Reconcile with status, then retry once to a different fresh destination; never overwrite a partial or current directory.

### ASCOV206: Pin state

Pin or unpin did not reach the requested verified final state. A failed pin does not protect the run. Check status for the registry and archive state, address the named cause, then retry once. Unpinning makes a verified archive eligible at the next maintenance pass.

### ASCOV207: Metadata capacity

A bounded registry or metadata category reached its count or byte limit. The manager preserves active/current/pinned ownership and does not discard it to admit more records. Status reports incomplete accounting and bounded overflow observations; inspect the current repository to refresh its lower bound.

### ASCOV208: Invalid storage input

An explicit coverage storage command argument failed validation. The result identifies the field and allowed form without echoing secret-like input. Run identifiers accept exactly 32 hexadecimal characters without hyphens; restore also requires a nonempty `--output` naming a fresh destination directory. Correct the named option before retrying. Unknown policy schema or invalid policy values must disable optional management without preventing required coverage; they are reported as ASCOV201 when opening the optional store. ASCOV208 identifies invalid command arguments before the store is opened.

### ASCOV209: Implicit collector risk

The effective project settings configure an enabled XPlat collector for a routine test path. This indicates a configuration risk only; it does not prove every test ran or produced a report. Follow [the migration example](#migrate-inherited-collector-settings) to select disabled settings for routine lanes and preserve the enabled settings for the explicit required lane.

### ASCOV210: Storage growth

Observed repository or registered-domain bytes meet the configured warning threshold. The diagnostic reports scope, counts, age, completeness, and largest sanitized relative paths, then points to inspect or an ownership-specific retention decision. It does not authorize cleanup.

### ASCOV211: Settings inspection unavailable

A settings path is missing, unreadable, unsafe to parse, or overridden by conflicting/uninterpretable test arguments. The diagnostic must preserve the original test arguments and say that collector execution is unknown; it must not claim that collection occurred. Resolve the effective settings or provide an unambiguous explicit override, then inspect again.

## Migrate inherited collector settings

An AppSurface upgrade cannot change a direct dotnet test invocation or edit a consumer's checked-in settings. If Directory.Build.props supplies a RunSettingsFilePath, direct tests may inherit an enabled XPlat collector unless their actual invocation overrides it. Different test lanes can use different effective settings; inspect the selected project and invocation rather than assuming a single repository-wide result.

Keep two settings choices:

1. Preserve the existing required-coverage settings file and all of its collector configuration, including every Include, Exclude, ExcludeByFile, format, and other project-specific value.
2. Create a routine-test variant from that file. Change only the collector's enablement to `enabled="false"`, as specified by the [VSTest collector configuration](https://github.com/microsoft/vstest/blob/main/docs/analyze.md#enabledisable-a-datacollector); retain all coverage filters and other settings unchanged. Pass that file explicitly to routine test invocations so it overrides the inherited enabled settings. `isEnabled` is not a supported runsettings attribute and causes VSTest to reject the settings file.
3. Keep the required coverage lane explicitly pointed at the original enabled settings. Preserve its coverage command, exclusions, artifact readers/uploads, line and branch metrics, thresholds, and failure behavior byte-for-byte unless a separately reviewed consumer change requires otherwise.
4. Compare the required lane's pre/post coverage metrics and confirm its existing gates still pass or fail for the same reasons. Run the exact existing gate command against the fresh canonical report from that required lane; a restored archive is not a fresh gate input. Do not broaden exclusions or lower any gate to make the migration succeed.

Representative command shape:

~~~sh
# Routine test: explicitly select the disabled variant; retain the lane's existing flags.
dotnet test tests/MyApp.Tests/MyApp.Tests.csproj \
  --configuration Release \
  --settings "$PWD/coverage-routine.runsettings" \
  --no-restore

# Required lane: retain the existing AppSurface invocation and explicitly select its
# original enabled settings file. Keep all existing filters, gates, output readers,
# and other arguments in place.
dotnet tool run appsurface -- coverage run \
  --solution ./MyApp.slnx \
  --coverage-driver collector \
  --test-argument "/p:RunSettingsFilePath=$PWD/coverage.runsettings"
~~~

This command shape illustrates explicit settings selection; adapt it to the consumer's existing required-lane command and preserve its other arguments exactly. Do not use this example to replace scripts/ci/run-coverage.sh, change a reader path, or claim that any storage command is installed. The pinned consumer baseline recorded an inherited enabled settings path, a stable managed output, a wrapper that reads canonical merged XML, and separate JUnit/log/timing readers. It also recorded direct test lanes that used their own settings override. These are static observations; no live post-upgrade run or storage improvement is claimed here.

## Ownership, privacy, and unsupported boundaries

- **Current:** Current AppSurface outputs remain in place and are outside shared history expiry. A later clean replacement attempts a snapshot at the existing replacement boundary. A gate does not make current output reclaimable.
- **Pinned:** A pin protects only a complete verified archive. Pinning current output first resolves the registered run and creates that archive; it does not pin the output path against future replacement.
- **Unknown:** Unknown manifest/schema versions, corrupt or changing inventories, active or stale-unknown leases, and older noncooperating writers never grant deletion authority. Preserve and report them.
- **Unmanaged:** Direct-test and legacy paths may be inspected within bounded roots, but inspection does not adopt or delete them. The first upgraded run does not retroactively archive or evict arbitrary old trees. Existing marker-owned output may be measured; a new compatible run manifest is needed for managed history.
- **Restored:** A restore is historical evidence with original hashes and provenance. It is not current-source attestation. The [Evidence coverage execution](../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md) must use only its newly executed run, never auto-discover a restored directory as current proof.
- **External readers:** External programs do not participate in AppSurface leases. Do not start a replacement run while another tool is reading the same output.

The store must be private to the user: on Unix, directories are mode 0700 and regular files mode 0600 owned by the effective user; on Windows, ACLs are restricted to that user plus necessary SYSTEM/administrator access. A custom APPSURFACE_ARTIFACT_HOME receives the same validation. The manager may safely create missing private components, but must not chmod, chown, or rewrite ACLs on an arbitrary supplied path. Unsafe permissions disable optional management and produce a cause-specific diagnostic.

Automatic management is intended only for validated user-local regular-file storage on Windows/NTFS, macOS/APFS, or Linux/ext4 after native lock, identity, no-follow, and same-volume atomic-publication checks pass. Linked roots, network shares, unrecognized filesystems, and failed capability checks disable automatic archive/deletion. Required coverage filesystem rules remain authoritative.

Warning and inspection state each use one fixed private publication slot under an exclusive observation-directory lease. Interrupted publication can retain that bounded slot for identity-checked recovery; it cannot admit a growing series of uniquely named temporary files. Unknown files and unsafe slots remain preserved for inspection. Invalid registry observation or expiry-receipt entries disable optional management before status or maintenance consumes them; required coverage retains its primary result.

Diagnostics and traces are bounded and private. Sanitize paths, controls, nested causes, and traces; use repository-relative paths where possible and a <home> marker for user prefixes. Never print raw source exception text, credentials, secret-like values, test contents, process arguments, or environment values. Environment names and set/unset status may be shown. A private trace reference is safe only when the private write succeeded; report trace-write failure without exposing the underlying text. No outbound telemetry is added.

## Recovery recipes

- **History was not saved:** keep the coverage result as reported, but do not assume the replaced files can be restored. Before the next replacement, pin the registered run and verify success. If the automatic snapshot exceeded its limit, explicit pin/restore still obeys the 64 GiB expanded and shared staging limits.
- **A run reports busy:** allow the named compatible operation to finish and retry once, or use a dedicated output. Never delete its lock.
- **Inspect is partial:** use the reported scope and lower-bound counts. Restore access or rerun the same root once; do not treat missing paths as zero.
- **Maintenance is partial:** inspect completed deletions and skipped candidates, reconcile with status, then retry at most once with --apply. A partial operation is not rolled back; each completed deletion remains completed.
- **Restore failed or has unknown publication state:** reconcile with status and choose a different fresh destination for the one retry. Do not reuse or overwrite a possibly partial destination.
- **Pin failed:** status must show that the archive is not protected. Fix the named quota, store, or integrity problem and retry once.
- **Expiry was interrupted after deletion:** the next applied maintenance pass reconciles an absent unpinned historical archive under the private store metadata lease, then continues with remaining candidates. Its bounded receipt says `archive-missing-reconciled`; this is metadata reconciliation, not a new physical deletion. Preview does not perform it. Current runs, pinned archives, pending snapshot reservations, and any present unknown entry remain protected.
- **Archive publication was interrupted:** a complete payload can remain preserved with its staging reservation until metadata is reconciled. After the conflicting operation finishes, preview and apply [storage maintenance](#storage-commands) before retrying the archive operation. Never delete the reservation, payload or lock to force admission.
- **Store is degraded:** repair the user-local path or permissions explicitly, then run status. Required coverage may continue, but no history or recovery promise is made while optional management is unavailable.
- **Unmanaged storage is large:** inspect it, decide which consumer owns it, and use that consumer's explicit retention procedure. AppSurface does not claim those files or run broad coverage clean --all automatically.
- **A restored report is needed for investigation:** use it for historical inspection only. Run a fresh coverage lane for any current gate or Evidence claim.

If a limit, warning, status, or diagnostic differs in an installed build, follow that build's verified command help and release documentation. Update this guide only after the implementation contract and consumer proof are reconciled; do not label measured savings or shipped behavior without the corresponding captured evidence.

On Windows, deletable archive and staging candidates coordinate through one empty private `.appsurface-coverage-candidates.lock` file in the domain root. A deterministic byte-range offset derived from the candidate's physical identity keeps independent candidates concurrent. The file stays zero-length because locking beyond its end does not allocate payload storage. Source-output leases keep their existing protocol. Every candidate operation verifies both retained directory and lock-file identities; a hash collision causes safe contention. The fixed file is never removed to force admission. See [Windows byte-range locking](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex).

## Repository cross-platform verification

AppSurface maintains its own storage implementation using [verified cross-platform coverage](coverage-fan-in.md). That workflow retains the full solution aggregate gate and extends patch evidence with matching native executions; consumers receive the storage defaults through their normal upgrade.
