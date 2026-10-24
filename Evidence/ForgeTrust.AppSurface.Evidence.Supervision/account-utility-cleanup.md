# Account utility cleanup procedure

`SupervisionAccountUtilityCleanup` is an internal ordering seam for one already-created
`LinuxAccountUtility`. It is not an owner, account, systemd unit, deadline, token, pipe,
backend, cgroup, custody or settlement factory. The private caller supplies callbacks bound
to its retained native objects and keeps all native facts and final result state.

## Contract

The first `StopAndGroupAsync` callback contains the original compound cleanup `try` body. It
constructs the caller's existing cleanup deadline, uses the caller's existing token, stops the
pending unit when the original `started` condition is true, closes transferred writes, and waits
for the actual group to become empty. If any operation in that callback fails, the callback's
remaining operations are skipped and the caller captures the failure before the helper continues.

When pipes were acquired, the helper then attempts the second write-copy close, output join and
native group/owner check, and pipe disposal in separate failure boundaries. The second close is
intentional: the first close belongs to the compound stop phase, while the second close remains the
unconditional pre-join operation from the original caller. A failed stop therefore cannot detach
the output pump or turn a failed close into EOF, but later cleanup still runs.

Backend disposal always follows pipe handling. Physical settlement is attempted only after backend
disposal and only when the caller's current `nativeJoined && pipesClosed` predicate is true. The
predicate is evaluated at that point; the helper never snapshots or creates those facts. A backend
failure can therefore clear the caller's native-join fact and suppress settlement.

The no-pipe/not-started branch is an optional caller callback that sets the original local facts only
when the original `!started` condition holds. The helper does not infer that an absent callback means
a successful native operation. Callback failures are
reported to the caller's existing sticky latch and do not replace the original exception or stage
projection. No callback may create a new owner, unit, timer, token, native handle, or result claim.

## Testing contract

`SupervisionAccountUtilityCleanupTests` use deterministic ordered callbacks and injected exceptions.
They cover successful ordering, stop failure with continued later cleanup, first compound write-close
failure that skips the remainder of the compound phase, backend failure clearing settlement, the
no-pipe branch, late evaluation of the retained settlement predicate, and sticky failure continuation.
They use no sleeps, owners, native facts, reflection, native handles, or fabricated acceptance proof.

These tests verify procedure ordering only. Genuine account ownership, systemd stop, cgroup emptiness,
pipe EOF, account custody, deadline enforcement and final owner checks remain in `LinuxAccountUtility`
and require the existing native controls.

The no-pipes state update and final settlement predicate must be nonthrowing local
assignments/comparisons. They retain the original caller's uncaught boundaries;
they are not fallible cleanup operations. The failure sink must also be nonthrowing.
A throwing replacement violates this internal callback contract and escapes;
it is not converted into a successful native settlement or suppressed failure.
