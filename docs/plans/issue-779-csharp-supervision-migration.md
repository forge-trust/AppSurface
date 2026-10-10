# Plan: migrate EvidenceHost supervision to C#

- Date: 2026-10-06
- Status: Checkpoint 1 complete empty-run source composition implemented; native gate pending.
- Design: [C# supervision core](../designs/issue-779-csharp-supervision-core.md).
- Requirements retained: [#779 trust boundary](../designs/issue-779-evidencehost-trust-boundary.md), [57-group test plan](issue-779-evidencehost-test-plan.md), and [consumer acceptance](../evidence/issue779-consumer-acceptance.md).

## Outcome

One protected AppSurface deployment supplies supervisor and worker roles. A typed C# supervision core replaces the privileged Python broker/application runtime. CLI and SDK execution share its authenticated control path, physical workload joins, and root completion rules.

The old native/private-harness repair loop remains stopped. Its snapshots and receipts are historical evidence. Existing source changes are preserved; the migration does not reset the branch or manufacture a new passing baseline.

## Common source and qualification builds

Ordinary supervisor and worker roles share one integrated source graph. The internal [compile-owned qualification selector](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceNativeQualification.cs) defaults to `None`; private N04/N07/N08/N09/N10/N11/N12/N13/N14/N15/N16 builds are described in the [Supervision package reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#ordinary-execution-and-private-qualification-images). A private checkpoint must not leave an unconditional helper wait or injected signal in ordinary execution. Its build selection creates no runtime admission, producer registration or consumer proof. Previously verified controls on private snapshots remain evidence for those snapshots until revalidated on the integrated graph.

## Execution rules

- Build one immutable source revision through the ordinary dependency graph. Avoid mixing private Contracts/Planner assemblies with another product revision.
- Complete one checkpoint before expanding the runtime surface. The first checkpoint contains no AppHost, DCP, ReportGenerator, or subject test collection.
- Each native attempt has a specific expected result, a finite deadline, bounded retained evidence, and a source binding. Delegate observation; the main lane owns implementation and diagnosis.
- Retry native execution only after a concrete failure is diagnosed and a bounded local regression verifies the correction. Preserve failed receipts. Unknown causes lead to a named diagnostic proposal, not unrelated repairs.
- Keep the current policy, identities, capabilities, time/output limits, write grants, comparison base, and numerical gate requirements. Changes to the OS termination wiring are part of the redesign and require their own real controls.
- Tests exercise behavior through public or intentional internal seams. Metadata fixtures issue no protected capabilities. Avoid reflection, timing sleeps as race proof, getter padding, and fabricated coverage.

## Checkpoint 1: one real supervised worker

**Scope:** process role dispatch, protected launch validation, minimal core state, Linux systemd backend, credential-checked ready/stop/wait messages, and exclusive artifact-slot allocation. Observation only, dependency-free declaration, no subject test collection or application launch. A closed test workload may create a descendant or stalled pump to verify containment; this adds no Evidence producer or application registration.

### Work

1. Add `Evidence.Supervision` and a focused test project to the normal build graph. Define the narrow internal core/backend seam and reviewed composition access.
2. Add early supervisor/worker dispatch to the [process entry](../../Cli/ForgeTrust.AppSurface.Cli/Program.cs) and [entry seam](../../Cli/ForgeTrust.AppSurface.Cli/ProgramEntryPoint.cs), before normal configuration. Add adjacent controls proving ordinary CLI dispatch still works. The protected bootstrap clears runtime injection before .NET starts and binds both the host and managed entry for a framework-dependent deployment.
3. Implement strict shared protocol types without adding a public descriptor-to-capability factory. Preserve existing client admission behavior; clarify workload-wait versus complete-run scopes.
4. Implement protected deployment/request validation, monotonic job allowance, root-run workspace, explicit accounts, and typed systemd operations. Pin the D-Bus dependency and prove its actual startup/termination behavior before relying on it. OS runtime plus stop/kill bounds must fit the remaining original job deadline.
5. Register pending worker ownership before start I/O. Arm systemd enforcement and establish supervisor identity before the worker can authenticate. Give the owner a single-use activation guard so a late dependent start cannot restart a closed owner/run; prove that ordering on Linux.
6. Reuse actual [artifact allocation](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxArtifactRoot.cs). An occupied slot or changed parent fails before artifact activation and leaves sentinel bytes unchanged.
7. Implement root completion after workload join, worker exit, drains, and strict finalization; no positive publication when the supervisor disappears.

### Required controls

Core tests cover malformed/replayed messages before I/O, one failure latch, deadline exhaustion, pump errors/byte accounting, expired final rechecks, and workload-operation versus control-handler accounting. Include a deterministic wait-handler control that would deadlock if the handler counted itself as unfinished workload.

The native inventory below is fixed for checkpoint 1. Every row requires a named result; there are no optional or silently skipped native rows. Test limits can be lowered within the protected contract to keep deadline cases finite.

| Case | Control | Required observation |
| --- | --- | --- |
| N01 | Genuine positive | Root supervisor starts the same binary as nonroot worker; actual peer PID/UID/GID and cgroup bind; one fresh admitted slot; worker exits; workload/run scopes finish in order. |
| N02 | Unprivileged supervisor invocation | Reject before accounts, units, or artifact writes. |
| N03 | Direct worker with nonroot broker | No authenticated ready request, admission, slot write, or complete claim. |
| N04 | Replaced root peer | Pin rejects a second PID before operation bytes; cleanup retains the original run owner. |
| N05 | Occupied slot | Actual exclusive allocation rejects; sentinel bytes remain unchanged; no writer activation. |
| N06 | Symlink slot | Actual link-safe allocation rejects; outside bytes remain unchanged. |
| N07 | Replaced parent identity | Retained parent binding rejects substitution; no output capability is activated. |
| N08 | Cancel before allocation | No allocated slot or subject work; original cancellation stays latched. |
| N09 | Cancel after retained allocation | Tracked allocation settles before handle/account cleanup; no successful publication. |
| N10 | Stop racing a pending start | Stop irreversibly closes pending-start admission, attempts the original stop, joins the actual original start task, attempts the second stop after that join, and joins worker finalization/pumps; native evidence must show no escaped group or reopened admission. A post-reply source frame is only a trigger and does not prove late creation or physical settlement. |
| N11 | Synchronous worker stall | Independent stop ends the worker within the bound; no dispose/hash/success marker from the stalled path. |
| N12 | Leader exit with live descendant/pump | Leader exit alone cannot establish join; stop ends the descendant and reaches actual EOF, or quarantines. |
| N13 | Supervisor SIGKILL during work | Dependent units terminate without managed finally; output remains quarantined. |
| N14 | Supervisor SIGSTOP | Fixed external deadline forces owner/dependent termination; no heartbeat thread is required to progress. |
| N15 | Supervisor death during an actual pending worker start | A source-owned trigger follows the validated `StartTransientUnit` reply while the original start task stays unjoined; dependent-unit termination is observed; no successful completion. |
| N16 | Blocked workload request with concurrent stop/wait | Fresh authenticated control requests remain usable; workload and handler joins are distinct and finish within the bound. |

**Exit gate:** a supported Linux runner executes all 16 named native cases using the deployed executable, with zero skipped native cases, plus the core controls above. Bound receipts identify actual code revision, units, identities, exit/EOF facts, and cleanup disposition. Unit tests alone or a subset of native cases do not complete this checkpoint.

If privilege bootstrap or same-binary role isolation fails, resolve that design issue here. Do not introduce an application fixture to work around it.

## Checkpoint 2: declared producer and artifact custody

**Scope:** one existing restricted coverage producer through the C# server. The root core selects the closed procedure; the subject gets its existing isolated results/scratch paths. Keep no application resources in this checkpoint.

### Work

1. Move producer unit execution/output drains into the backend and aggregate ownership ledger. Preserve the fixed command/environment policy in [Coverage](../../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md).
2. Share the per-run received-output counter across all owned streams, including discarded bytes. Completion requires two EOFs, no pump failure, exact summed bytes, and a final quota sample.
3. Move root artifact retention into `ArtifactCustody`. Bound file count/bytes, reject links and substitution, retain handles, and copy exclusively after physical workload exit.
4. Keep the worker's [restricted producer lease](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceRestrictedProducerLease.cs), actual plan/diff binding, cancellation tracking, and shared producer implementation.
5. Run exact reporter and numerical gate behavior on genuine retained reports. A reporter exit code alone does not prove the report contains valid lines or branches.

**Exit gate:** genuine declared tests run in the producer identity, generate real coverage, and reach the existing gate; positive/negative report semantics, cancellation, ignored cancellation, quota overflow, artifact mutation, and cleanup failure remain unsuccessful when appropriate. Worker/control/tool/final roots remain denied to subject writes. Label procedure tests separately from protected runtime evidence.

## Checkpoint 3: restricted application and SDK parity

**Scope:** the existing compiled restricted application contract, actual AppHost/DCP/resource startup, readiness, and joined shutdown. Use a compiled test registration to validate mechanics; production catalogue and proof registry remain closed.

### Work

1. Replace the Python application lease with C# owned-unit state in the same core. Select only compiled registrations; share canonical catalogue/file-role/capability validation with Planner.
2. Bind audited bundle bytes/modes to the selected entry. Retain pending ownership before start, use bounded application scratch, and enforce fixed argv/environment derived from that entry.
3. Observe actual application/resource PID identity, UID/GID, cgroup, and kernel-authenticated bounded UDS health. A JSON readiness value alone is insufficient.
4. Aggregate application and producer groups/output counters without allowing application access to producer result or protected writer paths.
5. Integrate the [restricted Aspire adapter](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceRestrictedAspireApplication.cs) and [bootstrap](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceHostBootstrap.cs). Preserve registration snapshots, pre-start validation, stage-token ownership, and the joined cleanup phase.
6. Prove CLI and SDK use the same admission, root implementation, stop/join rules, artifacts, and terminal claim restrictions.

**Exit gate:** real application startup/readiness and denied tool/output probes pass under actual OS identities; failed readiness, stalled factory, cancellation, stuck descendant, and disposal failure finish with the required absence of claims/publication. No uninstrumented native probe is credited as product branch coverage.

## Checkpoint 4: cutover and full verification

### Work

1. Remove Python from runtime launch, broker dispatch, application supervision, and security decisions. Migrate useful test intentions to .NET/native controls; retain historical receipts without treating them as current acceptance.
2. Update the protected consumer workflow to launch the same product supervisor/worker roles. Keep any repository preparation utilities thin and outside admission authority.
3. Update package/operator/SDK docs, packing, dependency manifests/locks, and supported-platform diagnostics. Prove the deployed tool contains the required runtime/backend dependencies and can be started from a fresh fixture.
4. Run the [complete test plan](issue-779-evidencehost-test-plan.md), including both admission callers, disposal/collection ordering, quota races, denial controls, rapid retry/fresh roots, fatal paths, and protected downstream verification.
5. Verify formatter, build/analyzers/XML docs, CLI/SDK QA, package checks, and the unchanged [solution/patch coverage gate](../evidence/issue779-coverage-gate.md). Collect coverage from a supported instrumented build at this revision; explicitly bind spawned-process data if used.
6. Update [acceptance status](../evidence/issue779-acceptance-status.md) from actual measurements. Trusted enablement requires the complete consumer/platform proof and a separately reviewed compiled registration.

**Exit gate:** all required acceptance rows and numerical gates pass; no unresolved correctness finding; packaged CLI/SDK behavior is proved. Only then finalize the validated draft PR. The redesign documents themselves do not satisfy this gate.

## Test migration inventory

| Existing evidence | Treatment |
| --- | --- |
| Planner, catalogue, strict descriptor/application parsing, manifest/numerical evaluation | Reuse current meaningful .NET cases; update only actual contract changes. |
| WorkerExecution barriers, cancellation, single use, registered disposal, callback join | Reuse; keep the worker/root completion scopes explicit. |
| Shared producer/factory/report gate procedure tests | Reuse with their existing metadata/procedure labels. |
| Python pure broker/unit-property/pump/state controls | Port intentions into core/backend tests, including previously found lifecycle races; avoid one-for-one implementation assertions. |
| External protocol worker/broker fixtures | Replace with shared C# server controls plus same-product process tests; synthetic receipts remain protocol-only. |
| Root openat2/identity/credential/cgroup controls | Execute on supported Linux with actual identities and retained handles. |
| Private assembly overlays, stub ELF/JSON fixtures, uninstrumented AppHost probes | Historical preparation/mechanism evidence only; no automatic admission or coverage credit. |

## Review and stop points

The design review checks privilege bootstrap, early role isolation, dependency direction, workload versus worker join scopes, pending-start races, final deadline checks, and private diagnostics. Implementation reviews are checkpoint scoped. A new persistent service, backend framework, additional write grant, capability table entry, policy increase, or altered gate is a new design decision and must be made explicitly.

No completion date or coverage gain is inferred from this plan. Progress is reported by the checkpoint's concrete exit gate, observed failure, and next bounded action.

## Implementation record

### 2026-10-06: checkpoint 1 foundation

Implemented in the ordinary solution graph:

- Internal [Evidence.Supervision library](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md), its focused test project, pinned D-Bus dependency and generated lock files.
- Early same-executable role dispatch before normal configuration/discovery, exact bounded argument parsing, and direct authenticated worker execution. The worker is a plain implementation type, not a discovered `ICommand`. Supervisor execution remains closed pending protected bootstrap and server/unit-start composition.
- Closed request parsing compatible with the existing client, pending workload ownership distinct from control handlers, counted paired output, and typed root/PID-1-authenticated systemd inspection/stop/kill operations. These primitives issue no admission or physical-exit receipt.

Local validation used .NET 10.0.102 on macOS arm64, sequential source builds with `--no-restore -p:UseSharedCompilation=false`, exact-file whitespace formatters and locked restores with audit unchanged. Passing cases were measured across the initial and focused corrective runs:

| Surface | Cases with a passing result | Scope |
| --- | ---: | --- |
| Process-role grammar | 24 | Data only; initial run |
| Compatible closed control codec | 70 | Data only; initial run |
| Pending ownership/control-handler ledger | 22 | Deterministic barriers; initial run |
| Paired output collection | 16 | Focused rerun after test-stream ordering correction |
| systemd names/properties/operation joins | 29 | Portable data/lifetime controls; corrective runs |
| Early CLI dispatch and ordinary help | 9 | Actual CLI composition; final source build |

The initial core run was 158/160. Two test-stream cancellation callbacks could be unregistered before observation; the test helper now installs its observer after the cancellation-aware pending read, and all 16 output cases passed. Independent review also corrected ordinary command discovery and the D-Bus interface for `ControlGroup`. A new wrong-type test initially supplied a valid string for that string field; the corrected five-row validation passed. Failed logs/TRXs remain preserved. The full core/solution was not rerun after those focused corrections; this is not a coverage-gate result.

All recorded compiler/analyzer diagnostic lines were zero, and no validation command timed out. Independent reviews covered output ownership, ledger self-join/races, early dispatch and typed backend projection. Actual built-executable QA passed ordinary help, both reserved help paths, malformed-role rejection and the still-closed supervisor invocation (five bounded processes). No native systemd operation or acceptance case ran locally.

Detailed commands, exits, TRXs, source hashes and QA outputs are in the local scratch record `/private/tmp/issue779-csharp-foundation-validation/`. This is a disposable local validation record, not a committed consumer proof.

**Next bounded work:** protected deployment/request validation through retained Linux handles, single-use owner bootstrap, worker unit creation and kernel-credential-checked server dispatch. Then execute all 16 native checkpoint cases. Checkpoints 2–4, full acceptance, package QA and the unchanged solution/patch coverage gate remain required.

### 2026-10-06: protected launch components

Added actual retained Linux request/deployment factories, a closed checkpoint-one launch request,
fixed worker-unit creation over the pinned D-Bus client, and kernel-credential-checked socket framing.
The new [reference content](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#protected-launch-inputs)
documents shapes, defaults, caps, ownership and ordering. The artifact allocator now shares its existing
native descriptor operations with the deployment audit; its exclusive allocation and unsupported errno
behavior retain their existing contracts.

The initial sequential source validation passed **303 core cases and 48 focused CLI cases**, with zero
failed/skipped cases or compiler diagnostics, in 13.802 seconds. The CLI scope was artifact-root (14),
protected execution (26) and early entry (8). It ran on macOS arm64; Linux syscall branches and systemd
execution were not established.

Independent review found and corrected:

- Worker duration ceilings and control-socket overlap with writable output. A negative stop-ceiling case
  was then corrected to isolate that guard from the smaller-allowance guard.
- Search-only protected ancestors: root-owned 0711 is sufficient outside inventoried directories, while
  inventory directories retain full read/search and change metadata. Ancestor identity/owner/mode stays
  pinned without rejecting unrelated directory child churn.
- Complete directory/name rechecks after the full hashing pass, and original monotonic deadline checks
  inside intermediate inventory/read operations.
- Concurrent socket closure: initiation alone cannot establish completion. Response/error/cancellation
  paths and async disposal join the actual close; barrier controls retain late close failure.

Final source validation ran three exact-file whitespace formatters followed by the three changed core
classes: **34 deployment controls, 28 worker recipe controls, and 29 connection controls; 91/91 passed**.
All four commands exited zero in 6.058 seconds, with no compiler diagnostics, skips or timeout. Formatting
changed no bytes, and all 26 recorded current source hashes match the final record. The final 91 cases
overlap the initial core run; these counts are not additive unique-case or numerical coverage claims.

The initial and final records remain separately bound at
`/private/tmp/issue779-csharp-launch-validation/{receipt.json,final/receipt.json}`. All owned compiler
processes reached a collected terminal state. No Git mutation, CI run, Python cutover, consumer proof or
coverage-gate result is claimed by this pass.

**Next critical integration:** verify and consume the root owner's single-use activation guard, create
the retained root-run/account workspace, bind a server dispatcher to the live selected worker/kernel
cgroup, and complete workload versus worker shutdown scopes. Connect the early supervisor role only
through that composition, then run all N01–N16 cases. The role remains closed meanwhile.

### 2026-10-06: root owner and pending-start ownership

Implemented the actual native `LinuxOwnerActivation.OpenAsync` factory, root-only atomic generation
marker consumption, systemd owner-property projection, repeated proc identity capture and an
irreversible pending-start/stop coordinator. The
[owner reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#root-owner-activation-and-process-lifetime)
describes their shapes, original deadline, pre-CLR bootstrap ordering and authority boundaries.

The initial focused source build passed 166 cases (owner 69, kernel-data 48, account-data 26,
pending-start 23) in 3.096 seconds, with no failure, skip, timeout or compiler diagnostic. It ran on
macOS arm64, so root activation/rename, NSS and Linux proc/syscall behavior were not executed.
Independent review found two owner acquisition issues: descriptor ownership must be registered
before metadata inspection can fail, and actual compound properties must pass their native
struct-array kind check before metadata projection. Both were fixed. The original review regression
was also corrected to isolate each bad array kind; multiple bad properties no longer mask the guard.
Initial and final records are retained separately under `/private/tmp/issue779-csharp-owner-validation/`. The final two exact-file formatting commands and full core source-build/test
all exited zero in 5.619 seconds: **475/475 passed**, zero skipped/failed cases and compiler diagnostics.
Formatting changed no bytes; all 36 recorded before/after hashes match current source. Final receipt
SHA-256 is `bdff2f03f58ca6d26ed9df528e5243673ba7a8f76a59c456ae22a49e19b72945`.
Independent reviews cleared the corrected owner acquisition, repeated process identity and pending-start
ordering. No actual native activation, cgroup emptiness, utility execution or account cleanup is claimed.

At this checkpoint, account work supplied fixed utility commands, pre-dispatch pending-name reservations
and actual bounded forward/reverse NSS readers. `CreateAsync`/`CloseAsync` explicitly rejected
`UtilityOwnerUnavailable`; these primitives issue no account owner. A bounded utility executor with
actual descendant/pump joins and strict absence verification was still required before creating the root
workspace. That record describes the earlier source state, not the later account implementation.

**Next critical step:** compose that utility executor and root workspace, then the selected worker's
start, actual cgroup/pump joins, authenticated ready/stop/wait server and root finalization. The early
supervisor role is still closed. All 16 native cases, checkpoints 2–4, full acceptance, package QA,
unchanged solution/patch coverage and draft PR update remain required.


### 2026-10-06: account utilities, root workspace and kernel/output custody

The core now implements actual fixed root account utilities over the authenticated D-Bus backend,
private account ownership with forward/reverse NSS checks, exclusive retained `/run` workspaces,
bounded cgroup-v2 sampling, and actual CLOEXEC stdout/stderr ownership. The
[account/workspace reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#account-preparation-boundary)
documents private factories, fixed policies, original deadlines, strict reverse cleanup and quarantine.
These paths are composed as source; the early supervisor role remains closed pending the server and
native checkpoint. No Linux account mutation or physical-exit claim is inferred from local tests.

Independent review corrected two races before integration: post-acquisition cgroup pruning must reject
rather than become initial absence, and account creation takes one irreversible owner-wide claim before
holder construction or NSS work so a second factory cannot enter the first holder's rollback. Pre-dispatch
utility rejection still finalizes its actual ownership for safe rollback. Integration checking moved
control/descriptor and broker directories into a separate root-owned control child, with output outside
that control root as required by the existing client. Worker/subject permissions, write grants, caps and
deadlines were preserved. Worker and utility unit metadata use `AddRef` on the starting bus connection
through both stops and joins; actual transport cancellation behavior remains a native requirement.

The first source test attempt compiled the core but failed on a synchronous xUnit assertion overload;
no tests ran. Explicit `Action` fixed that test without changing production behavior. The second full core
source build passed 599 cases. After the workspace correction and worker reference property, the final
exact-file formatting commands and full core source build/test all exited zero in **5.627 seconds**:
**601/601 passed**, zero failures, skips, timeouts or compiler warning/error diagnostics. Formatting changed
no bytes; all 47 recorded source hashes matched before, after and independent current-file verification.
It ran on macOS arm64/.NET 10.0.102 with `--no-restore -p:UseSharedCompilation=false`.

The final receipt is `/private/tmp/issue779-csharp-account-validation/attempt-3/receipt.json`, SHA-256
`2236739901a9033fdf2d383d666a85e1a637c9f9644d1897bae2ce3258e95d4d`.
Earlier failed and passing attempts remain separate. Portable pipe/barrier controls do not establish
Linux kernel, systemd, account-utility or inherited-writer acceptance.

**Next critical integration:** root-owned socket binding/sealing, descriptor publication from the actual
selected worker/kernel tuple, authenticated ready/stop/wait dispatch with separate workload/run
completion, and final artifact/workspace/account custody. Then run all N01–N16 on supported Linux before
opening the supervisor role. Checkpoints 2–4, genuine producer/AppHost/DCP and SDK consumer proof, the
unchanged coverage gate, full acceptance, package QA and draft PR update remain required. Production
catalogue/proof tables remain closed; no Python runtime cutover or Trusted qualification is claimed.


### 2026-10-06: retained control listener and bounded descriptor publication

Implemented the actual private root UNIX listener factory, its retained socket/parent checks, accepted
connection ownership and the existing v1 descriptor/ready serializer. The
[listener reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#retained-listener-ownership)
and [publication reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#worker-descriptor-publication-data)
describe their exact API shape, limits, shutdown ordering and authority boundaries. Bind/seal uses a
retained socket inode and descriptor-relative ownership/mode operations with no pathname fallback or
collision deletion. Accepted connections require a privately captured worker identity and repeat live
kernel continuity plus SO_PEERCRED checks for every read/write; caller-supplied credential tuples are
no longer an accepted factory input. Worker privileges and writable host paths were preserved.

The serializer structurally checks the existing Contracts schema and copies emitted byte arrays. It
reserves one LF byte within the 64 KiB ready-response frame. The first full source build ran 673 cases,
with 672 passing and one failing: directory policies did not retain the subject's separate primary
GID, so their comparison missed that account mismatch. Layout now retains the complete immutable
account snapshot and the serializer requires its equality with the supplied account data. That failing
case and the original receipt remain unchanged historical evidence.

The account-binding retry's exact-file formatting and full core source-build/test both exited zero in **4.555 seconds**:
**673/673 passed**, zero failures, errors, skips, timeouts or compiler diagnostics. All 53 source hash
bindings matched before/after and current bytes, and both owned validation process groups were absent.
That retry receipt is `/private/tmp/issue779-csharp-listener-validation/attempt-2/receipt.json`, SHA-256
`7f011a961295c8af87321851d1252c07d95d12184f3289dbcefdae9b4bfce903`; its TRX SHA-256 is
`2b3a7ac49168ee4a7b921c698ecb8254ff0b99d1d424546d567a938ed09709e2`. The run used macOS
arm64/.NET 10.0.102 and `--no-restore -p:UseSharedCompilation=false`. Portable barrier/data tests
establish neither actual Linux socket sealing nor root/worker authentication, systemd or physical exit.

Independent ownership review then found that an unrelated accept fault arriving after closure could be
misclassified as ordinary shutdown. Every unrelated fault now stays failed; only a fixed marker for an
intentionally interrupted actual socket operation permits normal shutdown bookkeeping. Native inspection
failures never become that marker. The listener also invokes the reentrancy guard before native close state
changes and closes retained path FDs only after the actual registered drain joins. Five deterministic barrier
cases preserve the original 22 ownership controls and verify the correction. Both incremental reviews cleared.

The final two exact-file formatters and full core source-build/test all exited zero in **5.715 seconds**:
**678/678 passed**, zero failures, errors, skips, timeouts or compiler diagnostics. All 53 before/after/current
source hashes matched, and all three owned process groups were absent. Final receipt:
`/private/tmp/issue779-csharp-listener-validation/attempt-3/receipt.json`, SHA-256
`1c53dde96c588ddc8dc429d78f7f6f3df8d1be46c05754fac02fd96f7ca089ba`; TRX SHA-256
`7f360c75501e4b51bfcdd5eea10eddcfb942008792a7e5d468a61a05372ad414`. Earlier failed and
passing attempts remain separate. These are source/portable procedure controls, not Linux native proof.

**Next critical integration:** same-image worker start with pending ownership and actual service/kernel
PID capture, sealed descriptor publication, authenticated ready/stop/wait/exit dispatch with separate
workload and final-run completion, and output/artifact/account custody before identity deletion. Then
run all 16 supported-Linux checkpoint cases before opening the CLI supervisor role. The native checkpoint,
Python cutover, genuine producer/AppHost/DCP/SDK proof, unchanged coverage gate, full acceptance/package
QA and validated draft PR remain required. Production enrollment and proof tables remain closed.

### 2026-10-06: actual same-image worker lifetime composition

Added the private `LinuxWorkerProcess` holder described in the
[same-image worker ownership reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#same-image-worker-ownership).
Its factory binds the actual owner/input/accounts/workspace/listener and claims the only worker attempt.
Startup reserves the complete acquisition procedure, begins both original output pumps before typed
D-Bus start, captures real running service/kernel identity, and seals the descriptor before ready.
The natural-exit monitor and final stop/join are outside request-handler and producer workload joins.
A fresh ready remainder is bounded by the unchanged original deadline; it does not renew startup data.

A separate procedure seam owns startup even when stop wins during acquisition, and joins the original
startup before finalization. Failure never becomes positive completion. Finalization retains the original
starting connection through pending dispatch, kernel group checks and both actual output tasks. Normal
zero exit, successful output and a live original cleanup bound are additional success requirements.
Account cleanup now retains this actual worker and refuses deletion before genuine filesystem custody.
This guard is intentionally unreleased until that next native phase exists; no Boolean or JSON receipt
can authorize identity reuse. The supervisor CLI role therefore remains closed during this checkpoint.

The first exact-file formatters exited zero, but the source test build failed on a newly introduced test
lambda: its underscore parameter was a CancellationToken, so `_ = StopAsync(...)` attempted to assign a
Task to that parameter. No tests executed and no TRX was produced. Renaming only that parameter fixes
the compile error without changing any behavior or assertion. The original failed receipt is preserved
at `/private/tmp/issue779-csharp-worker-validation/attempt-1/receipt.json`.

**Next critical integration:** root ready/stop/wait/exit dispatch with cleanup-only authentication,
real root custody of retained output before account deletion, and the protected same-image owner
bootstrap. All N01–N16 Linux checkpoint controls remain mandatory. Then finish actual producer and
AppHost/DCP/SDK proof, Python runtime removal, the unchanged coverage gate, complete acceptance,
package QA and the validated draft PR. Local source tests do not satisfy those native gates.

Independent review identified and corrected native ownership gaps before proceeding: cancellable status
reads now use a dedicated observation connection rather than releasing the starting AddRef; the retained
exit task is the original monitor Task rather than a result TCS; physical settlement requires authenticated
stopped-unit facts on the original connection before and after actual group/pump/monitor joins. Finalization
also rejects a failed pending start/stop sequence and clears its settlement projection on any finalization
failure. Nonzero natural worker status can settle physically, while successful execution remains rejected.
A separate caller-cancellation correction preserves the original caller token after startup returns;
stop-induced linked-token cancellation does not erase that earlier caller failure.

The final exact-file formatter and full core source-build/test exited zero in **3.565 seconds**:
**721/721 passed**, zero failures, errors, skips, timeouts or compiler diagnostics. All 56 source bindings
matched before/after and independent current bytes, formatting changed no source bytes, and both owned
process groups were absent. The run used macOS arm64/.NET 10.0.102 with `--no-restore` and
`UseSharedCompilation=false`. Final receipt:
`/private/tmp/issue779-csharp-worker-validation/attempt-6/receipt.json`, SHA-256
`1e744eb4b530760bd1f4a16cacf105ac8c790da3e38a9b66aadea7bc5fcb1fc4`; TRX SHA-256
`2d8103c39f29b8caa429f6fb5795117d0eaa79aabc90c91ad716bb8191ee98f5`. Earlier failed and
passing attempts remain separate historical records. The 15 lifetime barrier/cancellation cases and
56 recipe metadata cases create no actual Linux worker, account-cleanup grant or accepted proof. Native
monitor-error/finalization-error quarantine regression controls remain part of N01–N16; no native
settlement or coverage improvement was measured here.

### 2026-10-06: protected empty plan and authenticated control composition

Added the first private C# root control server for the genuine empty-targeted Observation procedure.
The [package API and ordering reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#authenticated-control-server-and-cleanup-continuity)
documents the exact native owners, one server claim, original task retention, bounded handlers, ACK commits
and separate descendant/control/worker joins. The server resolves actual retained protected policy and diff
bytes through the existing planner before construction. The allowed targeted profile must have no resources,
producers or obligations; a legitimate nonempty conservative profile remains required, and fallback rejects.
No application selection, producer execution, admission or proof is fabricated to make this checkpoint pass.

The private server implements READY/STOP/WAIT/EXIT only. The other five closed request forms remain rejected
pending producer and application checkpoints. STOP closes work before dispatching its one shared descendant
drain. Since this checkpoint exposes no descendant dispatch or external ledger registration, its empty join
observations are not assertions that the live worker exited. EXIT writes and closes its ACK before protocol
commit; the outer owner must subsequently join the actual worker and perform filesystem custody and strict
account cleanup. The reserved supervisor role remains closed until those native prerequisites are composed.

Transport continuity is now checked separately from active work admission. Per-request I/O cancellation does
not permanently invalidate a retained worker kernel identity. Root/account/workspace checks still require the
same actual references, original root and worker PID/start time, generated cgroup, consumed marker, named
inodes, descriptor hash and original deadline. Read-only workspace cancellation closes work admission without
creating new integrity quarantine; interrupted mutation remains quarantined. Existing quarantine is never
reinterpreted or cleared. Actual native rejection denies cleanup as well as new work. The socket factory itself
also binds the worker generation to its actual owner before exposing transport.

The portable sequence cases use actual task barriers and joined write claims; the empty-plan cases use genuine
planner policy and diff neighbors. Neither group constructs native ownership. The new generation comparison
control is detached data only. Source format/build/test and the actual Linux cancellation→cleanup/control-server
cases are recorded separately; source review cannot establish N01–N16 or physical/custody success.

The final scoped formatter and rebuilt full core test project exited zero in **4.705 seconds**, with
**772/772 passed**, zero failures, errors, skips or timeouts. A later case-insensitive log audit found
analyzer warning `xUnit2031`, missed by that run's scanner; the current custody validation includes the
assertion fix and verifies zero warning/error diagnostics. All 61 source hashes
matched before/after and independently verified current bytes; formatting changed no source bytes and
both owned process groups were absent. The run used macOS arm64/.NET 10.0.102, `--no-restore` and
`UseSharedCompilation=false`. Successful receipt:
`/private/tmp/issue779-csharp-control-validation/attempt-2/receipt.json`, SHA-256
`fe2674fd0193dad043550d85179016745acabb61b8b66e03e117f80ced6f87e2`; TRX SHA-256
`c8e35386eabd09169a99d2e6077b300c542d04c6b64bec4c9d6145f796c531d3`.
The first attempt's compile error and XML warning remain preserved in its distinct failed receipt, with
no executed-test claim. Independent source reviews identified and corrected read-only cancellation
quarantine, owner-generation factory binding, ACK timing, cleanup expiry and cancellation-error drain
gaps. Actual Linux cancellation→cleanup, write/close expiry, root authentication and native final custody
controls remain part of the mandatory checkpoint; these source/portable results create no native proof.

### 2026-10-06: terminal root tree custody and one teardown expiry

Implemented the native custody handoff after actual server I/O and worker settlement. The
[terminal custody reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#terminal-root-custody-and-shared-teardown)
documents the private native issuer, exact reference binding, complete fixed-tree preflight, retained
FD/name/hash checks, separate terminal ownership policy, and original-owner closure before issuance.
Account-associated nodes become root:root without changing live worker/subject path grants. Unknown
nodes or partial transfer preserve quarantine; a partial evidence set never authorizes publication.

The actual root holder retains its strict account-close task and descriptors through each deletion,
NSS absence and final native check. The original worker reservation is never cleared by a Boolean or
metadata receipt. Premature workspace/root-holder disposal cannot close handles used by the pending
transfer or account task. Root bytes and metadata remain data; structural manifest verification and
accepted consumer proof are additional gates.

The root owner now explicitly begins one monotonic teardown expiry. An actual worker claim keeps the
existing separate collection plus cleanup reserves, capped by the original job; pre-worker rollback
keeps only cleanup. Successful forward account utilities do not start whole-run teardown. Server,
worker settlement, custody and strict account deletion borrow the same remaining bound. Continued
worker exit observation uses cleanup-purpose native input/root checks instead of reopening active
admission. Per-unit stopping remains separately bounded and no later phase renews the root clock.

The new metadata, custody-procedure and scheduling controls are source-only tests. Their task barriers
and detached samples cannot construct an actual root holder, joined Linux worker or deletion authority.
The source validation receipt and actual Linux N01–N16 results must remain distinct. The supervisor
entry, producer/AppHost checkpoints, proof registry, unchanged coverage gate, QA and validated draft
PR remain pending.

**Current custody/teardown source validation:** exact-file formatting and the rebuilt full core test
project passed **897/897**, zero failures, errors, skips, timeouts or warning/error diagnostics in
**6.333 seconds**. All 68 source hashes matched before/after and independently verified current bytes;
formatting changed no source bytes and all three owned process groups were absent. The run used macOS
arm64/.NET 10.0.102, `--no-restore` and `UseSharedCompilation=false`. Receipt:
`/private/tmp/issue779-csharp-custody-validation/attempt-3/receipt.json`, SHA-256
`d7439a39401750c0defc5dab8031c406660aa3e1870306d7cb04c71468097fb6`; TRX SHA-256
`12c653f3afa20b2a47dca388b5200db3eaa2a3c865555357b68cdea1a807cd4e`.

Earlier attempts remain separate: attempt one passed 894 cases but retained an xUnit analyzer warning
missed by its original case-sensitive diagnostic scan; the assertion and scanner were corrected.
Attempt two passed the same 894 cases without diagnostics but predates the final original-task retention
and late-expiry propagation changes. The final run includes 59 custody metadata controls, 40 custody
procedure controls and 26 teardown scheduling controls. These construct no native custody holder.
Independent source review corrected cancellation reaching already-pending monitor/accept I/O. Native
Linux I/O abort/join, FD sealing, account deletion and the N01–N16 checkpoint remain unexecuted.

### 2026-10-06: complete empty-run source composition

The private [empty Observation root execution](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#empty-observation-root-execution-and-final-files)
now composes actual protected inputs, owner activation, empty plan resolution, accounts, workspace,
listener, same-image worker, authenticated control server, natural exit, physical joins, root custody,
final-file verification and strict account deletion. Success returns only after all original tasks and
native/local closes settle within the captured original monotonic remainder. The public supervisor
role remains closed while trusted OS bootstrap and the mandatory Linux checkpoint are pending.

Workspace account retention begins before directory mutation. Failed pre-worker preparation cannot
delete reusable accounts while preserved directories still belong to them. This implementation has no
startup-failure partial-custody fallback; those paths/accounts remain quarantined. Root final-file
verification requires successful protocol completion and natural zero-status worker exit, plus complete
canonical plan equality to the real protected planner result. The empty informational manifest and exact
seven-field summary can establish no admission, lease, consumer proof or Trusted claim.

The fixed startup contract now requires `/usr/bin/env -i`, five ordered assignments and the unchanged
runtime/entry/role/request argv. Paths reject command-expansion syntax; the runtime operand also rejects
`=` so env cannot consume it as an assignment. Positional entry/request `=` neighbors remain allowed.
Trusted native-runtime bytes/format, OS loader/locale/NSS and manager/bootstrap configuration still must
be established before invocation. Matching managed metadata cannot retroactively prove that prerequisite.

Independent source review found a final-close ordering bug: owner disposal deliberately cancels borrowed
teardown tokens, so checking that token afterward would reject ordinary success. The token is checked
before owner disposal; afterward the actual run uses its previously captured monotonic remainder. Three
timing controls distinguish deliberate disposal cancellation from real expiry/regression. No new allowance
or completion authority is created by those portable controls.

**Final source validation:** exact-file formatting and rebuilt full core tests passed **982/982** across
29 classes, zero failures, skips, timeouts or compiler/analyzer warnings/errors in **5.941 seconds**.
All 72 before/after hashes matched, no formatter changed bytes and all three owned process groups were
absent. macOS arm64/.NET 10.0.102 used `--no-restore -p:UseSharedCompilation=false`; native execution was
not attempted. Receipt `/private/tmp/issue779-csharp-empty-observation-validation/attempt-3/receipt.json`,
SHA-256 `5fe5fe15f7573dc034b59141227486edd71f2b8309d6a1e76b1a311e4108bfcc`; TRX SHA-256
`06f6fdedbd86a77776c739827662d2f315d75233cb8bc3e9766857837915584c`.

Attempt one is retained separately: 975/976 with a null-input fixture failing before its intended guard.
The test-only correction passed 976/976 in attempt two. The final bootstrap operand correction and six
additional owner controls are included in attempt three. The source tests issue no actual root holder.

**Next bounded work:** establish the fixed trusted OS bootstrap and bind the same product entry to a
source-bound Linux checkpoint-one fixture, then execute every N01–N16 control. Do not introduce producer
or application dispatch to work around that checkpoint. Checkpoints 2–4, Python runtime cutover, complete
consumer/platform proof, CLI/SDK QA, unchanged coverage gate and the validated draft PR remain required.


### 2026-10-06: guarded same-image CLI candidate and worker bootstrap

The reserved supervisor entry now invokes the [actual empty Observation composition](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#empty-observation-root-execution-and-final-files)
before ordinary configuration. Unsupported or non-root invocation rejects before protected input,
account or unit setup. On success it emits only five canonical fields copied from the verified returned
manifest: Mode, ClaimKind, Eligibility, ExecutionVerdict and CleanupCompleted. This is the guarded
checkpoint candidate, not native qualification, runtime cutover or Trusted enrollment.

Both roles now use the fixed environment-clearing bootstrap: env, -i, five ordered assignments, the
pinned runtime and same managed entry, then the fixed role/option/path. Worker still uses its existing
4096-byte deployment boundary and 100-byte socket bound; supervisor operands are bounded to 4095 bytes.
Runtime '=' cannot become an env assignment, while canonical positional entry/control '=' values are
allowed. All thirty worker properties, separate identities, 64-task/one-GiB ceilings, capabilities and
path grants remain the same. Type=exec confirms initial env execution; managed readiness still requires
the actual authenticated control exchange. Native runtime format/bytes and the trusted OS loader,
manager, locale and NSS prerequisites must be established before CLR startup.

**Source validation:** four exact-project format commands covering six files, rebuilt full core tests
**987/987** and CLI entry tests **10/10** passed. All six numeric exits were zero, with no failed/skipped
cases or compiler/analyzer diagnostic lines, no timeout and all owned process groups absent. The
combined run took **22.829 seconds** within 180 seconds. All 72 before/after/current source hashes
matched; formatting changed no bytes. This was macOS arm64/.NET 10.0.102 source validation, not Linux
execution. Receipt `/private/tmp/issue779-csharp-role-entry-validation/attempt-1/receipt.json`, SHA-256
`a155936c8c9eebc31892b38b5c3ccaf76e95a57f0d1af8bcd24e009353600bfd`;
core TRX `2e93a99dae203ab52101aac627ca389637722a99272241097df459bd2d307485`,
entry TRX `2a01879d9dc85bb7b46edf921eee46b547119526ec625aa5b94fc1f7cd2c67d1`.
Independent reviews covered the actual entry/composition and shared argv recipe.

**Built CLI QA:** five bounded processes passed ordinary help, both reserved help paths, unsupported
supervisor rejection and malformed-role rejection. Both failures returned exit one without success
stdout or the supplied private path. Receipt `/private/tmp/issue779-csharp-role-entry-validation/cli-qa-attempt-2/receipt.json`,
SHA-256 `9376d439debc3228c7185c09f5d51acf22247795360e13fef3a9d5f20cd6d899`.
An earlier QA receipt is retained: ordinary help exited zero, but the harness expected `Usage:` rather
than CliFx's actual `USAGE` heading. Only that QA expectation changed; no product correction or new
compiler command was needed. The corrected five-case run completed in 0.337 seconds.

The branch HEAD alone omits the untracked Supervision projects. A fresh native fixture must use the
complete reviewed source set and normal dependency graph. Historical Python workflow success cannot
supply this proof. Next prepare that capture and trusted OS bootstrap, execute N01/N02 to expose actual
setup issues, and then finish every mandatory N01–N16 case. No subset opens the checkpoint exit gate.
Checkpoints 2–4, complete platform/consumer proof, unchanged coverage gate, QA and the validated draft
PR remain required.

### 2026-10-10: N12 private same-image descendant source

Prepared the closed N12 same-image output-holder procedure on the integrated C# graph, described in the [Supervision reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#n12-leader-exit-with-an-inherited-output-holder). It exercises natural leader exit followed by real original-unit stop/group/pump joins. The ordinary build remains `None`, has no child or observer, and rejects the private role before configuration. No new admission factory, compiled workload registration, capability or consumer proof is created.

The private intermediate record explicitly precedes custody/account closure. Native N12 completion requires the actual post-stop group/EOF evidence **and** final root success/cleanup and complete source/deployment binding. This source-preparation entry is not a Linux result, a numerical coverage result or checkpoint completion.


## Private cancellation failure visibility

The N08/N09 failure-only [joined output diagnostic](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#private-pre-ready-worker-failure-diagnostics) preserves the actual worker stderr already retained by the original collector when READY/kernel/custody capture is unavailable. Its detached record establishes no native checkpoint or physical settlement. The unchanged first failure and native validators remain authoritative; the next cancellation attempt must diagnose any retained worker error before an evidence-supported execution fix or control credit.

### 2026-10-10: N11 synchronous worker factory source integration

Integrated the N11 synchronous stall into the common source graph using the compile-owned
`EvidencePrivateQualification=N11` selector. The [protected CLI reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md)
documents its placement after authenticated READY inside the existing tracked Admission callback.
Ordinary `None` continues through the normal protected input resolver. The deliberate `Thread.Sleep`
was not run locally, and this source-only integration establishes no native failure-settlement result,
checkpoint completion or consumer proof. The N11 failed-settlement codec and compile-selected failure-only
emitter are now present in the [Supervision source](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#private-n11-failed-settlement-observation).
Capture requires the original authenticated server's committed READY and completed I/O/handler joins;
its server owner guard grants no worker physical settlement or filesystem custody. The observation preserves
failed/false settlement data after the original lifetime and pumps join. Focused local checks passed 87
executions with zero compiler warnings/errors. The deliberate stall and actual Linux containment/custody
remain required; no native checkpoint or Linux result is claimed.


### 2026-10-10: cancellation fallback finalization evidence

The latest private N08 run retained the actual caller and lifecycle cancellation plus owned-work join,
but did not establish the complete root settlement/custody control. The private fallback now uses the
[original failed-settlement codec](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#private-n11-failed-settlement-observation)
when committed READY and the original joins exist. It copies monitor, lifetime, pending start, terminal,
cgroup and charged-pump data from the original holders before account/custody closure. Its new fixed
cancellation schema preserves false settlement and creates no native authority or acceptance. A failed
capture before output retains the existing pump-only fallback; a partial attempted write forbids a
second large diagnostic. The original failure, native success/custody guards, quotas and deadline remain
unchanged. Local codec/build checks and the next genuine Linux result must be recorded separately.

#### N08 capture-stage diagnosis

The fifth private cancellation run retained original physical settlement, joined monitor/pumps and normal nonzero worker exit, but rejected its cancellation capture. That evidence does not identify the failed kernel, signal or encoding guard. A separate [closed projection checkpoint](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxCancellationProjectionFailure.cs) now records the original failing operation without changing its guard or replacing the [first execution failure](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/EvidenceNativeObservationFailure.cs). This is diagnostic preparation; N08/N09 remain unqualified until the genuine Linux cases satisfy their full original records, custody and strict account cleanup.


### 2026-10-10: N16 accepted blocked-work source preparation

Added the compile-owned private N16 selection and a fixed operation-only authenticated workload request in the same-image empty-observation server. The server registers work before the blocked body and sends an intermediate acceptance frame while the original request remains pending. Separate authenticated STOP and WAIT handlers overlap that blocked request before the original stop sequence releases and joins the body; the final workload response requires the actual join. The private worker retains all three original I/O tasks and joins them on every exit; a private stop-start rendezvous handles WAIT arriving first under the existing request lifetime. Added deterministic registry/sequence and closed-protocol controls plus qualification documentation. Ordinary `None`, production dispatch, budgets, deadlines and custody guards remain closed/unchanged. This is source preparation only: no compilation or Linux N16 native acceptance was run, and the N16 checkpoint remains mandatory on the supported Linux image.

### N16 final cleanup and admission ordering

The private [accepted-work checkpoint](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#n16-accepted-blocked-work-with-concurrent-stop-and-wait) registers and synchronously starts its fixed body under the original owner admission gate. Every managed teardown/containment admission close uses the same gate; this removes the check-before-registration race without holding a lock across an asynchronous wait. The pre-custody event and kernel records require an actual committed EXIT. Final custody/account/handle closure and the original remaining deadline must also succeed before the root attaches `issue779-accepted-work-root-cleanup-v1` to its unchanged negative outcome. Root exit 1 alone proves neither expected cleanup nor native success. Detached JSON controls remain encoding tests; real Linux source-bound execution and independent account/custody checks remain required.
### N13/N14 accepted-work owner-death source seam

The private `N13`/`N14` compile variants reuse the existing authenticated, registered N16 blocked-work request, but hold its original request and server body after the committed intermediate reply. A single fixed root stdout phase line is emitted after that reply write completes so a future native harness can synchronize without a caller selector. These variants send no STOP, WAIT or EXIT and do not simulate owner death. N13 requires an externally delivered SIGKILL to the identity-pinned owner and actual dependent-unit termination; N14 relies on the existing systemd owner runtime deadline/stop policy after an externally delivered SIGSTOP. The existing original deadline, owner/worker properties and cleanup rules remain in force. This source preparation provides no native proof; the external harness, exact process/cgroup checks and strict account/workspace custody receipt remain unimplemented prerequisites. See the [N13/N14 implementation notes](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#n13n14-accepted-work-held-for-external-owner-death).

### 2026-10-10: N15 pending-start source preparation

The private `EvidencePrivateQualification=N15` source branch now holds the original `StartWorkerAsync` task after the real `StartTransientUnit` call has completed and its job object path has passed the existing validator. It emits one bounded source-owned stderr trigger containing only the actual selected unit and validated job path, then awaits the exact startup token already passed into the call. `SupervisionPendingStart` therefore retains its reservation and does not report `StartJoined`; cancellation or owner death remains a sticky failure, not successful startup. No backend, DBus reply, identity, READY request or owner control is synthesized.

This is source preparation, not a complete N15 fixture. The actual composition binds/listens on the worker control socket before launch, but creates and starts the accepting control server only after `worker.StartAsync` returns. With the barrier active, the actual worker may connect into the listening backlog and wait for its handshake response; its existing 30-second Admission handshake timeout starts in the worker and may precede the external fixture's PID check. Native N15 needs an independently reviewed way to verify the real `MainPID`, start time and cgroup within that original interval, or a separately designed compile-private preconnect hold. No synthetic READY, added allowance, or native execution is claimed. Only source edits and static path/hash/mode metadata were prepared; no build, restore, formatter, tests or CI were run.

### 2026-10-10: common source composition and local validation

Integrated N07 retained-parent substitution, N09 retained-allocation cancellation, N10 pending-start cancellation, N13/N14 accepted-work owner death and N15 pending-start owner death with the existing N04/N08/N11/N12/N16 variants. The ordinary build defaults to `None`; all eleven private selectors are mutually exclusive. The N07 and N15 source resolutions received independent reviews. Each private image is selected through the existing `EvidencePrivateQualification` MSBuild property and introduces no runtime selector, admission grant or successful Linux receipt.

The ordinary source build passed **1,366 Supervision tests and 198 focused CLI tests** on macOS arm64. Two locked restores, two scoped whitespace formatters and both source-built test commands exited zero in 33.496 seconds. Formatting and locks were unchanged; no compiler diagnostic or timeout was recorded. The receipt is `/private/tmp/issue779-csharp-integrated-lifecycle-73zmpq7b/integrated-validation-attempt-1/receipt.json` at source commit `f5be776aadbf40c96dddc05b989017752c719413`.

The four local compile-matrix attempts also produced passing TRXs for all eleven private variants and a final ordinary `None` build. These are source/metadata controls, not real systemd qualification. Attempts 1, 3 and 4 retained wrapper exit one with `PermissionError` during final process-group inspection even though their test commands returned zero; attempt 2 completed normally. Later selected-group observations found no remaining processes, but do not rewrite the original finalization failures or establish their earlier cleanup checkpoint. Preserve those failed receipts when assembling the final validation record.

The subsequent changes to the selector return documentation and this plan contain no executable behavior change. The checkpoint-one exit gate remains **all sixteen genuine Linux controls on one final integrated revision with zero skipped native cases**. Producer/artifact custody, application/SDK parity, Python runtime retirement, the unchanged coverage gate and a validated draft PR remain required.
