# Internal root Linux application lane

[`evidencehost_linux_application.py`](../../scripts/evidencehost_linux_application.py) prepares the root half of the
[v2 application protocol](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#internal-restricted-application-protocol-v2-prerequisite).
It is an internal source prerequisite. Production registrations are an **empty immutable tuple**; selection returns
`ASEVD407`. Candidate audits, JSON, uploaded receipts and matching metadata cannot enroll or issue a lease.
No portable control establishes root/systemd enforcement, native admission, a Passed proof or Trusted eligibility.

## Selection and immutable data

`audit_candidate(canonical_entry, entry_digest=..., catalogue_digest=..., policy_sha256=...)` audits exact bounded
C# canonical definition bytes and returns frozen data. It hashes the original bytes, preserving the C# digest rather
than approximating its serialization. The complete protected policy/profile, resource and producer declarations,
bundle and capabilities remain in those bytes. Qualification must also run the
[closed C# catalogue audit](../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md#internal-closed-application-catalogue-prerequisite).
The root account owner supplies `Identities`: three distinct positive UIDs and five distinct positive GIDs.
`CandidateAudit.descriptor(ids)` returns a fresh 14-field application metadata copy; it grants nothing.

`select_registration(id, digest, policy_sha256=..., profile_id=...)` selects only an entry already contained by
identity in the private compile-owned tuple. Only that selected object may construct `RootApplicationLease`.
There is no enrollment, transport, callback, caller argv, endpoint, or runtime registration API.

`audit_bundle(path, candidate, deadline=None)` retains actual no-follow directory/file descriptors and verifies the complete
regular single-link inventory, root ownership, immutable modes, lengths and hashes. Bounds are 256 files,
128 MiB/file, 512 MiB total, 4096 tree entries and depth 32. Rechecks compare opened and named identities.
The optional deadline is the caller's existing finite monotonic job/stage deadline, checked on entry, each directory,
file, read chunk and final descriptor recheck. It cannot reset the budget; `None` is the default data-audit mode.
Each pinned bundle retains an immutable entry digest and complete file declaration tuple. `require_binding(candidate)`
requires exact equality; `verify_candidate(candidate, deadline=...)` independently rehashes named bytes and matches
held identities. Root construction checks the selected binding, and start rehashes before watchdog/Popen.
The launcher retains the bundle until all application work is joined. On Linux x64,
opens use mandatory `openat2` with no syscall fallback. The explicit nonroot owner seam and component opens on
other platforms are read-only portable audit procedures and cannot create a root lease.

## Root launcher contract

The launcher constructs `RootApplicationLease(selected, identities, workspace, pinned_bundle, dotnet,
job_deadline, JobOutputCounter())`. It owns account creation/deletion, worker authentication, protected selection,
workspace creation/mounts, overall cancellation and aggregate finalization. All accounts remain reserved through
retained completion collection and successful completion close.

`Workspace.parent` is a fresh `/run/issue779-app-<32 lowercase hex>` directory, root/root `0711`; its name supplies
the lease ID. Its `control` child is root/root `0700`; `bundle` is root-owned read-only; `scratch` is application
UID/GID `0700` on a **host-visible size-bounded tmpfs** no larger than `ScratchBytes`. The launcher must create
and pin that mount before arming; declaring a limit without enforcing it is insufficient. Root must create the
parent exclusively. It must not reuse a previous workspace or place bundle bytes under the producer-owned
scratch parent. Tools, protected output, broker control, producer scratch and original subject checkout are
separate denied roots. The canonical platform dotnet executable lies outside those denied roots and the workspace.

The finite closed kinds are `RestrictedAspireAppHostV1` and `NativeHttpResourceV1`, compatible with the current
[AspireChild payload](AspireChild/README.md). The AppHost role is a **top-level DLL** and receives exactly
`--scratch <fresh scratch> --case normal`. Its binary derives audited `resource/NativeHttpResource.dll` itself;
the resource uses `scratch/http.sock`, `/health`, and exact `native-http-ready` body. Other controls/general AppHosts
are unsupported. The cleared environment carries only fixed scratch/store/telemetry settings and role-specific
qualification bindings. `MaximumTasks` is an exact selected declaration from 1 through 128, preserved in the
descriptor and `TasksMax`; 129 is rejected. A definition selecting 64 still emits 64. Supporting the explicitly
qualified SDK task limit does not enroll a candidate or provide a runtime/environment override. Memory,
scratch and output bounds remain independently selected and unchanged. The environment carries the fixed
`PROOF_PROTECTED_TOOLS`, `PROOF_PROTECTED_OUTPUT`, `PROOF_ALLOWED_INPUT` qualification bindings. The independently
root-inspected probe tools must contain the actual managed `protected-tool.dll`; absence is not denial proof.
`audit_protected_probes(workspace, deadline=...)` opens the existing fixed denial roots and tool through no-follow
FDs, requires root ownership and no group/other writes, and requires a regular single-link tool of at most 64 MiB.
It reads bounded chunks, checks stable named/opened identities, validates MZ/PE headers, mapped CLR and metadata
directories, and the BSJB signature without loading the assembly. It never copies, executes or downloads a tool.
The read-only owner parameter exists solely for portable file controls and cannot issue a lease.
Root checks the fixed `native-resource-output-probe` is absent with no-follow stat before start, after authenticated
health and after physical exit at join. Files, directories and dangling links all count as present. Tool content and
fixed-root identities must still match the initial snapshot, so a missing/invalid tool or preexisting output probe
cannot establish denied access. Protected output is the actual root-owned outer output parent; worker-owned run
anchors can exist beneath it. All probe failures become fixed ASEVD410 and latch failed ownership through the
start/readiness/join guards, with no underlying exception text in a response.
The pinned payload must retain its real DCP and resource dependencies and implement the documented Aspire store
and DCP overrides. No direct-process, namespace, syscall, capability or budget fallback exists.

## Lifecycle and integration ordering

`start(id, entry_digest, deadline)` records a pending one-attempt lease before launching. The closed latch, Popen
and retained process assignment share a lock, preventing a stop-before-spawn escape. An independent root watchdog
must ACK its actual live PID and root UID before root writes `armed`; the application inherits no watchdog descriptors.
The private bounded Pipe protocol is `READY:<pid>:<uid>`, root `DISARM:<pid>`, and matching `DONE:<pid>`.
The watchdog must remain live through physical unit/process exit and both pump EOFs. Root sends DISARM only then,
requires matching DONE and joined exit zero, and rejects any prior clean or nonzero monitor exit. Abort, deadline,
parent loss, invalid request or broken Pipe cannot acknowledge disarm; the production watchdog performs bounded
fixed-unit emergency teardown and exits nonzero. Startup metadata alone cannot establish clean disarm.
Disarm rechecks the deadline and abort after the actual watchdog join. Final receipt publication additionally
rechecks the existing deadline, abort and failed state after bundle/probe inspection, before caching any receipt.
Fresh loaded `exec`/inactive/dead/PID-zero/zero-exit metadata is **pending**, never execution or exit acknowledgement.
Startup requires actual running MainPID, exact UID/GID four-tuples, no-new-privileges, zero effective capabilities
and unified kernel cgroup membership. The generated unit is `issue779-app-<lease>.service`.

The start ACK has exactly seven fields: `ok`, `lease_id`, `apphost_pid`, `application_uid`, `application_gid`,
`cgroup`, `owned`. `resource_wait(lease, resource_id, deadline)` uses a fresh root-owned UDS, SO_PEERCRED, exact
resource image/argv and kernel identity checks plus at most 4096 HTTP bytes. Its ACK has exactly nine fields:
`ok`, `lease_id`, `resource_id`, `application_uid`, `cgroup`, `kernel_peer_checked`, `http_status`, `healthy`,
`received_bytes`. Child readiness text, AppHost output and health alone cannot substitute for the peer check.

`stop(deadline)` closes admission and sends main-only TERM. `join(deadline)` settles active operations, stops/KILLs
the entire generated group when necessary, reaps the launcher, requires both actual error-free pump EOFs and exact
received-byte counts, then disarms/joins the watchdog. It returns a separate `(received, stdout, stderr)` app receipt.
The caller supplies one shared monotonic cleanup deadline; stage/job bounds cannot be reset by nested operations.
Any failed/unconfirmed exit retains quarantine; cleanup/account deletion cannot upgrade it to success.
The internal `OwnershipState.joining` guard latches the first exceptional join before setting abort. A timeout remains
failed even when later cleanup could finish: retries rethrow the first internal failure before running cleanup or
returning a receipt. Exception text is never a protocol diagnostic. `WatchdogOwnership` and `watchdog_control`
are private process/Pipe procedure seams, with no registration, unit-operation or lease authority. The root lease
always requires startup UID zero; the portable startup UID parameter only validates a local fork's actual identity.
`PinnedBundle.close()` attempts every retained descriptor exactly once even when an earlier close fails. It retains
and rethrows the first internal close failure on repeated calls; a descriptor with uncertain close state is never
retried because its number could be reused. The launcher must require successful close before unmount/removal
and retain quarantine/accounts on failure. Successful repeated close is idempotent; close error text is not JSON.

**Keep producer and application ownership separate.** Intermediate artifact collection requires producer groups
and producer pumps empty; the application may remain alive and has no results/output access. Final broker stop,
wait, exit and completion must join both sets, all handlers and pumps. Share the typed `JobOutputCounter` with
producer budgets, and include the separate app receipt when reconciling the final job total. Do not add the live
application unit to the producer-only artifact-transfer predicate.

## Portable controls and remaining prerequisites

Run `python3 -B tests/evidencehost-consumer/test_linux_application.py` only within the authorized portable gate.
Controls use actual local descriptors/modes, real pump threads, deterministic state barriers, private fork/Pipe
handshakes and pure parsers. The protocol-only fork wrapper has no emergency systemd operations.
They never patch the empty registry, start systemd, execute a payload, or create a positive lease.
Native prerequisites remain: qualified compiled C#/root registrations and exact digest bytes, pinned Linux SDK
payload, actual root accounts/workspace/tmpfs/watchdog, kernel identity/readiness, producer/app aggregate joins,
protected/fork/downstream consumer acceptance, and an independently reviewed launcher integration.
