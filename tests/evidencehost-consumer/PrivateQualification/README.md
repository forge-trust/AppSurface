# Private resource-backed qualification

This separately compiled variant implements the human-approved [qualification proposal](authorization-proposal.md).
The user approved that proposal on 2026-10-03. Its immutable SHA256 is
`ab4028706a095510f4808ed32119d8063fb07937e6b959cf2e0b7fec5a028881`; its final pending-status paragraph records
the earlier proposal state, not the later approval. The approval permits this private variant and measured
execution only, and does not enroll a consumer proof or enable Trusted execution.
It is absent from the production build. Production catalogues, the accepted-consumer proof registry and
Trusted allowlist stay empty. Qualification cannot enroll a result or issue a gate/release claim.

## Compiled admission and result handling

The root workflow renders [`ContractsBinding.cs.in`](ContractsBinding.cs.in) with the exact current run,
workflow, frozen source, tested subject, canonical policy/plan and application entry/catalogue digests.
It hashes the generated sources and finished tooling independently before arming. Tool hashes are root-pinned
after compilation; they are not self-referential literals embedded in the assembly being hashed.

The private admission exception requires the actual root-authenticated `EvidenceLinuxWorkerSupervisor`, the
same armed run, a complete exact compiled plan and descriptor, three distinct fixed UIDs and five distinct
fixed GIDs, the closed bundle/catalogue and seven capability bounds. Only resource-backed Observation may
use this path. Public factories, local writers, JSON, environment flags, fake supervision and verifier
substitution cannot enable it.

Private completed admission carries an internal, non-serialized issuer marker. Structural finalization can
record a genuinely successful resource-backed execution, but always emits `ClaimKind=None` and
`Eligibility=None`; normal production Observation/Trusted semantics are unchanged. Structural verification
can reconstruct only the exact compiled private plan and None/None result; verifying data grants no capability.

The ordinary protected CLI worker uses the shared restricted coverage registration. A second binary built
with the compile-only `EVIDENCE_PRIVATE_QUALIFICATION_HOST` constant calls the real public
`EvidenceHostBootstrap.RunAsync(request)` with complete compiled declarations and the same sealed producer.
Its metadata callback declares Aspire health resource names; the root-owned adapter establishes actual
readiness. Neither binary installs an application factory or invokes a test-core entry.

## Complete producer declarations for the public Host

The private [`EvidencePrivateQualificationHostEntry`](../../../Cli/ForgeTrust.AppSurface.Cli/EvidencePrivateQualificationHostEntry.cs)
must use the complete [restricted Host registration](../../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md):
`registration.AddProducer(declaration, EvidenceRestrictedCoverageProducerFactory.Create(declaration))`.
The two-argument overload records both the exact compiled declaration and its shared sealed producer.
`AddProducer(producer)` records only the producer identity; it leaves the declaration map empty and
`CaptureRestrictedProducers` rejects that registration with `ASEVD404`, even when the producer's ID
matches the plan. This is an ordering requirement before the existing restricted registration audit;
no Host implementation, admission guard, catalogue, capability bound or proof registry is changed.

For historical V23, the parent-verified native evidence recorded CLI exit 0 with a Passed manifest and
Host failure `ASEVD404` (protected producer registration unavailable). The private entry now supplies
the missing complete declaration. This correction is source preparation only: it has not been compiled,
built, tested or proven by a new native qualification run, and it grants no Trusted eligibility.

## Ordering and immutable bindings

Only trusted metadata/build preparation precedes arming. Subject restore, MSBuild/assets evaluation,
AppHost/DCP startup, readiness and producer work begin through the actual armed root broker and its existing
deadline/stage budgets. The launcher retains bundle and artifact handles, distinct account reservations,
bounded scratch/capability/output grants, actual kernel UID/GID/cgroup facts and root-authenticated UDS HTTP
readiness. All producer/application groups and pumps must join before collection and strict cleanup.

The metadata formatter is data-only and uses existing test-friend visibility to produce canonical bytes from
a verified baseline, not runtime registration. Its output must be reviewed and embedded into private source
before compilation. An unresolved template, changed source, build hash, bundle byte/mode, policy/plan digest,
identity, deadline, HTTP peer or cleanup state must fail closed. No filename, assertion or generated receipt
is a substitute for measured execution. This preparation document makes no native success or coverage claim.

## Root build and consumer procedure

### Private bundle source layout

The launcher defaults to `_PRIVATE_QUALIFICATION_BUNDLE_SOURCE = False` and keeps
the ordinary `Tool/application-bundles/application-id/build-id` lookup. The private
[`prepare.py`](prepare.py) replaces exactly one marker with `True` alongside the
existing generated root registration, and includes the resulting launcher in
`generated_sha256`. No environment variable, worker packet, CLI option, uploaded
path or existence fallback selects this layout.

`prepare_application_bundle_input(workspace, deadline, *, expected_owner_uid=0,
expected_owner_gid=0)` returns `(bundle_path, provenance)`. Before canonicalization,
it moves the freshly built `workspace/bundle` into the fresh fixed
`workspace/application-bundle-input/application-id/build-id`. The root-owned outer
container stays `0700`; the application parent and sealed build directories are
`0555`, and declared files remain `0444`/`0555`. The helper retains no-follow parent
and bundle FDs, checks named identities, rejects occupied names, closes every FD,
and uses the original Runner deadline. A failure retains the partial private
workspace; it cannot fall back to another source. Owner overrides exercise file
metadata in portable controls and supply no root lease.

`application_bundle_input` contains `container_path`, `path`, `container_metadata`
(`uid`, `gid`, four-digit `mode`, `device`, `inode`, `nlink`) and
`bundle_files_sha256`, the SHA256 of canonical JSON for the existing `bundle_files`
rows. This is provenance data. The [controller](run-qualification.py) checks the
generated launcher digest before loading it, checks the exact fixed relationship,
and audits all declared bytes against the actual compiled root registration before
launch. Its existing 900-second entry collection clock now includes preflight;
the launcher's captured job and stage timers are unchanged.

The launcher's `_application_bundle_source(...)` context manager holds the outer
and nested directory FDs through the same actual source audit and copy into the
existing `/run` application workspace. The sealed inner root remains distinct
from the writable root-only outer container. Worker/app permissions, target audit,
scratch mounts, admission, accounts, cgroups, pumps and proof criteria are unchanged.

No application bundle copy is published beneath either tool in this private
variant. `measure_product_tool_inventory(tool, coverage_module, deadline, *,
expected_owner_uid=0, expected_owner_gid=0)` returns `(complete_sha_map, summary)`
after bounded real snapshots and streamed rehashing. `tools[entry].sha256` still
binds every tool file; `published_inventory` records actual `file_count`,
`directory_count` (including the root), `total_bytes`, `maximum_file_bytes`,
`maximum_depth`, `file_limit_bytes`, `tree_limit_bytes` and `maximum_entries`.
No file is skipped. The [coverage owner](product-coverage-README.md) retains its
32 MiB per-file, 256 MiB tree, 2,048-entry and depth-eight bounds.

Run 37397763258 retained `tree-byte-bound` at product preparation. Its declared
bundle was 170,136,237 bytes; each tool included that deployment. The fix removes
those additional tool copies while retaining the same actual audited application
copy. The remaining tool totals have not been measured by a new preparation run:
source readiness does not establish that they fit, native success, genuine product
coverage, or a passing coverage gate.

[`prepare.py`](prepare.py) requires the workflow's exact clean source commit and a fresh root-owned workspace.
It binds every tracked source byte and Git executable mode, archives pristine `5d325bb` for the
[metadata formatter](../PrivateQualificationMetadata/README.md), and builds the pinned 13.4.4 AppHost/DCP payload.
It never evaluates the [subject project](../PrivateQualificationSubject/README.md). Canonical policy, plan,
entry and catalogue bytes come from the actual C# metadata APIs. The generator expands source templates and
builds a normal protected CLI and a separate binary with the compile-only `QualificationHostEntry` property.
Source/bundle/template and finished-tool hashes remain distinct; tool hashes are selected after compilation.
App deployment directories are sealed `0555`, declared files are `0444` or `0555`, and later worker permission
changes cannot alter the separately pinned deployment copy. Each trusted build command has one bounded
process group, disabled server reuse, finite kill/reap/group-absence checks and an exclusive durable command
receipt. Nonzero exits, oversized logs, expired deadlines and incomplete ownership remain failures.

Before these trusted builds, the private [`trusted_sdk_distribution.py`](trusted_sdk_distribution.py)
installer selects the **complete .NET 10.0.401/linux-x64 distribution** at the literal publisher URL and
SHA-512 documented in its [reference](trusted-sdk-distribution-README.md). It creates only the fresh
`/usr/share/issue779-dotnet-10.0.401` root after archive authentication and exhaustive metadata checks.
The preinstalled SDK remains in place. The workflow does not use a floating setup-dotnet version.
The [workflow](workflow.yml.in) places that fixed SDK first in root preparation's PATH, followed only by
the absolute executable directories selected by its existing pinned Node/pnpm setup actions and `/usr/bin:/bin`.
It rejects colon/newline or non-executable selections before sudo. This preserves trusted build tooling without
passing the entire ambient PATH. The [launcher's closed environment](../../../scripts/evidencehost-linux-launcher.py)
remains `/usr/share/issue779-dotnet-10.0.401:/usr/bin:/bin`; subsequent .NET builds use the auditor's absolute host.

[`prepare.bootstrap_sdk`](prepare.py) first creates an incomplete root-private binding without reading
an absent future host. `protect_sdk_ancestors(deadline, diagnostic=...)` authenticates root and the NSS
`runner` sudo invoker before retained-FD ancestor protection. `/usr` stays root:root, readable/searchable,
ordinary and nonwritable by others. The retained, named `/usr/share` directory may lose only existing
`022` bits through `seal_share_ancestor`; substitution or invalid metadata rejects before installation.

The installer runs as the fixed `/usr/bin/python3 -B <frozen-module> --install` child under the existing
`Runner.run` process group owner. Its absolute command deadline is 180 seconds, including output acceptance;
on expiry the owner kills/reaps the group and prohibits every later build. The module's read/socket checks
supplement this external owner. `validate_distribution_provenance(raw)` accepts at most 4096 bytes, exact
fields and the fixed URL/hash/SDK/RID/root, bounded counts and a canonical tree digest. Provenance is data,
not SDK audit, process ownership or admission. `record_installed_sdk_preflight` retains it and ancestor facts
before reading the newly installed host's metadata. A failed install leaves preparation incomplete.

Only then does [`trusted_sdk.seal_trusted_sdk`](trusted_sdk.py) audit the **entire** fresh installation with
the existing cumulative 120-second/four-hash-pass contract. A fresh audit diagnostic clock excludes installer
time. No archive inventory, prior hash or publisher receipt substitutes for this audit. Installation and
sealing must both complete before metadata/app builds, and subject evaluation still waits for the actual
supervisor to arm. `sdk_bootstrap.distribution` and `preinstallation_ancestors` remain in the completed
private build binding. All original qualification gates, None/None claims and empty production registry remain.

The internal `seal_share_ancestor(parent, share_fd, deadline, *, diagnostic=None)` FD procedure is
called only with the bootstrap's pinned `/usr` and fixed `share` directory. It returns numeric
`before`/`after` metadata plus `write_bits_cleared`; the completed SDK record stores this as
`share_ancestor`. An already nonwritable ancestor is checked without `fchmod`. There is no `fchown`,
recursive mode change or rollback promise. A syscall failure, substitution, invalid metadata or
expired deadline propagates and prevents SDK/consumer dispatch, even after permissions were tightened.
The diagnostic phases `share-sealing` and `share-recheck` preserve the first failure without paths or
exception messages. Three [portable procedure matrices](test_trusted_sdk.py) use real current-owned
temporary FDs and substitute only root stat observations; they verify mode preservation, named/type/
owner rejection and mutation/recheck/deadline failure. They do not invoke root bootstrap or a system SDK.

The complete initial tree must contain only readable regular single-link files and searchable directories
owned by root or that trusted setup account, with no special permission bits. The host must be an executable
Linux x64 ELF file. Bounds are 100,000 nodes, 32 directory levels, 256 MiB per file, 16 GiB total and one
120-second allowance inside the existing cumulative build deadline. After the complete audit, root adopts
ownership through retained FDs and removes group/other write bits. It preserves all bytes, read/execute bits,
canonical paths and executable device/inode identity, then independently rehashes the exact tree. Any
substitution, foreign owner, link, unsupported type, mismatch or deadline expiry fails preparation; a partial
seal never permits consumer execution. The application startup ownership guard remains unchanged.

The private build binding records actual initial and final host UID/GID, mode, link count, device/inode,
length and SHA256, tree digests and counts. A bootstrap failure retains an explicitly incomplete private
preflight binding with initial host metadata, or explicit unavailable/null if metadata cannot be read,
not a completed build or acceptance record. Earlier V7 measured
only rejection at `dotnet-validation`; it did not measure which ownership/permission predicate failed. This
bootstrap is a controlled trusted-installation handoff, not an inference about that missing native field.

The optional internal `diagnostic=SdkDiagnostic()` keyword on the SDK procedures records data only;
it supplies no root, owner, path or guard override. On bootstrap failure, `prepare` attempts an atomic
update of the existing incomplete `build-binding.json`, capped at 4096 bytes, preserving `host_before`
and `preparation_complete:false`. The nested `sdk_bootstrap.diagnostic` schema is
`issue779-trusted-sdk-diagnostic-v1`: closed phase/role/error-class labels, last observed node numeric
UID/GID/mode/link-count/device/inode/length, NSS runner IDs, bounded parsed sudo IDs, and nullable
ambient/fixed PATH-match booleans. Node mode is an integer permission mask. Unknown observations are
null; no SDK node paths, filenames, arguments, exception messages or content bytes are recorded.
The node also records the closed kind `regular`, `directory`, `symlink` or `other`, derived from that
same observed stat without another lookup; this distinguishes file type from permission bits.

Phases cover root/sudo identity, PATH binding, the two system ancestors, SDK-root validation, inventory
directory/node/type/owner/mode/size/hash/count/shape/deadline, sealing directory/node/hash/adoption/
recheck/count, and final recheck/deadline. The first propagated failure freezes its snapshot. The node
is the last observation already made by the procedure, not an additional lookup at failure time.
Missing diagnostics or failed capture do not replace the original failure, complete preparation or
permit consumer execution. Earlier verified nodes may already be sealed when a later check fails;
the diagnostic neither rolls that state back nor treats it as successful sealing. Four additional
[portable controls](test_trusted_sdk.py) use owned temporary metadata and deliberately failed guards;
they do not invoke root bootstrap, NSS, system SDK paths or native execution.

A successful deadline check keeps the active operation phase; only its failed predicate records
`deadline` (or retains `final-deadline`). It takes the same single monotonic sample as the guard.
Enumeration entered during sealing retains `sealing-count` when its bound rejects. Three additional
controls cover retained-FD read failure attribution, sealing enumeration overflow, and atomic retention
of the closed frame. The retention control explicitly substitutes root UID/GID observations only for
owned temporary inodes; it establishes no native ownership. Its success preserves initial host data
and the incomplete state. Unsafe/oversized inputs or replace failure leave the original binding and
exception intact and remove any diagnostic temporary file.

### Bounded work diagnostics and exact-target mutation skip

The private `seal_inventory` procedure independently skips `fchown` when the retained node already
has root UID/GID, and skips `fchmod` when its retained mode already equals the target (the audited
mode with only `022` cleared). A differing owner or mode still requires its respective operation.
Skipping one operation does not skip the other operation's guard. Both file hashes, every
named-FD/identity check, complete set check and deadline remain in place. The full bootstrap still
reads every file four times: initial inventory, before adoption, after adoption, final inventory.
The strict `/usr` and retained `/usr/share` guards are unchanged. This is a conservative work reduction,
not a claim that an unchanged 120-second window will complete a particular installation.

The existing private `issue779-trusted-sdk-diagnostic-v1` frame additionally records:

- `phase_before_deadline`: the closed phase active before the first failed deadline predicate; null
  until expiry. Existing `phase` still reports `deadline` or `final-deadline`.
- `work_pass`: `unknown`, `initial-inventory`, `seal-before`, `seal-after` or `final-inventory`.
  These are host-selected traversal/hash stages; no node names or paths are recorded.
- `elapsed_milliseconds`: null before the first existing deadline sample, otherwise the nonnegative
  interval between the first and latest such samples, capped at 120000. It is a sampled span, not
  a measured duration for root/NSS/PATH work before the first sample. No additional clock read occurs.
- `nodes_observed`: cumulative directory/file observations across the three traversals, capped at
  300000 (three times the existing 100000-node bound).
- `hashed_bytes`: successfully read, validated bytes across four hash passes, capped at 68719476736
  (four times the existing 16GiB aggregate bound). It includes repeated reads; it is not unique file size.
- `work_overflow`: false normally. A counter exceeding its bound is clamped and immediately fails
  closed; reaching the bound alone never grants success. The first propagated failure freezes all facts.

Internal `SdkDiagnostic.add_work`, `work_pass` and `observe_clock` update only this bounded data.
The deadline uses its existing single monotonic sample and unchanged predicate. Observer/capture
failure cannot replace an already propagating file or SDK error; raw exceptions and bytes remain
excluded. The existing incomplete-binding retention still caps JSON at 4096 and preserves failure.
Three [portable procedure controls](test_trusted_sdk.py) verify exact-target skipping versus needed
mutations on real current-owned FDs with labelled root-stat projection, all four hash-pass byte totals,
first-expiry attribution/capping, counter rejection and equivalence of successful guards. They do not
call root bootstrap, system SDK paths, native commands or grant admission. Existing20 SDK methods
remain intact; future execution belongs to a separately authorized focused validation.

### Private cost measurement proposal

The existing private frame adds `process_cpu_nanoseconds` (sampled process CPU span),
`measurement_incomplete`, `measurement_clamped`, and `costs`. `costs` has exactly four closed rows:
`read`, `sha-update`, `filesystem`, `projection`; each has `calls` and `nanoseconds`.
The optional `_measure(diagnostic, kind, operation, *args, **kwargs)` executes the original operation
exactly once and uses measurement-only `perf_counter_ns`/`process_time_ns` samples around it. No measured
value influences a deadline, filesystem guard, admission or success result. There is no caching or
change to the complete tree, four hash passes, 120-second allowance, bytes, identity or ancestor guards.

`filesystem` measures existing open/stat/fstat/lseek/scandir-construction calls; it excludes iteration
inside scandir and close/mutation syscalls. `read` and `sha-update` separately time actual read/update
calls. `projection` includes diagnostic note/projection work, including notes without numeric metadata.
CPU span runs from the first valid cost sample to the latest completed sample, not total job CPU.
Categories and CPU/elapsed spans overlap and are not summed to reconstruct duration. Instrumentation
adds its own overhead, so a future native measurement would identify sampled costs, not prove a remedy.

Call counts cap at 32 times (the existing four-pass byte bound plus three-pass node bound); each duration
and CPU span caps at 120000000000ns. Clamp sets both measurement flags without changing the operation's
result. Failed/malformed/backwards timing marks incomplete and records null for that row's duration;
missing CPU is null. Counts still describe attempted calls, including failures. First propagated failure
freezes copied aggregates. No paths, names, arguments, exception strings or raw content enter the frame.
The existing incomplete-binding retention remains capped at4096 bytes. Three new portable real-FD/data
controls define count/result/hash equivalence, clock failure with the same original EIO, diagnostic-only
clamping, first-failure freezing and canary/frame bounds. Original23 SDK test bodies remain unchanged.
This describes the initial cost-diagnostic source handoff; later portable execution receipts are separate.
These data/procedure controls do not represent a system SDK or qualification execution.

The copied subject snapshot is separately sealed to `0555` directories and `0444` files before immutable
preflight. Git tar entries may carry `0775`/`0664` modes; copying those modes does not satisfy the protected
snapshot's no-write requirement. Sealing changes no bytes and evaluates no subject project. The launcher
later creates its independently owned writable subject copy only inside the armed run. The subject revision
remains the separately bound SHA256 tree identifier; it is not represented as an invented Git commit.

[`run-qualification.py`](run-qualification.py) calls the actual launcher's `launch_with_completion` for both
entries, passing the compiled profile/application/entry selection and one 900-second deadline per fresh run.
Admission/start/collection/cleanup/stopping allowances are 30/120/60/60/5 seconds; resource readiness is
45 seconds and the one coverage producer has 180 seconds. Stopping is reserved inside cleanup. These are
finite reviewed inputs, not resets of an active allowance. Fixed account creation and pending ownership are
documented in [root bindings](root-bindings-README.md).

The [retained-output collector](retained-output-README.md) duplicates the actual completion handles and reads
only the closed CLI/Host output layouts. After those duplicates close, completion must close its handles and
strictly delete retained accounts. Only then are raw files saved privately and structurally evaluated, with
the actual private CLI's `evidence verify` against the independently compiled expected plan. Saved diagnostics
are not uploaded authority. The controller requires genuine Passed execution, Ready resource, Passed producer,
matching report bytes and closed obligations while forcing `ClaimKind=None` and `Eligibility=None`.

## Failure retention and coverage limits

The root controller emits only a fixed terminal category. Its private archive selects fixed root-owned,
single-link `0600` files beneath retained `0700` diagnostic directories. It includes bounded build/verifier
command receipts, closed early import/tool/subject preflight stages, at most 16 KiB per log tail, bounded launcher journal and subject prefixes, and complete
small collected plan/manifest/report copies. The index distinguishes tails, complete files and oversized
omissions; capture failure cannot change the original execution result. Archive data is canonical USTAR,
at most 18 MiB with 16 MiB retained payload including its index, copied to a runner `0700` directory with `0600` files. It supplies
diagnosis, not acceptance. Private portable controls use actual file/process data and metadata seams; they
do not fabricate admission, kernel observations or a protected positive result.

The [workflow template](workflow.yml.in) must be expanded with the frozen source commit in a subsequent
normal harness commit before push. Native execution remains unverified until its exact run, head, workflow,
source/tool/bundle bindings, receipts and artifacts are independently checked. The initial qualification
attempt was uninstrumented. External CLI/Host execution is not automatically included in VSTest collection.
The separately reviewed product coverage procedure below is still pending actual mixed execution; passing
qualification alone does not establish the unchanged solution coverage gate.

## Product coverage within existing worker permissions

The private [coverage owner](product-coverage-README.md) and [launcher integration](product-integration-README.md)
use the worker's existing writable anchor. They add no writable unit path and preserve the artifact
allocator's mount-crossing guard. An independently owned root watchdog controls the official Coverlet
provider, its lifetime, private state, restoration, and mount quarantine. The real worker still receives
its original identities, sandbox properties, and deadline.

[`prepare.py`](prepare.py) loads one fixed physical [product preparation helper](prepare-product.py)
from its authenticated build tree. It verifies the complete [pinned product inventory](product-source-manifest.json)
for commit `171f53911c7c1fec08bf0676b321539567f97c5d`, builds the three pristine Evidence Cli/Aspire/Coverage
libraries with locked dependencies, and checks an actual five-pair [ABI metadata report](../ProductAbiInspector/README.md)
before replacing exactly those three DLL/PDB pairs in each private consumer deployment. Contracts, Planner,
the entry executable, the protected probe, the compiled catalogue, and proof registry remain private variant
inputs. The metadata report supplies no runtime compatibility or coverage verdict. Missing definitions,
unresolved access, source checksums, debug identities, or bounds reject preparation before replacement.

The [partition contract](product-partition-preparation-README.md) keeps inventory and comparison
results separate. An exact shared-image reference avoids serializing the same verified Coverage
DLL/PDB inventory twice. Common completeness and definition/dependency checks each have a
100,000-work limit, share one 32 MiB byte account, and use the same 30-second reconciliation
command and original preparation deadline. These are trusted preparation limits; the worker
keeps its existing permissions and resource limits.

The root-owned public [task host](../PrivateProductCoverageTaskHost/README.md) lives at
`workspace/product-coverage-taskhost`; it is built from authenticated Coverlet 10.0.1 bytes. Each consumer
has separate provider state, hit data, report directory, and restoration receipt. The root controller collects
declared artifacts first, joins consumers, collects genuine provider hits, verifies restored hashes and
metadata, copies five fixed private report files, then closes completion and accounts. Instrumented execution
hashes and restored product hashes are recorded separately. Failure preserves its original category and
cannot become a coverage or qualification success through diagnostic collection.

Build binding is capped at 1 MiB and references the full product manifest by its hash. Per-entry binary
receipts are capped at 512 KiB. Product Cobertura and JSON reports are capped at 2 MiB and 4 MiB; provider
stdout/stderr caps are 64 KiB and 128 KiB. The private archive includes those fixed files within its
16 MiB payload and 18 MiB archive limits. The actual product shards must be checked against the pinned
sources and merged through the unchanged reporter/gate before any measured patch-coverage claim.


## Trusted Runner failure latch and current retention controls

[`Runner.run`](prepare.py) owns one cumulative deadline and one command at a time. Valid arguments set an internal pending latch before any log open or process spawn; reentrant calls reject before process work. Any later exception, nonzero exit, timeout, uncertain cleanup, receipt failure, capture failure or final deadline rejection permanently marks that Runner failed. Remaining cumulative time cannot permit another dispatch. Only a complete zero-exit durable receipt, actual group absence, successful pipe cleanup and final deadline/capture checks restore ready. Invalid argument guards reject before consuming ready. Use a fresh Runner only for a separately owned preparation operation; it cannot resume a failed build chain.

Captured stdout defaults to1MiB, with root-selected integer cap1..32MiB+1; input is None or bytes<=1MiB. Selector I/O and reap use the original command allowance, including reserved cleanup, and no second communicate is used for capture. `maximum_seconds` defaults180 and accepts only integer1..180 inside the unchanged global deadline. Close failures reject output; timeout remains code124/process-timeout even when cleanup also fails. Private receipts exclude raw exception canaries.

The current [private retention controller](run-qualification.py) selects build log/receipt numbers01..64, with65 outside the whitelist. Overall limits remain16MiB payload (including index) and18MiB canonical archive, not the historical3MiB/4MiB limits. The refreshed data control uses actual legal fixed-name files below each individual cap, verifies a valid adjacent aggregate, then adds one legal file to exceed16MiB and requires no archive. Runtime retention code and limits are unchanged.

Historical portable-final-validation attempts1 and2 remain failed receipts: attempt1 used unsupported systemPython3.9; corrected Homebrew attempt2 reported195 tests/five failures. The focused regression controls described here are new evidence only after their own supervised receipt; they do not replace those failures or establish native qualification/ABI/coverage.

## Current existing-permissions preparation validation

The [product metadata stages](product-partition-preparation-README.md) now split mixed Contracts into complete inventory and private inspector's C# Comparisons.Common comparison jobs, as already done for Coverage. Three ordinary pairs plus those four jobs precede one Python reconciliation child. Seven native100K limits and two independent Python100K limits produce a maximum aggregate900K across eight processes. The original cumulative deadline, per-command30 seconds, shared32MiB metadata ledger,512KiB receipt and actual worker permissions are unchanged. A combined mixed Contracts job exceeded100K and remains a failed historical receipt.

All seven local metadata jobs and full mixed-data reconciliation passed. Corrected full portable discovery passed198 tests with zero failures, errors, skips or warnings. Root receipt publication, actual Linux execution, product instrumentation/restoration, source attribution and the unchanged coverage gate remain mandatory before a product coverage claim. The [CLI coverage gate reference](../../../Cli/ForgeTrust.AppSurface.Cli/README.md) describes policy; use the actual authenticated product build root and exact base-to-product diff for this separately compiled variant. Numeric gate success must be reconciled with genuine normalized filenames and measured changed lines; missing attribution is not coverage evidence.
