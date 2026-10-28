# Protected completed-run preparation and closeout

[`completed_controller.py`](completed_controller.py) joins the
[source preflight](completed-source-README.md), [provider resolver](completed-run-README.md),
[archive inventory](completed-validation-README.md), and
[fixed replay observations](completed-orchestration-README.md) through two separate comparison APIs.
Neither API executes completed-source callbacks, restores packages, builds a project, launches
`dotnet`, or invokes the replay executor. Tokens are used only by the fixed provider transport.

## Preparation

`prepare_completed_run(event_raw, environment, validator_capture, source_capture, source_root,
native_source_root, stage_directory, token, *, deadline)` first verifies the protected validator
and both exact completed-source checkouts. It then reads the two fixed workflow bodies from the
verified completed checkout, resolves the original successful protected run and all 37 archives,
and inspects their complete ZIP projections. Returned `CompletedPreparation` contains the typed
stage, immutable source binding, parsed inventory, three caller-supplied roots and the recorded
identities of the validator, archive, completed-source and native-source directories.

The source step has at most 120 seconds and archive transport at most 1,200 seconds, both within
the caller's absolute monotonic deadline of at most 1,320 seconds from entry. It starts no replay.
The stage directory must initially be empty. All four roots must be canonical, caller-owned and
disjoint; the stage and native checkout must have mode `0700`. The original completed-source
mount and fixed future workflow context come from the source preflight contract. No alternate
workflow path, API URL, validator, callback or coverage option is accepted.

Preparation pins the roots while working and records their identities for closeout. These
records are ordinary local comparison data, not a portable provider receipt or authority token.
A reconstructed object cannot authenticate an execution context. The caller must keep the
original directories in its independently contained workspace and preserve their identity until
closeout. On failure, only the resolver's own download cleanup applies; later parsing failures
leave staged data for caller review and disposal.

## Replay boundary

Between preparation and closeout, a separately managed executor may run the existing
`replay_completed_run` API without a provider token or privileged secrets. Its workspace/process
containment and settlement remain the caller's responsibility. In particular, Python function
return or removal of a token from an argument list does not establish process isolation. These
APIs do not implement a Docker containment runtime or a new native ownership guarantee.

The future protected workflow remains absent and disabled. No command-line bridge, workflow
trigger, support registry entry or admission path is added by this module. A reviewed caller must
establish the credential separation and execution boundary before using these APIs for a real
protected completed-run validation.

## Closeout

`close_completed_run(preparation, replay, validator_capture, source_capture, token, *, deadline)`
accepts the in-process `CompletedPreparation` and a typed `CompletedRunReplay`. It rechecks stage
and provider-run facts before any API use, requires every fixed phase's canonical JSON/hash,
exact field schema, source/run attribution and false authority claims, and rechecks the existing
phase result contracts. The measured report's byte projection binds its recorded size and digest;
closeout does not reconstruct the discarded raw report bytes from that digest.

The native and gate output roots must share the original private parent and use their fixed
names and returned child paths. Their parent must contain only `native` and `gate` and be disjoint
from the validator, archives and source checkouts. The original four preparation roots must still
identify the recorded nodes. All roots and 37 archive descriptors remain pinned during closeout,
with no-follow opens, hashes and identity checks before and after provider reads. A foreign or
replaced node is rejected and preserved.

Closeout verifies all three complete sources, re-inspects the original archive inventory, invokes
the six-query [provider revalidation](completed-run-README.md), compares every returned job,
workflow and archive fact, and rechecks sources and original archives again. Its one absolute
deadline has at most 180 seconds remaining. Each source verification has at most 120 seconds and
the provider query phase at most 60 seconds, always capped by the same outer deadline. No expired
interval is renewed. Token-bearing code never invokes the replay executor.

`CompletedValidationObservation` records repository/run/head and validator identities, capture
digests, the canonical provider/replay/inventory digests and each phase digest. It has fixed false
`cleanup_complete`, `admission_created`, `native_acceptance`, `qualification_complete`,
`gate_eligible`, `original_gate_success_proven` and `test_inventory_success_proven` fields, with no
constructor overrides. The report means the compared metadata and local projections matched.
It does not independently prove execution, original success, settled containment, support
registration, runtime Trusted admission or downstream acceptance.

## Verification

[`test_completed_controller.py`](test_completed_controller.py) exercises ordinary private files,
the real 37-archive inventory and typed replay observations, with provider and source preflight
seams mocked. It covers ordering, invalid deadlines, source/stage mismatch, projection tampering,
foreign paths, archive drift and provider changes. These fixtures are mechanism verification;
they are not protected workflow evidence. The source preflight has separate real-Git tests in
[`test_completed_source.py`](test_completed_source.py).

```sh
python3 -B -m unittest discover -s .github/issue779-protected-qualification -p 'test_completed_controller.py'
```
