# Protected completed-run command entrypoint

[`completed_entrypoint.py`](completed_entrypoint.py) exposes the existing
[preparation and closeout APIs](completed-controller-README.md) through two fixed commands.
Preparation reads provider metadata and stages original archives. Closeout compares the separate
[replay worker](completed-replay-worker-README.md) result with fresh source, provider and archive
checks. Neither command calls the replay executor, target callbacks, restores, builds or `dotnet`.
These commands prepare a future protected caller; the named finalizer workflow remains absent.

## Commands and API

The command shapes are fixed, with no optional arguments:

```text
completed_entrypoint.py prepare VALIDATOR_CAPTURE SOURCE_CAPTURE SOURCE_ROOT NATIVE_SOURCE_ROOT STAGE_DIRECTORY PREPARATION
completed_entrypoint.py close VALIDATOR_CAPTURE SOURCE_CAPTURE PREPARATION REPLAY REPORT
```

Use the reviewed validator's script with `/usr/bin/python3 -I -S -B`. Paths select bounded local
files and directories; they never select executable behavior, workflow paths, URLs, phase lists
or coverage options. The module explicitly adds its own reviewed directory to Python's import
path, so isolated Python mode does not depend on the current directory or inherited `PYTHONPATH`.
`GITHUB_EVENT_PATH` selects the original workflow-run event. `GH_TOKEN` is read only from the
credentialed process environment and is passed to the fixed provider transport through the
controller. Do not put credentials in command arguments or wire files.

The corresponding synchronous functions are:

```python
prepare_entrypoint(event_path, environment, validator_capture_path, source_capture_path,
                   source_root, native_source_root, stage_directory, preparation_path,
                   *, deadline) -> CompletedPreparation

close_entrypoint(event_path, environment, validator_capture_path, source_capture_path,
                 preparation_path, replay_path, report_path,
                 *, deadline) -> CompletedValidationObservation
```

Both accept one absolute monotonic deadline. Preparation allows at most 1,320 seconds from entry;
closeout allows at most 180 seconds. The CLI supplies those fixed bounds. No interval is renewed,
including during file readback and publication. Invalid, nonfinite, Boolean, expired or oversized
deadlines reject before opening inputs or outputs. The controller retains its existing inner
source/provider deadlines.

`guard_completed_entrypoint(event_raw, environment, *, deadline)` checks the canonical repository,
repository ID, protected `main` ref, workflow-run event, fixed future workflow reference and the
current validator commit against both workflow SHA environment values. It returns the event's
repository/run/attempt/head/workflow ID and current validator head for comparison only. Closeout
binds that context to the decoded preparation before invoking the controller. The independent
[source preflight](completed-source-README.md) still verifies all three complete source captures,
the validator's exact workflow file and completed-source ancestry. Environment strings alone do
not authenticate a protected execution context.

## File custody and publication

Every input path must be canonical and absolute. Event JSON allows mode `0600` or `0644` and the
existing event size limit. Source captures allow only mode `0600` and the existing 1 MiB limit.
Preparation and replay use the [strict wire codec](completed-wire-README.md), with mode `0600`
and a 16 MiB bound. All inputs must be regular single-link files owned by the current UID. The
entrypoint holds no-follow, close-on-exec descriptors, bounds reads before allocation, compares
opened and named identities, and rechecks hashes and identities after the credentialed controller
returns. Input readback finishes before any handoff or observation is published.

Outputs must not already exist, including dangling symlinks. Their canonical caller-owned parent
must have mode `0700` and be disjoint from the validator, staged archives and both source roots;
the closeout report parent must also be disjoint from the replay's retained output tree. Preparation
writes canonical preparation wire data. Closeout writes at most 64 KiB of canonical JSON with
schema `issue779-completed-validation-observation-v1`, both input wire SHA-256 values and the
controller's exact observation fields. These include the fixed false authority and cleanup claims.

The internal input context checks initial read metadata and final content under pinned descriptors.
The publication context creates one exclusive `0600` output, flushes file and directory metadata,
holds its descriptor through the enclosing root checks, and checks named identity and bytes again
before returning. A failure removes only its still-owned regular single-link output. A substituted
file or symlink is preserved. Staged archives, native/gate diagnostics and other files remain under
the existing controller and caller cleanup policies. Publication is not an execution-settlement
receipt, and a partial file observed before command success is not a usable handoff.

The CLI returns zero and emits only `EVIDENCE_COMPLETED_COMPARISON_RETAINED` on success. Expected
input, source, provider, I/O and subprocess failures return two with only
`EVIDENCE_COMPLETED_INPUT_REJECTED` on standard error. It does not print exception text, paths,
tokens or arbitrary input values. The Python APIs propagate their original exceptions for trusted
callers to handle without publishing them into public logs.

## Choosing the boundary

Use preparation and closeout in separately managed credentialed phases. Use the fixed replay
worker only in an independently isolated credential-free execution phase. Neither command starts
that worker or proves its containment. Root identities in preparation require the same filesystem
nodes and ownership at replay and closeout; transporting JSON to a different checkout or changing
ownership invalidates them. A same-UID child with a cleared environment can still access a live
credentialed parent's process state unless an outer boundary prevents it.

Before enabling the future workflow, its caller must independently establish credential placement,
process and filesystem isolation, resource bounds and full descendant settlement before restoring
provider access for closeout. No workflow trigger, support registration, Trusted admission, native
acceptance or gate eligibility is added here. The existing numerical evaluator, fixed comparison
base, thresholds, tolerance, exclusions and native clocks remain unchanged.

[`test_completed_entrypoint.py`](test_completed_entrypoint.py) exercises real private files and
the real wire codec with source/provider controller seams mocked. It covers successful publication,
context binding, input replacement, output substitution, late custody/deadline failures and fixed
CLI diagnostics. These controls verify the command mechanism, not a protected full-stage run.
