# Private N10 pending-start cancellation fixture

This fixed Linux-x64 fixture observes the private build-owned N10 checkpoint only. The checkpoint emits its first frame after the original validated StartTransientUnit reply, while the original pending-start task remains blocked on the original startup token. The root fixture validates that exact worker unit, the live worker PID, UID, GID, and cgroup, then sends SIGINT to the original root owner within the original 30-second startup window.

The original pending-start coordinator remains responsible for its two stops and awaiting the original start task. The worker finalizer retains the existing selected-unit and post-pump cgroup samples. The joined frame is accepted only when it preserves sticky StartCancelled, two original stop calls, actual original start/stop joins, stopped-unit evidence, empty post-pump cgroup evidence, and error-free joined stdout/stderr EOF. physically_settled must remain the actual false projection for this cancelled run. A retained workspace/account path is reported as retained; the fixture never force-closes it or upgrades that state to custody.

The fixed parser in [check_n10_pending_start.py](check_n10_pending_start.py) consumes bounded records only. [test_n10_pending_start.py](test_n10_pending_start.py) exercises only parser data and makes no native, lease, admission, or ownership claim. [checkpoint-n10-pending-start.sh](checkpoint-n10-pending-start.sh) stages source/tool/runtime inputs and performs the existing independent OS audit before the fixed owner launch. build-fdd.sh compiles with -p:EvidencePrivateQualification=N10 for both restore and publish; ordinary builds remain None.

The source binding preserves the raw capture at source-capture.json by absolute reference and pins its SHA-256. Its explicit in-memory projection maps only head_commit to the build receipt's validation_commit, parent_commit to previous_validation_commit, and copies source_count, tree, and per-path SHA/mode rows from source_sha256_modes. It does not synthesize source counts, test counts, or validation results.

## Scope and release state

The source capture is bound to 93180b42bb48e1d4170c049468e8b0bb036a68d0, direct parent dd941d06ec1a038fd4d82228005f14fd3c6440a0, 2,851 source entries, and raw capture SHA d130827f7afd25e53cb9dc7a1d5345cde22cec64b4600f0a3258c3a6fcc17bbc. This folder is source preparation only. Parser/fixture/transport/source review gates are false, native execution and native credit are false, and the final workflow pin closure is pending until the executors are frozen and independently reviewed.

No source build, root action, native case, CI action, or publication was performed by this preparation.

## Final teardown sample validation

The N10 parser validates the actual recorded terminal state before accepting its stopped and empty-group projections. Stopped requires inactive/dead or failed/failed with MainPID zero and a positive retained ExecMainPID; integer code and status fields reject booleans and invalid ranges. A pruned cgroup requires every subordinate field to be null. A present empty cgroup requires a complete false-population, inode-bound record. These are data checks; they grant no admission or native proof.

Pending-start flags require actual JSON boolean values; integers zero and one are rejected before a record is accepted. The native test image keeps the original ownership, deadline, join and custody predicates. Review gates permit only one experimental native control, and a green workflow still requires source-bound real records before any native credit.


Review status for this v11 preparation: the three read-only review guards are promoted on the basis of the accepted v10 independent final input review (SHA-256 `d803ac53a816e2c4aee7087b4d4947b03f208d6ed6ecd04575cbb951d3b64482`). A fresh independent review of the complete v11 installation is pending. All eight outer review gates remain false; execution is unauthorized and no native execution or native credit is claimed.


### N10 v1 observed pre-dispatch failure

Run `38093261308` at head `78b4fba224e0d14f4ffcb59b992e7747cca3e7ba` failed the build-source receipt predicate before native dispatch; it provides no N10 C# or native-behavior result. The four retained build phases report product-map digest `ef2e460e6565671f5aee86566f723a249518724eecea825bff229ea6a4f03496`; the existing `SOURCE_MAP` value `8db3377d9e25b1127f90cf82fb797b51ce8d15db026ea0f628c739594175b85c` identifies the distinct canonical capture-map digest. The builder hashes the compact `{sha256, modes}` projection without a trailing newline, while the canonical capture digest hashes the full `source_sha256_modes` record with a trailing newline. This candidate keeps those values separate and uses `BUILD_SOURCE_MAP` only for build-phase facts. Independent review and a future native run remain pending; no native credit is asserted.
