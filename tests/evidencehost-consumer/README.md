# EvidenceHost consumer proof (provisional)

This fixture tests the Linux mechanism proposed for [issue #779](https://github.com/forge-trust/AppSurface/issues/779).
The [consumer acceptance record](../../docs/evidence/issue779-consumer-acceptance.md) remains the release prerequisite.
Neither a passed fixture nor its uploaded JSON grants Trusted admission. The preliminary mechanism fixture has no EvidenceHost lease API,
protected verifier, producer catalogue, or downstream gate. Public provider APIs remain provisional until a real
consumer demonstrates the [approved boundaries](../../docs/designs/issue-779-evidencehost-trust-boundary.md).

The separate [runtime candidate](RuntimeSubject/README.md) exercises production CLI Observation orchestration,
including authenticated broker communication and retained-handle output. Its [workflow](../../.github/workflows/evidencehost-runtime-proof.yml)
is candidate-controlled and supplies no protected-base or Trusted acceptance. The [runtime proof driver](runtime-proof.py)
requires exact missing-proof rejection and structural verification of the collected plan/manifest. These tests and
the preliminary mechanism observations have different source and execution bindings; one cannot substitute for the other.

## Run

On a disposable Ubuntu 24.04 VM with systemd 255+, cgroup v2, Python 3, a C compiler, .NET 10 and passwordless
noninteractive sudo, from the repository root:

```text
bash scripts/verify-evidencehost-linux-mechanism.sh /var/tmp/evidence779-proof-results
```

The absolute report directory must be new. The script builds an explicit [worker entry](Worker.cs) in a temporary
directory outside repository MSBuild discovery, then runs the [allocation probe](allocation.md) and
[supervision probe](linux-proof.py). Unsupported platform controls fail; there is no skipped-success result.
The [fixture verifier regression checks](test_linux_proof.py) require the exact twelve subject results and
Boolean `true` values, rejecting missing, extra, false or truthy non-Boolean observations. Run those checks
independently with `python3 -B tests/evidencehost-consumer/test_linux_proof.py` on any Python 3 host.
The [candidate workflow](../../.github/workflows/evidencehost-mechanism-proof.yml) runs on a disposable hosted VM
with read-only repository permission, pinned actions and no repository secret projection. It observes candidate
code on PR events and is deliberately not an authenticated protected evidence gate.

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

For direct CLI/Aspire consumer-path regression tests, use the separate
[root-owned protocol fixture](ExecutionBroker-README.md). Its subject results and cgroup
metadata are synthetic; it does not replace the actual systemd or consumer CI proof.

Record immutable real CI run/revision/artifact identifiers, rerun from a reviewed protected workflow/base,
then integrate the actual protected worker and restricted subject. Prove exact catalogue/context binding,
admission before callbacks, host stop/join ordering, retained-handle writer hashing, aggregate budgets and the
protected downstream gate against stale/forged/missing manifests. Windows and macOS stay excluded.
See the [acceptance matrix](../../docs/plans/issue-779-evidencehost-test-plan.md) for all remaining caller and
release proofs. This fixture selects no Docker runtime and does not establish sandbox attestation.
