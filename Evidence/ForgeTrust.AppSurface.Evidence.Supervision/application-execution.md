# Restricted application ownership

The [C# root composition](LinuxEmptyObservationExecution.cs) owns an application in addition to
its [worker](README.md) and [coverage producer](producer-execution.md). All three execute on the
same Linux machine in separate systemd units. The worker hosts either the CLI adapter or the
[Aspire SDK adapter](../ForgeTrust.AppSurface.Evidence.Aspire/README.md). Neither adapter starts
an AppHost in its own process.

## Selection and immutable bytes

The protected supervisor request may contain `application_id` and `application_entry_digest`.
Both must be present together. They identify a registration already compiled into the
[closed catalogue](../ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationCatalogue.cs).
They cannot add a registration, command, environment variable, path grant or accepted proof.
Ordinary builds still contain no application registrations and reject selection with `ASEVD407`.

[`EvidenceApplicationSelection`](EvidenceApplicationSelection.cs) re-resolves the complete protected
policy and diff, then compares the selected profile, complete definitions, canonical entry and catalogue
digests and actual distinct account identities. [`LinuxApplicationWorkspace`](LinuxApplicationWorkspace.cs)
then audits every payload file's exact bytes, role and mode. It copies the selected bundle to a fresh
root-owned read-only generation outside the protected tool tree. Retained handles and fresh hashes
bind startup and readiness to the same selected inventory. A passing candidate audit is metadata only.

## Native process and readiness

[`LinuxApplicationProcess`](LinuxApplicationProcess.cs) retains ownership before dispatch. Its one
startup attempt and each declared readiness attempt run through the
[application lifecycle coordinator](application-lifecycle.md). Requests carry only an application
identity/digest or a lease/resource identity; they carry no executable or argument authority.

The [closed unit recipe](LinuxApplicationUnit.cs) invokes the selected top-level AppHost through the
audited runtime with a cleared environment. It sets the Aspire store below bounded application
scratch. The bundle fixes DCP and resource paths. Private networking, empty capabilities, no
supplementary groups, `NoNewPrivileges`, strict system protection, a finite runtime, and the selected
memory/task limits apply to AppHost, DCP and every descendant. The application cannot read the
protected tools/control roots or write worker/producer results. See the
[workspace rules](application-workspace.md) and [identity rules](application-accounts.md).

A systemd start reply does not establish running identity. The root authenticates live PID, all four
UIDs/GIDs, start time and exact cgroup. Resource readiness additionally connects to the fixed Unix
socket, authenticates `SO_PEERCRED`, retains the actual resource identity and DCP executable identity,
and reads at most 4096 HTTP bytes. Launch-argument bytes remain a consistency check; kernel executable identity and the retained DCP process lineage bind the responding resource to root-controlled launch provenance. Only status 200 with the exact fixed body and unambiguous content
length passes. AppHost log text and JSON health declarations cannot establish readiness.

## Shutdown and file custody

The root shares the existing run output budget across worker, application and producer pumps. Each
application also has its own compiled output cap. Bytes remain charged while draining after failure.
A live application is a retained workload even after its startup operation returns.

STOP closes admission, contains application and producer independently, joins the original starts and
readiness operations, and only then joins aggregate workloads. Application shutdown sends TERM only to AppHost and waits within the compiled stop cap and original cleanup deadline. If exit is unconfirmed, it requests unit stop and immediately KILLs all remaining members; it does not grant a second grace interval. Grace cancellation uses a separate D-Bus connection so forced containment can still authenticate a fresh connection.

The worker/control handler that requested
STOP is excluded from that descendant join. The application checks its stopped unit, physical cgroup
emptiness twice, both actual EOFs and error-free output reconciliation before releasing storage.
The external root OS lifetime contains managed or native calls that stop making progress.

The live application monitor borrows the original control identity and deadline, rather than permission to
admit new work. STOP closes that admission before native containment starts; using `RequireActive` in the
already-owned monitor could incorrectly turn that normal transition into a workload failure.
[`SupervisionApplicationMonitorState`](SupervisionApplicationMonitorState.cs) atomically orders observation
failure against intentional stop. Failure recorded before stop stays latched. A late result from an in-flight
observation after stop cannot retrospectively fail the live phase. This comparison state grants no authority:
the original monitor task still joins, and unit, group, pending-start, output and custody checks remain required.
The bounded `settled` diagnostic includes the closed ledger failure and monitor-failure bit so a negative
owned-exit reply is distinguishable from successful physical cleanup.

Scratch is unmounted and account-associated nodes are secured before the strict account owner deletes
reusable identities. An uncertain start, failed pump, failed unmount or unsafe retained name quarantines
those identities. Retrying a failed disposer cannot turn it into successful cleanup. The SDK's registered
disposer uses its existing joined cleanup phase, so it does not wait for its own task to complete.

## Validation boundary

Portable lifecycle, schema, recipe and metadata controls do not manufacture native ownership or
accepted coverage. The checkpoint-three Linux workflow runs the compiled private registration with
real AppHost/DCP/resource processes, positive tool/output denial probes, readiness failure, factory
stall, cancellation, a stuck descendant and disposal failure. Private images cannot be packed. Genuine
native receipts must bind the tested source revision and actual cleanup facts; preparatory or local
build success is not a Linux pass or a Trusted claim.

The [verified checkpoint-three record](../../.github/issue779-checkpoint3/README.md#verified-checkpoint-result)
binds all seven genuine controls to source `4ce55f54`, including CLI/SDK numerical coverage and the intended
disposal quarantine. This completes that checkpoint only; production enrollment and the full cutover/coverage
gate remain separate requirements in the [migration plan](../../docs/plans/issue-779-csharp-supervision-migration.md).

### Control-server preparation before worker dispatch

The root retains the control server, resolved plan, coverage owner and application owner before calling
`LinuxWorkerProcess.StartAsync`. This preparation uses the original job allowance, not the worker's
30-second connection/admission allowance. Creating the server establishes reference ownership only;
`RunCoreAsync` still requires the original joined startup and authenticates the live worker PID, all four
UID/GID values, start time and cgroup before accepting request bytes. A prepared server cannot supply READY
or an application lease without that later kernel check. Preparation failure prevents worker dispatch, and
its retained owners remain in the existing aggregate failure cleanup path.

The [native runner](../../.github/issue779-checkpoint3/run-native.py) retains the existing bounded C2
worker/producer failure prefixes in its private artifact as well as application prefixes. A closed root
`PeerCheck` failure alone cannot identify the worker's earlier error; diagnostic absence remains unknown.

### Bounded readiness rejection observations

`evidence-application-readiness-v1` reports fixed `resource-peer-validated` and `http-validated` checkpoints,
and at most the first `dcp-capture-rejected` record for one readiness operation. The latter contains a closed
`LinuxDcpCaptureStage`, closed error family, optional known direct errno, and bounded observed/matching process
counts. It contains no exception text, path, command line or candidate-controlled values. The actual Capture
overload returns these data on rejection while preserving its original exception, identity guards, cleanup and
deadline. This output grants no readiness, process ownership or retry permission. It is not a success receipt.

These diagnostics distinguish a stalled HTTP read from repeated DCP sampling rejection without changing the
HTTP response limit, resource deadline, cgroup/executable/proc guards, failure latch, stop policy or acceptance.
A diagnostic write failure is ignored and cannot replace the original operation result. Only the original
complete identity capture and peer-provenance check can produce the existing readiness ACK.

### Several processes from the audited DCP executable

The genuine pinned SDK run observed two live application members with the same root-audited DCP executable,
so executable uniqueness is not a valid assumption for resource selection. `CaptureForResource` still captures
every bounded cgroup member, authenticates each candidate's UID/GID tuple and start time, and compares its
actual executable descriptor with the retained root file. It chooses only the nearest such candidate in the
actual resource peer's kernel PPid chain, sampled twice through at most sixteen same-application parents.
No array order, raw PID field, argument text, log or first executable match selects the DCP owner.

Both parent walks must agree, all intermediate and candidate identities are rechecked, the complete cgroup
process list is repeated, and the selected executable and named root path are checked before transfer. All
unselected retained descriptors close on every outcome; the selected owner remains retained until joined
application shutdown. `RequireResourcePeer` repeats the same selected lineage at readiness ACK, before producer
dispatch and during monitoring. The original `Capture` entry without a resource remains a strict one-candidate
mechanism fixture. `FindNearestDcpAncestor` is detached comparison data only and cannot construct a live owner.
