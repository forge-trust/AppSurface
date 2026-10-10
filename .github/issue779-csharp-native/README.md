# N11 original stalled-worker capture

This private fixture preparation uses the original [C# failed-settlement holder](../issue779-csharp-n11-failed-settlement-source-capture-v2/execution/repo/Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxFailedSettlementObservation.cs) and the [N11 requirement](../issue779-csharp-n11-failed-settlement-source-capture-v2/execution/repo/docs/plans/issue-779-csharp-supervision-migration.md#required-controls). It changes no product, runtime authority, policy, deadline, or production registration.

The root bootstrap basename allowlist includes underscores for fixed N11 parser filenames such as `check_kernel_record.py`; it still rejects path separators, dot components, and names longer than 255 bytes.

`parse_original(frame, descriptor, *, request, generation, source, base, entry_sha, policy_sha, runtime_relative_host, workflow)` consumes detached bytes only. `request` supplies the original request bytes, whose UTC job deadline must match the descriptor as an instant with all seven fractional digits retained. The parser requires the fixed three-line frame, complete original failed-settlement data, exact descriptor fields and reviewed fixed fixture declaration. Full worker streams are decoded canonically and checked against their original full-byte digests, EOFs and received counts. Failed lifetime and monitor state stay failed; this function issues no admission, completion receipt or native acceptance.

`capture(private, generation, source, base, entry_sha, policy_sha, runtime_relative_host, workflow, end_ms)` runs only as root against the fixture's exact generated `/run` paths. `end_ms` is the fixed fixture-harness BOOTTIME observer cutoff selected before launch, with the existing five-second helper termination/reap reserve; it is separate from the C# job deadline and creates no runtime allowance. Reads retain no-follow descriptors and compare names, metadata and byte counts before/after. The capture binds the original root-owned request bytes to the descriptor before reading outputs, and requires no output slot or raw result, absent original process IDs, fresh empty/pruned generated worker and owner groups, and retained quarantine accounts. It writes only fixed evidence names with exclusive root0600 files, then closes all its read descriptors and checks the observer cutoff again. It never stops a process, deletes a path, activates a writer or releases an account.

The fixture must still prove the immutable deployment, authenticated original root/worker identities, actual root launch and nonzero exit, genuine original stop under its existing bounds, complete stream collection, its own subprocess joins and final cleanup. The starttime used for consistency comes from the original source-bound retained holder; it is **not** a second independent pre-exit sampler. Original JSON cannot supply any missing native provenance. An emergency outer fixture kill is containment failure and cannot substitute for the original stop. No native run has occurred for this packet.

The entry `capture_stall.py` uses isolated Python only after four fixed module files are SHA/mode checked and copied to a root0700 module directory. Python reads and retains test evidence; the product C# supervisor retains all runtime security and termination decisions. Missing fields, incomplete streams, unexpected paths and failed closes reject; nothing is converted into a positive completion.

The portable controls use detached records and owned temporary files. They validate consistency and real no-follow/exclusive file behavior without root, systemd, admission or Trusted claims. Native cgroup, NSS and final account disposition require the Linux fixture. Do not treat portable passing results as N11 acceptance or branch-coverage proof.


## Request deadline and fixture observer cutoff

`capture(..., end_ms=...)` retains one harness-selected BOOTTIME observer cutoff.
The fixed pre-launch 240-second cutoff and five-second helper termination/reap
reserve remain unchanged. This cutoff is separate from the C# job and cumulative
teardown deadlines; it never supplies, extends, or resets those deadlines.

The data parser now requires the original request bytes. Native capture reads
`request.json` through the existing root-owned 0700 parent and retained no-follow
600/single-link file checks, then compares its `job_deadline_utc` with the retained
descriptor before outputs. UTC rendering differences compare as instants with
all seven fractional digits retained. A mismatch, missing/extra request field,
invalid calendar value, non-UTC value, or duplicate field rejects fixed data.
`original-request.json` is retained with the other original bytes. The receipt
reports `job_deadline_utc`, `observer_deadline_kind=fixture-observer-boottime`, and
`observer_end_boottime_ms` separately. No private C# monotonic expiry is inferred.

## Closed retained-record installation contract

The installed capture module exports exactly seven original records:
`root.stderr`, `original-failed-settlement.json`, `root-failure.json`,
`worker-control.json`, `worker.stdout`, `worker.stderr`, and
`original-request.json`. The root-owned `request.json` is read through the fixed
no-follow descriptor checks, compared against the original descriptor's UTC
deadline before output inspection, and retained byte-for-byte under the stable
name `original-request.json`. After the filesystem/NSS sample, exactly one
additional file, `filesystem-nss-observation.json`, completes the eight-name
retention set.

The module rejects an internal record-set mismatch before writing. The shell
runner independently checks the capture receipt's exact eight `record_files`
names, metadata shape, bounded sizes, and non-authority markers before it emits
the fixture observation. The runner's four module hashes and the ordered TSV
must agree byte-for-byte; the current changed `stall_capture.py` digest is
`d77dabf16e317e7790bfcc1d458f3d61342655465e4fa1c3e3a2e4ba35992035`.

The only N11 installation/harness consumers located in the bounded prior N11
packet inventory were the pinned parser bundle, `checkpoint-n11.sh`, and its
capture helper. No separate N11 workflow file or launcher supplying
`--reviewed-script-sha256` was present in those packets. The external installer
must therefore update that caller's reviewed-script pin to the final digest in
`source-ready.json` before any future native attempt. This packet does not change
review flags or authorize a run.

These are preparation/data controls, with no root execution or native acceptance.
Fixture/module integration, source review, pin closure, and the real N11 run
remain pending. All promotion flags stay false.

## Parser bundle handoff

The fixture accepts exactly four rows in `parser-pins.tsv`, each encoded as
`module-name<TAB>sha256` in the order `root_stall_frame.py`,
`check_stall_record.py`, `stall_capture.py`, `capture_stall.py`.
Both literal arrays in `checkpoint-n11.sh` must match these rows and the actual
module bytes. A hash-first table or an older capture-module digest rejects before
parser dispatch. Root ownership, mode 0444, single-link checks, bounded reads,
and retained source/copied-file checks remain required by the fixture.

This preparation corrects the table order and capture-module pin. Bash syntax and
the complete four-module pin closure were checked locally; the seven review gates
are still pending, and no root parser dispatch or native N11 result is claimed.

## N11 evidence checker contract corrections

The N11 negative-run archive uses its own preserved-quarantine schema. It cannot
use the cancellation checker, whose N08/N09 result requires removed accounts and
completed root custody. The runner compares the exact archived capture digest to
the fixture result, recomputes the structured consistency object from the original
frame/descriptor/streams/request, and checks the captured generated NSS and empty
cgroup observations. These are private diagnostic data checks. Actual native
qualification additionally requires the separately observed original process
terminal, compiled image, complete source/runtime bindings and root archive custody.

The runner and Ubuntu prerequisite loader execute exactly the retained bytes that
passed their hash/identity checks. They do not reopen mutable helper paths for
Python imports. The fixed helper dependency order is unchanged, and loading is
bounded by the original deadline. Neither Python parsing nor a metadata test grants
C# admission, producer leases, native acceptance or Trusted eligibility.

This correction has portable checker/loader regressions; native N11 execution is
still pending. Its Python evidence trust gate remains closed until independent
review of the corrected contract. Earlier failed native and review records remain
immutable history.

## Required N11 failed-settlement outcome

The detached parser reports the original holder's terminal facts without converting
them into success. The N11 runner additionally requires `physically_settled=false`
and a failed or cancelled terminal monitor (`Faulted` or `Cancelled`). A `Completed`
monitor or a physically settled lifetime cannot pass this stalled-worker control,
even when all archived record digests and recomputed consistency fields agree.
The original nonzero root result and preserved quarantine remain mandatory. These
checks classify private evidence; they issue no admission or native authority.

## Current source-review release

The corrected N11 Python evidence contract received independent source review,
including original-frame failed-settlement regressions and execution from retained
verified helper bytes. Its dedicated fixture gate and source-review field are now
released for native case execution. The final installation pin graph is reviewed
separately. No Linux execution or N11 acceptance is established by this release;
the earlier blocked native attempt and failed checker assertions remain history.
