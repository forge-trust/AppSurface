# C# N02 startup file-limit diagnostic

This private workflow builds the ordinary CLI and investigates a fixture limit before any native acceptance credit. The [C# supervision core](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md) and [migration plan](../../docs/plans/issue-779-csharp-supervision-migration.md) still require all sixteen native controls.

## Binding and baseline

Product source remains 5d1036b7035a7d9e980cdab5afc88879fca5048c, direct parent 2993dcfaac1b9b6dfb8adf057191f837876f01fe. This harness is a direct child of 18ab1ad31eec2a304c9121b33375d14a08e5deed. All 2,819 source hashes/modes, source tree and historical 997 local tests plus separate 28 focused STOP/WAIT tests remain bound by [source-review.json](source-review.json). The SDK remains 10.0.401; the execution runtime is the pinned Ubuntu 10.0.12 packages. Every ELF/dependency remains subject to [the OS audit](prepare-os-audit-v2.py).

## Diagnostic contract

[The fixture](checkpoint-n01-n02-v5.sh) runs the same unprivileged supervise command sequentially under 2048 KiB and 262144 KiB file limits. Both children uniformly disable core dumps. The second value is the existing 256 MiB deployment-file envelope. Image, request, selected UID/GID, cleared groups, environment and argv remain identical. The original monotonic 600-second fixture interval and cleanup reserve remain. Extended fixed strace records actual memory/limit/credential calls. Missing kernel facts remain unknown.

Attempt 9 measured runtime out-of-memory and SIGABRT; it did not measure the failed allocation. [Pinned .NET runtime source](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/minipal/Unix/doublemapping.cpp#L107-L122) clips executable-code backing storage by the file limit. This is the reason for the comparison, not a measured cause. Both results and private raw traces are preserved, including the lower failure. Trace caps of 1 MiB and stdio caps of 1024 bytes are verified after each synchronous case; there is no claim of an independent live trace writer cap. The root utility cgroup/deadline containment is unchanged.

The final result is the closed issue779-n02-startup-limit-diagnostic-v1 shape. It has empty native_controls_executed, false n01_attempted and false native_acceptance. Completion intentionally exits nonzero even if the larger limit reaches ASEVD402. The normal native result validator is unchanged and cannot accept this run. All N01/other controls remain pending.

## Private retention and ordering

[The dispatcher](run-native.py) retains the reviewed FIFO capture with separate 8 MiB writers, exclusive root-private files, both PID joins and the original deadline. [The retainer](retain-native.sh) adds only the fifteen fixed diagnostic names listed in the source review; no discovery, authority or original-result override is added. Every retained source remains root 0600/single-link/no-follow; fixed canonical USTAR and 32 MiB total bounds remain. Externally verified hashes precede interpreter use.

Nine pure jq/static controls passed; they establish no native result. No worker security policy, cap, timer, production catalogue or acceptance registry is changed. Root-owned validation-tool settlement cannot establish C# supervisor/worker settlement. At source preparation, the workflow has seven false promotion flags. Main may promote exactly those flags only after independent composition review; the diagnostic remains nonzero.

## Provenance regression

Attempt 10 compiled the unchanged CLI with 33 successful commands and zero compiler diagnostics, then rejected the handoff as ubuntu-runtime-prerequisite-source. Its builder still embedded obsolete prerequisite/runner hashes. The named startup diagnostic did not run. Historical receipts remain unchanged.

The builder now names both required hashes as compilation-bound constants and uses require_prerequisite_source after the existing result, package and schema checks and before runtime host copying. It checks exact equality and preserves the same fixed BuildFailure category. This helper validates metadata only; it cannot construct execution, admission or completion authority. All command settlement, selected runtime reads, ELFs, tree bounds, native checks and deadlines remain required. Five pure regression controls execute only the actual guard definitions and verify the current file-to-pin chain; no SDK/root/runtime is launched.
