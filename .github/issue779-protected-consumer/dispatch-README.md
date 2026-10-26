# Protected-parent dispatch driver (source preparation)

`dispatch.dispatch_protected_parent(context_bytes, token, *, dotnet_host, total_timeout=150)` is the fixed parent-side orchestration seam. It is a Python API, not a general command runner or user-facing CLI. The parent caller must independently source and protect every context fact, supply the API token explicitly (for example, after reading its one fixed `GH_TOKEN` secret), and supply its canonical absolute `dotnet` host path. The consumer DLL is fixed relative to this module at `semantic-gate/bin/Release/net10.0/EvidenceHost.ProtectedParentGate.dll`; neither executable path nor arguments can be overridden through context bytes.

## Closed context format

`context_bytes` is UTF-8 JSON, at most 24 MiB, with exactly these top-level keys:

| Key | Meaning |
| --- | --- |
| `schema` | Exact `issue779-protected-parent-dispatch-v1`. |
| `expectation` | Base64 of the frozen protected-parent expectation JSON bytes. |
| `protected_facts` | Exact `workflow_path`, `workflow_sha`, `workflow_source_commit`, `base_sha`, `subject_sha`, independently sourced by the parent. |
| `policy_bytes`, `policy_sha256` | Base64 original protected policy and its independently expected lowercase raw SHA-256. |
| `diff_bytes`, `diff_sha256` | Base64 original protected UTF-8 diff and its independently expected lowercase raw SHA-256. |
| `expected_gate` | Exact public `EvidenceProtectedGateExpectation` properties, including its explicit release opt-in boolean. |
| `artifacts` | Parent-collected byte bodies as `ProducerId`, `RelativePath`, and base64 `Bytes`; keys are comparison data and never opened as paths. |

The driver validates duplicate/case-aliased JSON keys, exact field sets, base64, bounded bytes and independent policy/diff hashes. It obtains plan and manifest bytes only from `github_parent.resolve_protected_parent`; context has no fields for substituting uploaded plan/manifest bytes. The expected gate run identity must equal `github-actions-{run_id}-{run_attempt}`, its subject revision must match the resolved run head, and its base revision must match the protected base fact.

## Fixed child protocol and bounds

The parent-level budget is one absolute monotonic deadline of at most 150 seconds, measured from dispatch entry through resolver, child, pipe closure, process-group termination/reaping, and final status. Four seconds at the end are reserved for cleanup inside that same cap; the resolver receives only the earlier remaining interval (still at most its 120-second limit), and child execution shares the remaining pre-cleanup interval. No cleanup step starts a fresh post-expiry timeout. The semantic JSON envelope is capped at 20 MiB. Policy, diff, plan and manifest are nonempty and at most 1 MiB each; there are at most 64 artifact bodies, each at most 1 MiB, including allowed zero-byte bodies.

The child argument vector is exactly `[dotnet_host, fixed_consumer_dll]`, with no caller arguments, `shell=False`, a new POSIX session/process group, and a restricted environment that excludes `GH_TOKEN`. The token is used only for resolver HTTP requests and is never written to child stdin, logs, or the JSON envelope. The parent sends stdin and drains stdout/stderr with nonblocking selector I/O under the pre-cleanup portion of the same absolute deadline; retained output is capped at 4 KiB per stream while excess bytes continue to be drained. This avoids detached pipe-pump threads. At timeout, overflow, or inherited-pipe hang, the dispatcher closes its pipe endpoints, terminates the owned process group, escalates to SIGKILL when needed, reaps the direct child, and confirms group disappearance using only the reserved tail before the original deadline. If that proof cannot complete, it returns fixed `semantic-gate-cleanup-unconfirmed` / `DISPATCH_CLEANUP_UNCONFIRMED`; it never reports a semantic result. The child accepts no command-line arguments and has its own 30 second input cancellation bound.

Only the four exact child diagnostic/exit combinations are surfaced after successful cleanup: exit 0 with stdout `EVIDENCE_PARENT_GATE_ALLOWED`, exits 1/2/3 with the corresponding fixed stderr diagnostic and empty stdout. Unexpected output, overflow, timeout, cleanup uncertainty, and launch/supervision failures map to fixed redacted driver statuses; raw child output is never returned. The real-process controls use a fixed ordinary helper process to fill both output pipes while consuming a payload larger than pipe capacity, and another helper that spawns a TERM-resistant descendant and retains the pipes. They check bounded completion and owned process-group disappearance; they do not invoke GitHub or .NET.

## Trust boundary

This driver is orchestration, not authentication. It cannot establish that its caller's expectation, protected facts, policy, diff, expected gate facts, runtime host, consumer DLL, or artifact-body bytes came from a protected source. The resolver's matching API evidence and hashes do not replace the parent's independent protected-source controls. The semantic executable's zero exit is only the public semantic gate's result for supplied inputs; it does not create admission, `Trusted`, sandbox attestation, or positive protected acceptance. The parent must keep all inputs and executable pins outside subject control. The local ordinary-child controls exercise process supervision, and the [semantic integration controls](test_dispatch_semantic.py) invoke the fixed .NET child with a detached resolver. They do not make real API requests or execute a protected workflow. The separate [protected entry point](entrypoint-README.md) remains unregistered until a reviewed record is committed on protected main.

The original dispatch tests mock the resolver and private child-process seam to test envelope construction, fixed argument selection and redacted result mapping. The added owned-process class uses real ordinary POSIX child processes. The semantic integration class mocks only the resolver and calls the actual fixed .NET semantic executable; its coherent public-data positive grants no runtime authority.
