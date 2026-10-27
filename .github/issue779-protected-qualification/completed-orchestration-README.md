# Fixed completed-run replay orchestration

[`completed_orchestration.replay_completed_run`](completed_orchestration.py) consumes an already
resolved [`CompletedRunStage`](completed-run-README.md), its private archive directory, the exact
source-capture bytes, two disposable source checkouts, and an empty private output parent. It
returns a frozen `CompletedRunReplay` containing eight ordered `PhaseObservation` records, the
run/head/tree/capture identity, retained output roots, and the original coverage job ID. Each
observation retains bounded canonical JSON and its SHA-256 digest. Byte arrays inside a projection
are represented by their length and SHA-256; original archives and controller outputs remain the
underlying evidence.

## Call contract and fixed sequence

Call `replay_completed_run(stage, stage_directory, source_capture, source_root,
native_source_root, private_parent, *, deadline)` after the protected completed-run resolver has
successfully staged all 37 original archives. `deadline` is one finite absolute monotonic deadline
with at most 1,200 seconds remaining; it is passed unchanged to each validator. Resolver transport
and replay have separate finite budgets. This API accepts no token, executable selector, callback,
caller-provided validator, alternate coverage base, or threshold override.

The fixed sequence is:

1. [C1 aggregate and retained-evidence callbacks](completed-c1-README.md).
2. [C2 producer receipts](completed-c2-README.md).
3. [C3 application and SDK lifecycle receipts](completed-c3-README.md).
4. [Account creation and rollback lineage](completed-accounts-README.md).
5. [All fourteen measured segments](completed-measurement-chain-README.md).
6. [Retained solution-test inventory](completed-test-inventory-README.md).
7. [The existing public native data test](completed-native-replay-README.md).
8. [The existing public coverage CLI](completed-gate-replay-README.md).

Before archive inspection, the orchestrator requires all fixed successful provider job facts,
including name-to-ID attribution for the coverage job. It then checks the full archive inventory,
verifies both checkouts against the complete source capture, and rejects existing untracked or
ignored outputs. Every phase receives the same typed inventory, stage, run identity and deadline.
Results must have the expected exact type, source/run attribution, fixed case inventory and false
authority claims. Source content is checked again between phases; original archive hashes and
the inventory are checked again before return.

## Workspace and custody

This is a Linux API. The original `source_root` must be
`/home/runner/work/AppSurface/AppSurface`, preserving the captured workflow's raw source and
Cobertura paths. `native_source_root` must be a separate fresh checkout at the same captured
commit, with mode `0700`. Both checkouts must be owned by the caller. `stage_directory` and
`private_parent` must be canonical absolute caller-owned mode-`0700` directories; the latter must
initially be empty. All four roots must be disjoint. Use disposable contained checkouts, not a
developer checkout with build caches.

The callback and measurement phases run first while both checkouts are fresh. Native replay uses
only its separate checkout. Gate replay runs last because it builds in the original workflow
mount. Native and gate controllers retain logs, reports, build/cache state and their existing
`cleanup_complete: false` observations. A caller-managed container or equivalent outer process
boundary must contain children that create new sessions; the controllers' process-group teardown
does not supply that outer boundary.

The orchestrator keeps no-follow close-on-exec descriptors open for all staged archives and the
root directories. It also pins the created `native` and `gate` output roots. Named paths must
still identify those nodes, and returned retained paths must equal their fixed children. It
rejects archive replacement, root substitution, unexpected output-parent siblings, or unknown
scratch contents. Only its four recorded callback scratch roots are removed, and only while
they still identify owned nodes. Foreign nodes are preserved. On failure, later validators do
not run; the original error remains primary and carries a retained-workspace note.

## Decision and limits

Use this API to exercise the fixed source-owned replay sequence over an eligible staged completed
run. Use the individual adapters for isolated mechanism diagnosis. Synthetic tests run real archive
inventory and custody checks while substituting the expensive validator entry points; those tests
establish wiring and rejection behavior, not a successful protected full-run replay.

`CompletedRunReplay` always has `cleanup_complete`, `qualification_complete`, `native_acceptance`,
`admission_created`, `gate_eligible`, `original_gate_success_proven`, and
`test_inventory_success_proven` set to literal `False` and unavailable as constructor options.
The job ID is provider comparison metadata, not a credential. A retained passing report and
matching replay do not independently establish original workflow success. This module adds no
protected finalizer, support registration, production catalogue entry, or runtime Trusted admission.
Those obligations remain in the [qualification boundary](README.md).

Run its portable wiring and custody tests with:

```sh
python3 -B -m unittest discover -s .github/issue779-protected-qualification -p 'test_completed_orchestration.py'
```
