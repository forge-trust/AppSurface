# Private N10 pending-start cancellation fixture

This fixed Linux-x64 fixture observes the private build-owned N10 checkpoint only. The checkpoint emits its first frame after the original validated StartTransientUnit reply, while the original pending-start task remains blocked on the original startup token. The root fixture validates that exact worker unit, the live worker PID, UID, GID, and cgroup, then sends SIGINT to the original root owner within the original 30-second startup window.

The original pending-start coordinator remains responsible for its two stops and awaiting the original start task. The worker finalizer retains the existing selected-unit and post-pump cgroup samples. The joined frame is accepted only when it preserves sticky StartCancelled, two original stop calls, actual original start/stop joins, stopped-unit evidence, empty post-pump cgroup evidence, and error-free joined stdout/stderr EOF. physically_settled must remain the actual false projection for this cancelled run. A retained workspace/account path is reported as retained; the fixture never force-closes it or upgrades that state to custody.

The fixed parser in [check_n10_pending_start.py](check_n10_pending_start.py) consumes bounded records only. [test_n10_pending_start.py](test_n10_pending_start.py) exercises only parser data and makes no native, lease, admission, or ownership claim. [checkpoint-n10-pending-start.sh](checkpoint-n10-pending-start.sh) stages source/tool/runtime inputs and performs the existing independent OS audit before the fixed owner launch. build-fdd.sh compiles with -p:EvidencePrivateQualification=N10 for both restore and publish; ordinary builds remain None.

The source binding preserves the raw capture at source-capture.json by absolute reference and pins its SHA-256. Its explicit in-memory projection maps only validation_commit and previous_validation_commit directly, maps source_tree to the build receipt tree, and copies source_count plus the nested source.sha256/source.modes maps. It does not synthesize source counts, test counts, or validation results.

## Scope and release state

This v12 packet corrects the workflow's direct-push review bypass. Before any
build, runtime preparation or native invocation, the checked-out workflow verifies
[execution-review.json](execution-review.json), its pinned digest, completed
independent review flags, exact source/capture identity and all installed input
bytes and modes. The workflow is included: only its single digest of that review
is normalized to zeroes to avoid a circular digest. Every other workflow byte is
reviewed. A pending review, stale file, changed mode or conflicting identity stops
execution. Changing the eight environment flags alone cannot open this gate.

The pending record is deliberately clear=false and released=false. Independent
review must populate the exact completed record; parent pin refresh and publisher
peer review follow before publication. The historical v9 bypass finding and v11
incorrect promotion remain preserved. Source commit 0c100a94d93f82fd633e5674009a41659940d010,
direct parent 03ff51b361d686e6e2590a27e9a4f43529cdbaf2 and the original 2,865-entry
capture SHA256 653d30169f6fc3aed53274349304dfebc3aff2b529e42bf3a4524a37fa546fd2
remains unchanged. This is fixture validation, not production admission.
No native execution, native case credit or acceptance is inferred from review.

## Final teardown sample validation

The N10 parser validates the actual recorded terminal state before accepting its stopped and empty-group projections. Stopped requires inactive/dead or failed/failed with MainPID zero and a positive retained ExecMainPID; integer code and status fields reject booleans and invalid ranges. A pruned cgroup requires every subordinate field to be null. A present empty cgroup requires a complete false-population, inode-bound record. These are data checks; they grant no admission or native proof.

Pending-start flags require actual JSON boolean values; integers zero and one are rejected before a record is accepted. The native test image keeps the original ownership, deadline, join and custody predicates. Review gates permit only one experimental native control, and a green workflow still requires source-bound real records before any native credit.


### N10 v2 observed acquisition pre-dispatch failure

Run `38096489551` at head `5c395700d8e27f08217e444a5b83e309a2d6442b` completed with a failure in step 12, before native execution. Its prerequisite build recorded 43 successful commands; the fixed acquisition path then rejected the build receipt at command ordinal 1 with `INBOUND_REJECTED:bounded-operation`. A bounded replay of the retained authentic receipt showed that its product-map digest `ef2e460e6565671f5aee86566f723a249518724eecea825bff229ea6a4f03496` passes the build-map predicate, while the distinct canonical capture-map digest `8db3377d9e25b1127f90cf82fb797b51ce8d15db026ea0f628c739594175b85c` does not. The acquisition and root-transport guards then kept those map identities separate. These historical v14 results predate the current common-source-v4 binding and do not establish native execution or acceptance.

### N10 v1 observed pre-dispatch failure

Run `38093261308` at head `78b4fba224e0d14f4ffcb59b992e7747cca3e7ba` failed the build-source receipt predicate before native dispatch; it provides no N10 C# or native-behavior result. Its four retained build phases reported product-map digest `ef2e460e6565671f5aee86566f723a249518724eecea825bff229ea6a4f03496`; the v14 `SOURCE_MAP` was `8db3377d9e25b1127f90cf82fb797b51ce8d15db026ea0f628c739594175b85c`. In that historical schema, the builder product map hashed compact `{sha256, modes}` JSON without a trailing newline, while the canonical digest hashed the `source_sha256_modes` record with a trailing newline. These are retained historical facts, not the current v4 map identities.

### Current common-source-v4 map identities

For the captured common-source-v4 bytes, the canonical capture map hashes compact `{source_sha256, source_modes}` JSON to `a3dbb4a7be588b3e5234bf728e4eda4032f5ee8acab1d2ab7213cceef106586e`; the builder product map hashes the distinct compact `{sha256, modes}` source object to `07e4be7c1c59989a70d88c345ff360e68a0a394a6729877afa7981d6d913d238`. The build-receipt capture projection has its own digest, `0c5f9605840c134d9b21866f2eb813111884a431540f0da2eb516ff0146e0c8c`. All three roles remain separate. Review and runtime/native qualification are pending; no native credit is asserted.


### Selected capture operand correction

Run `38108900943` failed during root acquisition, before the native N10 control was dispatched. The installed `capture_map` command passed the literal filename `capture`; the prior validation had already pinned the absolute capture file. Replaying that literal operand returned jq exit 2. Passing the quoted `$capture` operand returned exit 0 and the exact expected canonical map digest. The acquisition correction changes only that operand. The [CaptureMapOperandControls](test_n10_pending_start.py) execute the installed statement with a selected path containing spaces, a conflicting working-directory decoy, a missing selected file, the full pinned capture, and a mode mutation. They perform no root staging or native execution. Fresh independent review and a source-bound Linux run remain required.


### N10 systemd identity properties

The N10 pending-start fixture reads the selected owner and worker unit properties as named `Property=Value` records. It rejects missing, duplicate, malformed, or unrequested properties and does not rely on the order returned by `systemctl show`. The existing active/running, positive live PID, root-owner UID/GID, nonroot-worker UID/GID, exact cgroup, `/proc` identity, startup deadline, signal, and join checks remain required. The fixed data controls are part of `test_n10_pending_start.py`; they do not execute systemd or claim native qualification. The retained prior failure reports only `n10-owner-identity`, not a specific bad property, so this parser correction does not claim that output ordering caused that historical failure.

### Preflight and post-run unit-collision diagnostic

The preflight and post-run absence checks keep their original generated-unit `systemctl show <generated-unit> -p LoadState --value` query and still reject unless its exit is zero and its value is exactly `not-found`. Before a preflight rejection, the fixture may write the fixed `n10-unit-collision.json` record from that same query; it does not issue a second query. The record contains only `schema`, the closed `owner` or `worker` role, a closed `load_state` value (`loaded`, `not-found`, `error`, `masked`, or `unknown`), and the numeric exit code. Malformed output is recorded as `unknown`; raw property output and generated unit names are never retained. The record is mode 0600, limited to 4 KiB, and a diagnostic write failure leaves the original rejection intact, including the existing `bounded-operation` rejection for a nonzero query exit.

Retention adds only that fixed filename to its existing file whitelist. The archive validator applies the same 4 KiB cap and rejects duplicate JSON keys, extra fields, invalid roles or states, and non-integer or out-of-range query exits. An absent record is expected when the original query positively reports `not-found`, diagnostic capture fails, or the fixture stops before a rejected unit query. These controls exercise extracted shell procedures with fixed mock replies; they do not execute systemd or establish the operating-system cause of a prior failure.
