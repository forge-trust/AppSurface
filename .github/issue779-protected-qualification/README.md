# Protected qualification prerequisite

This [manual workflow](../workflows/evidencehost-protected-qualification.yml) runs the existing
[full runtime-proof workflow](../workflows/evidencehost-runtime-proof.yml) as a reusable child after
the protected-main context guard succeeds. Both workflow files and their fixed dependencies must be
reviewed and merged to protected `main` before dispatch. It accepts no dispatch inputs. A candidate-
branch execution, unit-test fixture, matching digest, or successful receipt write cannot authenticate
protected execution or establish support.

## Execution and fixed inputs

The workflow checks out its own immutable workflow commit. Before native dispatch,
[`entrypoint.context`](entrypoint.py) requires the canonical repository, protected `main`,
`workflow_dispatch`, the exact workflow path/ref, matching checkout/workflow SHA, and positive
canonical repository/run/attempt identifiers from the provider context. Outside this protected
workflow those environment strings are only comparison data.

The wrapper calls `evidencehost-runtime-proof.yml` with no inputs. That reusable workflow runs the
existing fourteen C1 selectors covering sixteen controls and their aggregate, packaged C2 producer, C3 application consumers, account
creation and rollback, all current measured selectors, and the unchanged solution/patch gates through
its existing `needs` graph. The call edge preserves every runtime job definition, threshold, budget, source base and gate command. The wrapper's collector depends on successful completion of the reusable
workflow job; the C1 aggregate remains upstream inside that graph and does not depend on the collector,
so the graph has no aggregate/collector cycle.

The C1 job API prefix is fixed to the nested reusable-workflow names:
`Full current runtime proof and unchanged gates / All sixteen current CSharp worker controls / `.
The collector requires 36 exact job rows: the protected-context job and all 35 runtime leaf jobs
(parent controls, fourteen selectors and aggregate, producer,
application, both account jobs, fourteen measured selectors, and coverage gate). Every required job
must be unique, completed and successful for the exact head and attempt before the collector downloads
the C1 aggregate. The workflow also passes the reusable job's `needs` result as a fixed environment value;
the data-only collector rejects before API access unless it is `success`. A protected run must confirm
the expected nested job-name prefix through the provider API before any future schema change is accepted.

The final job uses read-only repository/Actions permission. `collect` queries fixed GitHub API
endpoints through the existing [bounded parent transport](../issue779-protected-consumer/github-parent-README.md).
It verifies the repository ID/default branch, current run/attempt/head/event/path, immutable workflow
bytes, all thirty-six exact successful job names, and one exact current aggregate artifact. Its digest
is compared with the downloaded original bytes before ZIP parsing. The selected archive is at most
64 KiB with one `aggregate.json` member at most 16 KiB; the transport retains its existing independent
32 MiB response ceiling and 120-second total deadline. The ZIP central-directory count and extent
are checked before constructing archive entries. The original aggregate must report exactly the
fixed sixteen cases, fourteen selectors, zero skips/failure exit and the same source capture.
The collector never accepts an arbitrary URL, record, selector, expected result, executable or policy.

## Receipt and deliberately unfinished obligations

`partial-qualification.json` uses `issue779-protected-full-runtime-partial-v1`.
It binds the protected wrapper's repository/workflow/current-run identity, the called runtime workflow
path and successful reusable-job result, actual source capture and tree, successful C1 job IDs, API
artifact ID/name/bytes/digest, raw ZIP digest and exact aggregate-body digest.
`source.json`, `aggregate.zip` and `aggregate.json` retain the original bytes separately; they are
created exclusively at private `0600` leaves in the fresh `0700` runner evidence directory.

The receipt truthfully observes its own run **in progress**. A job cannot certify the successful
completion of the workflow that still contains it. Independent final verification after the outer
run completes must re-resolve its final conclusion, the complete reusable child job graph, all current
selector and consumer/account artifacts, exact source and original native callbacks, and the unchanged
solution/patch gate. The successful reusable-job dependency and C1 aggregate alone do not replace that
work. The receipt fixes all of these claims to false:

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

The reviewed collector passed twelve detached boundary controls in the isolated validation checkout.
These use synthetic provider metadata and local archive bytes; no live API or native workflow ran.
The workflow has not run on protected `main`. These tests do not establish qualification, acceptance,
coverage gain or a Trusted result.
