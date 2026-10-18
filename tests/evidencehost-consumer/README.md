# EvidenceHost consumer proof (provisional)

This fixture tests the Linux mechanism proposed for [issue #779](https://github.com/forge-trust/AppSurface/issues/779).
The [consumer acceptance record](../../docs/evidence/issue779-consumer-acceptance.md) remains the release prerequisite.
Neither a passed fixture nor its uploaded JSON grants Trusted admission. The preliminary mechanism fixture has no EvidenceHost lease API,
protected verifier, producer catalogue, or downstream gate. Public provider APIs remain provisional until a real
consumer demonstrates the [approved boundaries](../../docs/designs/issue-779-evidencehost-trust-boundary.md).

## Current C# runtime

The legacy Python runtime candidate and root launcher are retired. Use the [C# cutover reference](../../docs/evidence/issue779-csharp-runtime-cutover.md), [same-product process roles](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#process-roles), and current [runtime validation workflow](../../.github/workflows/evidencehost-runtime-proof.yml). Checkpoints [two](../../.github/issue779-checkpoint2/README.md) and [three](../../.github/issue779-checkpoint3/README.md) exercise the actual C# producer/artifact and application owners. Their Python drivers are preparation and observation fixtures, not runtime authority.

The remaining older probes below are historical mechanism fixtures. Their execution wrapper/workflow is retired; their old JSON and observations cannot replace the current C# native controls or grant admission.

## Historical mechanism execution

The previous standalone mechanism wrapper and workflow have been retired. Their source and exact invocation remain in Git history before the [C# cutover](../../docs/evidence/issue779-csharp-runtime-cutover.md). The retained [allocation probe](allocation.md), [old supervision probe](linux-proof.py) and [fixture verifier tests](test_linux_proof.py) describe the earlier mechanism only. Run current acceptance through the [C# runtime workflow](../../.github/workflows/evidencehost-runtime-proof.yml).

## Mechanism and observations

The root launcher creates two distinct non-login accounts. Root owns the read-only tool files; the worker's
private group permits reads. The worker account can read the simulated protected
registration/verifier and write its private output directory. The subject account can read its declared input,
but must fail to read the registration/verifier, worker environment/file descriptor and launcher control file;
write final output; signal or ptrace the worker; or migrate itself out of its cgroup. The
[subject entry](subject.py) requires every denial and the neighboring allowed input to succeed. It receives only
PATH, HOME and LANG, without inherited CI credentials. A real integration must additionally exclude access to
the runner's token/control files, sockets and privileged executables and prove its exact runtime environment.

PID 1 arms each transient service's runtime deadline before starting the worker. `KillMode=control-group`,
`TimeoutStopSec=1` and `SendSIGKILL=yes` enforce termination independently of the worker callback thread.
The worker has no capabilities, cannot acquire new privileges and cannot write the control-group hierarchy.
These choices follow [systemd transient-service controls](https://manpages.ubuntu.com/manpages/noble/man1/systemd-run.1.html)
and [systemd termination semantics](https://manpages.ubuntu.com/manpages/noble/man5/systemd.kill.5.html).
Descendants inherit cgroup membership across fork, as specified by the
[Linux cgroup v2 contract](https://www.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html).

The scenarios cover a completed worker, cooperative stop with joined-work/disposal/failure markers, synchronous
configuration/verifier/factory/producer stalls, a stuck disposer, nonreturning .NET `FailFast`, and an owned
descendant that creates a new session and ignores SIGTERM while output pumps remain active. Failed stopping
must leave no unwind, completed disposal or final manifest marker, and the service control group must be empty.
Each scenario has a ten-second exit-observation deadline in addition to PID 1's four-second runtime and
one-second stop grace. Every control invocation also has its own timeout; the workflow has a five-minute bound.
Markers in this fixture are observations of the mechanism, not EvidenceHost manifest or API integration tests.

## Reports and failure behavior

`allocation.json` records the allocation probe. `supervision.json` records scenario results, elapsed time and
the tested kernel/systemd/architecture, always with `admission: none`. `artifacts.json` binds local report hashes
and the checked-out revision. The final artifact index is absent when either probe fails. Output is bounded for
control commands, whose fixtures emit finite small output; this is not proof of the production aggregate stream
quota. Arbitrary child errors are reduced to diagnostic categories rather than copied into the report.

Only fresh users, units and temporary files created by this run are removed. If worker or descendant exit cannot
be established, the probe fails; a real integration must quarantine output and cancel its job. The test does
not handle external topology or confer permission to reuse failed production output.

## Before public API freeze or Trusted support

### Retired Python root completion channel

The Python `launch_with_completion` API is removed. Its former descriptor, account and root-completion responsibilities now belong to the C# [retained report custody](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/report-custody.md) and [producer artifact owner](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/producer-artifacts.md). Historical source/receipts remain in Git history and are not current consumer proof.

### Standalone gate and Aspire child controls

The [protected gate consumer](ProtectedGateConsumer/README.md) is a separate
executable using only public Contracts APIs. Its counted standard-input message
contains a plan, nullable manifest and independently selected expected facts.
Its [external-process controls](test_protected_gate_consumer.py) check bounded
JSON ingestion, current-fact comparisons, Observation denial and fixed diagnostics.
A coherent synthetic positive checks API behavior; standard input and exit zero
do not authenticate the producer, expected facts or collected artifact bytes.
The protected parent must establish those bindings before using its decision.

The [Aspire child controller](AspireChild/README.md) prepares a real pinned
AppHost/DCP bundle with one [native HTTP executable](NativeHttpResource/README.md).
Its own root watchdog, systemd unit and independent Unix-socket peer checks
exercise startup, readiness and bounded teardown. The portable controls run the
resource DLL directly and check controller behavior; native Aspire/DCP/systemd
execution has separate prerequisites and results. This provisional fixture uses
one application/resource identity and has no admission capability. Shared-host
ownership, resource capability mapping and protected consumer acceptance remain
part of the approved implementation.

For direct CLI/Aspire consumer-path regression tests, use the separate
[root-owned protocol fixture](ExecutionBroker-README.md). Its subject results and cgroup
metadata are synthetic; it does not replace the actual systemd or consumer CI proof.

Record immutable real CI run/revision/artifact identifiers, rerun from a reviewed protected workflow/base,
then integrate the actual protected worker and restricted subject. Prove exact catalogue/context binding,
admission before callbacks, host stop/join ordering, retained-handle writer hashing, aggregate budgets and the
protected downstream gate against stale/forged/missing manifests. Windows and macOS stay excluded.
See the [acceptance matrix](../../docs/plans/issue-779-evidencehost-test-plan.md) for all remaining caller and
release proofs. This fixture selects no Docker runtime and does not establish sandbox attestation.
