# Private account lifecycle controls

These two closed Linux account mechanism controls run through the finite
[driver](../run-accounts.py) and [independent matrix workflow](../../workflows/issue779-checkpoint4-accounts.yml).
Local compile and image-metadata checks passed before driver integration; genuine Linux
execution and its standard report remain required. No native qualification is inferred.
The [linked implementation](LinuxAccountMeasurement.cs) runs only in the private
`C4Accounts` and `C4AccountRollback` builds. The ordinary `None` image contains no
fixture type, private observation methods, collision fields or fixture call sites.

## Build and entry contract

[`Directory.Build.props`](../../../Directory.Build.props) maps `C4Accounts` to
`EVIDENCE_PRIVATE_C4_ACCOUNTS`. `C4AccountRollback` additionally defines
`EVIDENCE_PRIVATE_C4_ACCOUNT_ROLLBACK`. The
[Supervision project](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/ForgeTrust.AppSurface.Evidence.Supervision.csproj)
links this file only for those two property values. The file rejects mixed private
image symbols at compilation. The existing non-`None` package guard still rejects
packing either image. Select the variant consistently across the complete project graph.
Use fresh build outputs when changing variants; ordinary and private outputs must
not overwrite one another during validation.

The private branch in
[`EvidenceProcessEntryPoint`](../../../Cli/ForgeTrust.AppSurface.Cli/EvidenceProcessEntryPoint.cs)
dispatches only the parsed supervisor role, after normal help handling. Its argv
remains `evidence supervise --request <protected-request-file>`. No argument,
environment variable, request field or callback can select a fixture operation.
The worker role gains no fixture dispatch. The existing qualification enum is not
used as an account-fixture command switch.

`LinuxAccountMeasurement.RunAsync(requestPath, originalToken)` permits one attempt
per process and returns a UTF-8 JSON byte array of at most 4096 bytes. The CLI writes
it only after that task returns. Unexpected operation, observation, cleanup or
deadline failures produce fixed `ASEVD410`; original caller cancellation is checked
after cleanup and retains the existing CLI cancellation result. No raw exception,
utility output, native pathname or account name is placed in the receipt.

## Authentic acquisition

The fixture must execute inside a fresh root Linux x64 systemd owner created by the
existing protected bootstrap recipe. The original protected runtime/tool trees,
request, cleared environment, full argv and armed owner marker are mandatory.
Running the fixture in an ordinary test host does not create owner authority.

The fixture calls the unchanged native factories:

1. `EvidenceProtectedLaunchInput.Open` retains and audits the actual input and deployments.
2. `LinuxSystemdBackend.ConnectAsync` connects to the real system manager.
3. `LinuxOwnerActivation.OpenAsync` authenticates the current service and process and
   consumes its real armed marker.

See [owner activation](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxOwnerActivation.cs)
and the existing [bootstrap recipe](../../issue779-checkpoint3/run-native.py).
The linked job token uses the original token and `input.Remaining`; it supplies no
new job allowance. A genuine `EvidenceEmptyObservationPlan.FromInput` must resolve,
and application selection and observation producers must both be absent.

## C4Accounts: successful account-only closure

The fixture invokes actual `LinuxRunAccounts.CreateAsync(owner, token, false)`.
It requires exactly three successful, physically settled forward utilities,
reference-bound account ownership, and equality between the retained identity
snapshot and a second actual NSS read. The latter repeats forward/reverse lookups
and primary-group-only checks. Successful utility settlement must leave the owner
active before teardown.

After `BeginRootTeardown`, two `CloseAsync` calls must return the same actual task.
That original task is joined before any result is interpreted. Closure must be
successful without quarantine, reservation failure or a first fault. Both closed
account ownership APIs must reject. The final retained utility count is 6 through 8:
three creates plus three mandatory deletes and zero through two private-group
deletes, depending on actual `userdel` behavior. Every retained executor must be
physically settled and have no first failure.

This is the existing no-workspace/no-worker cleanup path. It creates no consumer,
account-associated workspace or filesystem custody holder.

## C4AccountRollback: reserved fixture-owned collision

The first guarded hook is inside `LinuxRunAccounts.CreateCoreAsync`, after the
results-group deletion obligation is reserved and before its original normal dispatch.
Only the exact default-profile `CreateResultsGroup` command for this owner's
generated results name is eligible. The hook executes that command once through
the same retained `RunUtilityAsync`, requires all three setup utilities physically
settled, then captures the actual complete default-profile NSS snapshot.

The snapshot is stored in a private fixture field, **never** production `_identities`.
The unchanged normal dispatch then executes the same command, encountering the
real existing group. No arbitrary command, numeric account allocation, fabricated
process result or injected executor is used. Both attempts are retained before
dispatch, and reservation precedes both.

The existing catch performs its original reverse cleanup and throws its original
account exception. A second guarded hook, after that cleanup attempt, copies only
actual state into a one-attempt witness bound by reference to the authentic owner.
It cannot replace the original first failure, reopen work admission or return an
account holder. Unavailable witness data fails the fixture.

The runner requires actual `OperationFailed`, the identical retained first-fault
object, `UtilityExecute` / `TerminalCheck` / `CreateResultsGroup`, and observed
`ExecMainCode == 1` with nonzero status through 255. No particular shadow-tools
status is presumed. The original close task must have succeeded, production
identities must remain unpublished, reservations must remain failed and quarantine
must be false. Exactly one utility has a first failure. The retained utility count
is 7 through 9, and every executor must be physically settled.

This is a collision with the fixture's own freshly reserved group. It establishes
no authority to adopt or delete an unknown foreign account. A failure earlier than
the intended utility terminal check cannot pass as this control.

## Real NSS absence and finalization

Both variants require actual name absence through
`RequireNamesAbsentAfterCleanupAsync`. A private partial of the existing
`LinuxRunAccountNss` then calls its original numeric `User` / `Group` helpers for
the two captured UIDs and three captured GIDs. Each domain is positive,
non-sentinel and internally distinct; equality across UID and GID domains is valid.
Application identity fields must be zero for this default profile.

The numeric observer joins the original synchronous NSS task directly, with owner
and deadline checks around lookups. Provider errors, malformed results and numeric
ID reuse reject; they never become absence. No cleanup is performed by this observer.
Synchronous NSS still depends on the external owner runtime bound for containment;
there is no detached timeout or fake cancellation.

Finalization attempts account closure even if beginning owner teardown throws,
then independently attempts job, owner, input and backend disposal. A cleanup
exception prevents a receipt but does not skip later resource closes. The fixture
captures the original remaining monotonic cleanup interval before owner disposal,
checks it after all closes, and checks it again immediately before returning the
encoded receipt. Owner disposal's expected cancellation cannot masquerade as expiry
or grant a fresh interval. No task returns a receipt from inside the protected try.

## Receipt and meaning

The fixed schema is `issue779-c4-accounts-v1`. It records the compiled variant,
actual owner generation, retained managed-entry SHA-256, five allocated numeric IDs,
actual utility counts and closure/absence observations. `operation_failure` is null
for `C4Accounts`; rollback retains its actual closed failure fields and numeric exit
sample. `shared_close_task` and `closed_holder_rejected` are null for rollback,
because failed creation returns no production holder to the runner.

`native_acceptance` and `consumer_started` are always false. CLI exit zero means
this private mechanism control satisfied its own expected outcome; rollback still
records `creation_succeeded: false`. No manifest, worker success, accepted proof,
HTTP health or application qualification is fabricated. A future driver must bind
the complete private source and module hashes; the managed-entry hash alone is not
a complete module or source binding.

Before use, the parent must compile both variants, inspect ordinary `None` PE
metadata for absence of fixture types/methods/references, check the existing pack
rejection, and run the controls in fresh protected Linux owners. The initial handoff was
source-only; local compilation and image-metadata checks subsequently passed.
Native control outcomes, supported report integration and measured coverage gains
still require their own fresh evidence.

## Driver ordering and report custody

The driver performs `bind` before the pinned collector instruments the six selected modules,
then `run` as its actual target, and `finalize` only after collection and restoration of all
twelve original DLL/PDB files. Each compiled variant has a separate job, source capture,
artifact and report. The root-owned staged inputs use the existing C3 owner recipe and
its 1100-second service lifetime inside one 1200-second driver bound; the request retains
more than 1110 seconds at publication to cover that service plus stop time. It supplies
no solution, application, producer or consumer.

Finalization checks exact source/variant/image bindings, the native result and strict
receipt, original bytes restored, and real account/owner source hits in the standard
Coverlet JSON. Portable tests validate detached data and small files only. These reports
are not consumed by the final gate until a separately reviewed merge and binding check
has been connected; no raw counter or report arithmetic takes place in this driver.
