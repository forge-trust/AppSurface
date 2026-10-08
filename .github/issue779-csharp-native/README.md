# C# Linux native N01/N02 checkpoint

This private workflow builds the ordinary CLI and executes the first two real controls from the [C# supervision migration plan](../../docs/plans/issue-779-csharp-supervision-migration.md). The [C# core](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md) uses the same CLI executable for root supervision and the isolated worker. Python here prepares validation inputs and collects private receipts; it provides no production supervision or admission authority.

## Source and execution binding

Product source remains `5d1036b7035a7d9e980cdab5afc88879fca5048c`, direct parent `2993dcfaac1b9b6dfb8adf057191f837876f01fe`. The harness is a direct child of `1e3c586ef2a3d196f7abcb2ba7aaa6956c1fe21d`. All 2,819 source hashes/modes remain bound by [source-review.json](source-review.json), the complete build receipt and OS audit. Historical local 997 tests and separate 28 STOP/WAIT tests are source-bound local verification, not native acceptance. SDK10.0.401 builds the CLI; signed Ubuntu10.0.12 execution packages undergo the full [OS audit](prepare-os-audit-v2.py).

## Measured startup limit correction

Run37708079779 compared the same fixed unprivileged command using 2MiB and 256MiB file limits. The lower case exited134 with runtime out-of-memory; the higher case exited1 with ASEVD402 and no stdout. Both sampled actual kernel UID/GID1001 and uniformly disabled core dumps. The precise failed allocation was not measured. Actual strace argv is ellipsized; identical complete invocation is established by the source-owned single function and image/request hashes. The diagnostic intentionally exited1 and attempted no N01.

[The normal fixture](checkpoint-n01-n02-v5.sh) restores the original root-rejection assertions at a 262144KiB startup file limit with core0, equal to the existing256MiB deployment-file envelope. All functional argv/environment, N01 assertions, original600s interval/cleanup30, UID/cgroup/unit guards and custody checks remain. No C# worker policy, memory/task cap, security flag, timer, production catalogue or acceptance registry changes. N01 and N02 are pending until actual positive/negative receipts satisfy the unchanged normal validator. All other fourteen controls remain pending.

## Audit, settlement and retention

The reviewed batched audit verifies complete membership, file/node/byte/depth envelopes and pre/post identities and content hashes without a cache or deadline extension. Its fourteen actual Bash parser controls passed in the prior Linux run; they run again before this dispatch. [The root transport](prepare-root-inputs-v2.sh) pins both the fixture and OS auditor before root execution. Every dependent acquisition/runner/runtime/builder/workflow pin is checked together.

[The dispatcher](run-native.py) preserves bounded FIFO writers, exclusive private files, process-group joins, root tool cgroup guards and the original deadline. [The retainer](retain-native.sh) accepts only fixed names, root0600/single-link/no-follow, canonical USTAR and32MiB total bounds. The previous diagnostic names remain allowable but absent in this normal fixture. Trace and stdio bounds remain post-join checks, with no claim of an independent live trace quota. Retention cannot turn a failure into success.

## Result and sharp edges

Normal success requires actual N01 exit0, N02 exit1 with root rejection before protected I/O, real supervisor/worker settlement and strict account/workspace/FD cleanup. The unchanged closed native validator requires those facts; root validation-tool settlement alone is insufficient. No native pass or Trusted enablement is claimed by this preparation. Earlier failures and the startup comparison remain immutable historical evidence.
