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
