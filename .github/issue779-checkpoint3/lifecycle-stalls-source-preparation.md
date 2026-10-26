# Compile-owned C3Sdk lifecycle stall preparation

This source preparation adds three fixed failure-only controls to the existing authenticated C3Sdk worker path:

- `ConfigureSync`: synchronously blocks inside the actual bounded `EvidenceHostBootstrap` registration callback.
- `RegisteredDisposer`: returns a never-completing `ValueTask` from the actual retained `EvidenceRestrictedAspireApplication.DisposeAsync`, reached through the existing `EvidenceWorkerExecution` registered cleanup callback. Returning the pending task makes an omitted cleanup await observable; a synchronous block would hide that regression.

For the two stalls, the selector is the private MSBuild property `EvidencePrivateC3SdkLifecycleStall`. It accepts exactly `ConfigureSync` or `RegisteredDisposer`, and only when `EvidencePrivateQualification=C3Sdk`. The corresponding compile symbol is absent when the property is unset, and invalid property combinations fail the build. There is no runtime option or transport, owner, admission, clock, or timeout seam. The pre-existing C3 case table remains seven entries.

Each fixed branch writes one `evidence-worker-lifecycle-stall-v1` event with `phase=entered` and `native_acceptance=false`. Configuration blocks synchronously; disposal returns its pending task after the existing cleanup state has required `EvidenceWorkerExecution.RequireJoinedCleanupPhase`. The native runner requires the original authenticated root-owned supervisor recipe, exact marker, nonzero root exit, empty owned systemd groups, actual NSS identity absence, and no final manifest. The configure control additionally requires that the application never started. The registered-disposer control requires the actual application to have reached its settled observation before the registered disposer marker. These controls do not claim verifier-stall, physical output-write, pump-stall, or successful acceptance coverage.

Worker stderr is collected by the original root output pumps and is **not** echoed to `owner.stderr`. [`lifecycle_stalls.read_worker_marker`](lifecycle_stalls.py) reads only the existing `/run/appsurface-evidence-worker-diagnostics-<generation>` directory after the owner is terminal. It requires root `0700`, the exact three root `0600` single-link regular leaves, retained descriptor/name identity and stable bytes. Both prefix limits remain 1 MiB. It validates the existing worker-failure state and the fixed marker without echoing private bytes. The existing shared output receipt must show all original pairs joined to EOF, no quota or read failure, and reconciled received bytes. The first registered worker pair must account for the retained prefixes. Missing retention fails this control; there is no journal, owner-log, or new-reader fallback. The ordinary-file helper's UID/GID arguments exist for detached metadata tests and supply no protected path or ownership authority.

The original worker prefix must also contain its specific production `EvidenceWorkerExecution` fatal reason: callback work outliving its stopping grace, or a registered disposer outliving cleanup. An unrelated crash or outer watchdog kill after the entry marker is insufficient. Only the closed reason label and byte digests enter the case JSON; private stack and prefix bytes stay in the separately bounded artifact. The fixed-byte parser does not independently prove process identity or fatal termination.

## Workflow integration awaiting fresh Linux execution

The [C3 workflow](../workflows/issue779-checkpoint3-application.yml) builds three additional CLI images after the original measurements, using the same source capture and generated registration table. It passes `-p:EvidencePrivateQualification=C3Sdk` and respectively `-p:EvidencePrivateC3SdkLifecycleStall=ConfigureSync` and `=RegisteredDisposer`, then publishes each full rebuilt graph to a distinct runner-temp directory. All three outputs stay separate from the ordinary C3Sdk image and the seven-case receipt. Existing locked restore, warning-as-error, generated-source and private-package guards remain in force.

Invoke the dedicated partition with the existing fixed runtime, prepared application metadata, registration digest, subject, source and base:

```text
python3 -B .github/issue779-checkpoint3/run-native.py \
  --tool <ordinary-c3-tool> --sdk-tool <ordinary-c3sdk-tool> \
  --configure-stall-tool <configure-stall-tool> \
  --disposer-stall-tool <registered-disposer-stall-tool> \
  --post-stop-guards-tool <post-stop-guards-tool> \
  --runtime <fixed-runtime> --subject <fixed-subject> \
  --prepared <prepared-json> --digests <registration-digests-json> \
  --source <captured-revision> --base <captured-base> \
  --output <new-private-lifecycle-output> --lifecycle-stalls
```

The dedicated three-case partition has one 1,200-second outer orchestration clock and a 480-second observation bound per case, capped by its original remainder. It uses the existing request builder and owner recipe unchanged: request job 1,200 seconds, admission 30, start 120, collection 60, cleanup 120, stopping 10; owner RuntimeMaxSec 1,100. These clocks and the existing 65-minute workflow limit are not extended. An expired original clock still rejects.

After each original owner is terminal, its groups are empty, its exact accounts are absent, and diagnostics are retained, the existing descriptor-bound fixture remover reclaims the generated case directory and consumed activation-guard directory. After all three cases pass, it also reclaims the fresh copied tool/runtime parent. Removal checks root ownership, single-link regular leaves, retained names, the original mount identity, node count and the original case/partition deadline. Successful receipts require those exact fixture paths absent; a failed final group recheck or reclamation keeps the case or partition failed. Failed controls preserve their remaining fixture trees for diagnosis and are never credited as cleanup successes. Product-owned output and diagnostic retention remain separate.

The separate `issue779-c3sdk-lifecycle-<run>-<attempt>` artifact contains at most 26 fixed regular files, each at most 8 MiB and at most 32 MiB in total. Files are private `0600` beneath `0700`. This does not expand or replace the original C3 artifact, original 41-case root receipt, supplementary root receipt, account lineage, coverage report, or seven-case acceptance table. This partition is uninstrumented and contributes no invented coverage counters. Source preparation, local compilation and ordinary-file controls cannot establish that either native stall control passed; fresh source-bound Linux execution and artifact verification remain required.

## Post-stop rejection control

The third image uses the separate, mutually exclusive `EvidencePrivateC3SdkPostStopGuards=true` build property. Its [fixed SDK worker control](post-stop-guards-source-preparation.md) runs only after genuine successful stop/join/disposal, while the admission and original protected writer remain active and retained. A valid new owned-work callback must not dispatch, and a fresh zero-byte write to the optional `post-stop-probe` slot must reject with the specific closed-write diagnostic. The original artifact list must remain unchanged. The private image then deliberately returns an unsuccessful result, preserving its closed marker through the existing root-retained worker prefix channel. This is a recoverable negative result, not a fatal-path control.

The native runner requires the exact six true marker facts, that intentional failure, empty root stdout, no final manifest or probe artifact, actual application settlement, all three original output pairs reconciled through EOF, empty groups and absent original accounts. It never credits a stale worker, already-failed admission, duplicate slot, invalid path, or generic failure as the write-admission rejection. The optional fixture slot permits at most one byte and is not used by the ordinary coverage adapter. The probe attempts an empty write; its positive metadata bound prevents registration rejection from masking the intended closed-admission guard. No new timer, runtime selector, transport, public capability or production registration is added. The three cases share the original 1,200-second partition deadline.
