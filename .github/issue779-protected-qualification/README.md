# Protected qualification prerequisite

This [manual workflow](../workflows/evidencehost-protected-qualification.yml) is an initial,
partial qualification step. It must be reviewed and merged onto protected `main` together
with its [current worker workflow](../workflows/issue779-checkpoint1-worker.yml) and fixed
dependencies before it can run. It accepts no dispatch inputs. A candidate-branch execution,
unit-test fixture, matching digest, or successful receipt write cannot authenticate protected
execution or establish support.

## Execution and fixed inputs

The workflow checks out its own immutable workflow commit. Before native dispatch,
[`entrypoint.context`](entrypoint.py) requires the canonical repository, protected `main`,
`workflow_dispatch`, the exact workflow path/ref, matching checkout/workflow SHA, and positive
canonical repository/run/attempt identifiers from the provider context. Outside this protected
workflow those environment strings are only comparison data.

The existing reusable C1 workflow then executes its original fourteen selectors and reconciliation
with its original process policy, budgets and cleanup requirements. This wrapper changes none of
those controls. The worker workflow must itself be reviewed on protected `main`; copying only
this wrapper to `main` leaves an unresolved reusable-workflow dependency.

The final job uses read-only repository/Actions permission. `collect` queries fixed GitHub API
endpoints through the existing [bounded parent transport](../issue779-protected-consumer/github-parent-README.md).
It verifies the repository ID/default branch, current run/attempt/head/event/path, immutable workflow
bytes, all fifteen exact successful job names, and one exact current aggregate artifact. Its digest
is compared with the downloaded original bytes before ZIP parsing. The selected archive is at most
64 KiB with one `aggregate.json` member at most 16 KiB; the transport retains its existing independent
32 MiB response ceiling and 120-second total deadline. The ZIP central-directory count and extent
are checked before constructing archive entries. The original aggregate must report exactly the
fixed sixteen cases, fourteen selectors, zero skips/failure exit and the same source capture.
The collector never accepts an arbitrary URL, record, selector, expected result, executable or policy.

## Receipt and deliberately unfinished obligations

`partial-qualification.json` uses `issue779-protected-worker-qualification-partial-v1`.
It binds repository/workflow/current-run identity, the actual source capture and tree, successful
job IDs, API artifact ID/name/bytes/digest, raw ZIP digest and exact aggregate-body digest.
`source.json`, `aggregate.zip` and `aggregate.json` retain the original bytes separately; they are
created exclusively at private `0600` leaves in the fresh `0700` runner evidence directory.

The receipt truthfully observes its own run **in progress**. A job cannot certify the successful
completion of the workflow that still contains it. Independent verification after run completion
must re-resolve the final run, all current selector artifacts, exact source and original native
callbacks, every required consumer/account/lifecycle control and the unchanged solution/patch gate.
The aggregate alone does not replace that work. The receipt fixes all of these claims to false:

- `selector_callbacks_independently_verified`
- `qualification_complete`
- `admission_created`
- `trusted_manifest_created`
- `gate_claimed`
- `native_acceptance_claimed`

The [ordinary protected parent](../issue779-protected-consumer/entrypoint-README.md) remains a separate
final consumer of an independently reviewed completed producer record. This prerequisite never calls
that semantic child, modifies its empty record table, registers a support record or bypasses
[Planner's accepted-proof decision](../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceProtectedWorkerInputs.cs).
After full protected qualification and independent review, a separately reviewed support registration
must precede the first ordinary Trusted production run; requiring that run before registering its
support would be circular. The current ordinary root path still accepts Observation only. Runtime
Trusted wiring, full support qualification, protected-main review/merge and an actual downstream
accepted record remain required. No local or candidate run closes those obligations.

## Tests and failure behavior

[`test_entrypoint.py`](test_entrypoint.py) exercises detached JSON/provider metadata and ordinary local
files only. It uses mocked API transport, never executes a native owner or grants authority. Invalid
context rejects before API access; wrong job/run/artifact bindings and malformed archives reject before
writing a receipt. The executable returns only a fixed rejection marker on failure and no token,
exception, private output or path. An expired deadline fails the command; it never renews a native clock.

The proposed workflow has not run on protected `main`. Source preparation and these tests are not a
qualification, acceptance, coverage gain or Trusted result.
