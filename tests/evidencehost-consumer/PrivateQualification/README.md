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

Before these trusted builds, [`trusted_sdk.py`](trusted_sdk.py) audits and seals the fixed
`/usr/share/dotnet` installation selected by the pinned workflow's trusted setup action. The root
controller requires the NSS `runner` account to match the sudo invoker, and both its preparation PATH
and the launcher's fixed `/usr/bin:/bin` PATH to resolve to that same canonical host. The `/usr`
ancestor retains its strict root-owned, nonwritable guard. The fixed `/usr/share` ancestor is retained
through a no-follow directory FD and must have root UID/GID, directory type, no special permission
bits and all `0555` bits. Before opening the SDK tree, root may clear only its group/other write bits
(`022`) through that FD. Its named identity, ownership, type and all other permission bits are
rechecked. This private trusted-CI setup hardening accepts neither subject files nor a caller-selected
SDK root and leaves the SDK tree's complete-audit-before-adoption ordering unchanged.

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
No controls have run in this source-preparation packet; no system SDK or qualification is exercised.

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
at most 4 MiB with 3 MiB retained payload, copied to a runner `0700` directory with `0600` files. It supplies
diagnosis, not acceptance. Private portable controls use actual file/process data and metadata seams; they
do not fabricate admission, kernel observations or a protected positive result.

The [workflow template](workflow.yml.in) must be expanded with the frozen source commit in a subsequent
normal harness commit before push. Native execution remains unverified until its exact run, head, workflow,
source/tool/bundle bindings, receipts and artifacts are independently checked. This first qualification
attempt is uninstrumented. External CLI/Host execution is not automatically included in VSTest collection;
future honest measurement must preserve PDB document/line/branch correspondence and feed actual shards
before the unchanged solution coverage gate. Passing qualification alone does not establish that gate.
