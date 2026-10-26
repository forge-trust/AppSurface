# Private account lifecycle controls

These two closed Linux account mechanism controls run through the finite
[driver](../run-accounts.py) and [reusable sequential workflow](../../workflows/issue779-checkpoint4-accounts.yml).
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

Publish both account variants in **Release**, matching the preceding C3/C3Sdk images,
the later C2/None images and the solution run. Debug and Release can map the same
unchanged source condition to different IL offsets. The standard collector retains
both maps when merged; it cannot infer that those rows describe the same condition.
Keep the original reports intact and collect fresh, consistently configured builds
instead of rewriting or deduplicating counters.

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

Before `BeginRootTeardown`, C4Accounts also performs three read-only cleanup-name
checks against those actual NSS entries: the worker `DeleteUser` command with only
its expected UID changed to the distinct allocated subject UID, the same command
with only its expected GID changed to the distinct subject GID, and the results
`DeleteGroup` command with only its expected GID changed to the distinct worker
GID. Each must throw `LinuxRunAccountException` with `IdentityMismatch`; any other
exception, no exception, or reported absence fails the control. Repeating each
check with the original expected snapshot must return `false` (present and matching),
and a fresh independent NSS read plus a second account-state snapshot must still
equal the originals. These probes dispatch no utilities and do not delete the
tampered identity. The normal shared `CloseAsync` sequence then performs the only
account deletions. The C4 receipt sets `cleanup_identity_guards_checked` true;
rollback leaves that field null because it never reaches successful identity
publication. This source change still requires a fresh genuine Linux run.

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
sample. `cleanup_identity_guards_checked` is true only after all three negative and
matching-positive NSS checks complete without changing the created utility/account
state; it is null for rollback. `shared_close_task` and `closed_holder_rejected`
are also null for rollback because failed creation returns no production holder to
the runner.

`native_acceptance` and `consumer_started` are always false. CLI exit zero means
this private mechanism control satisfied its own expected outcome; rollback still
records `creation_succeeded: false`. No manifest, worker success, accepted proof,
HTTP health or application qualification is fabricated. The driver binds the complete private source and module hashes; the managed-entry
hash alone is not a complete module or source binding.

Before use, the parent must compile both variants, inspect ordinary `None` PE
metadata for absence of fixture types/methods/references, check the existing pack
rejection, and run the controls in fresh protected Linux owners. The initial handoff was
source-only; local compilation and image-metadata checks subsequently passed.
Native control outcomes, supported report integration and measured coverage gains
still require their own fresh evidence.

## Driver ordering and report custody

The driver performs `bind` before the pinned collector instruments the seven selected modules,
then `run` as its actual target, and `finalize` only after collection and restoration of all
fourteen original DLL/PDB files. Each compiled variant has a separate job, source capture,
artifact and report. The root-owned staged inputs use the existing C3 owner recipe and
its 1100-second service lifetime inside one 1200-second driver bound; the request retains
more than 1110 seconds at publication to cover that service plus stop time. It supplies
no solution, application, producer or consumer.

The closed module set is CLI, Contracts, Planner, Coverage, Aspire, Supervision and the shared [Evidence.Cli workflow assembly](../../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md). Its DLL and PDB join the same source-bound original/instrumented inventory and restoration checks. Both account stages require the exact seven-module predecessor and output report; a missing or aliased Evidence.Cli module fails even when the remaining six modules and their hashes are consistent. Account utility counts, deadlines, retention limits and native cleanup criteria are unchanged.

Finalization checks exact source/variant/image bindings, the native result and strict
receipt, original bytes restored, and real account/owner source hits in the standard
Coverlet JSON. Portable tests validate detached data and small files only. The proposed integration below uses only the pinned collector's standard merge; no
raw counter or report arithmetic takes place in this driver.


## Prepared supported merge lineage

This is an isolated source proposal at baseline
`488143ea14b791fae6755bec50f33646e262c3fb`. No source29 account artifact, native
control, report arithmetic or coverage gain has been verified by this preparation.
Adoption requires parent review, actual current-source account receipts and the
parent-owned C# account lineage validator. No native test or runtime source is
changed here. The existing C3 seven cases, six workspace controls, 41 root tests,
original clocks, source base and coverage command/policy remain unchanged.

The [runtime workflow](../../workflows/evidencehost-runtime-proof.yml) proposes this
single chain, with ordinary successful-job dependencies at each arrow:

`application-consumers -> account-creation -> account-rollback -> solution-coverage`

The [reusable account workflow](../../workflows/issue779-checkpoint4-accounts.yml)
accepts exactly three required string inputs: `variant`, `prior_kind`, and
`prior_artifact_name`. Only `C4Accounts` with `c3`, followed by
`C4AccountRollback` with `accounts`, is valid. It returns `artifact_name` through
its job output. Downloads use the fixed name from the successful predecessor in
the same workflow run/attempt. A prior artifact is data, never runtime authority.
The reusable workflow has no standalone dispatch without a predecessor.

### Prior inputs and validation API

All `bind`, `run`, and `finalize` calls now additionally require:

- `--prior-kind c3|accounts` and `--prior-artifact-name NAME`;
- absolute `--merge-report`, `--merge-binding`, `--merge-source`, and
  `--merge-native` paths;
- `--merge-root-trx PATH` for C3 only; it is rejected for the account predecessor.

The downloaded artifact is kept outside the output evidence directory. The
workflow copies only fixed inputs into a private temporary directory and sets the
leaves to `0400`. The driver reads stable single-link regular files without
following leaf symlinks or blocking on FIFOs. Reports alone may be 32 MiB; source,
binding, receipts and TRX retain the 8 MiB limit. Prior `source.json` must be
**byte-for-byte equal** to the complete current source capture, including the
captured file hashes and modes. Semantic JSON equivalence is insufficient.

For C3, require the existing `issue779-native-instrumentation-v1` schema,
source/collector/module bindings, report/source/native-receipt SHA-256, exact
seven-case and six-guard order with passing and physically empty groups, and the
actual hash-bound TRX with 39 executed/passed and no failed/skipped results.
The existing native measurement tests still own their full C3/SDK consumer checks;
this premerge check does not replace them.

For the account predecessor, require `issue779-c4-account-measurement-v2`, variant
`C4Accounts`, its successful driver receipt and strict actual C# cleanup receipt,
source/module/report/native-receipt hashes, and its C3 predecessor lineage fields.
`creation_succeeded` means the expected account-only mechanism result, never a
consumer or accepted proof. The rollback stage cannot consume itself or a
standalone pre-lineage v1 account report.

### Binding and collector ordering

`account-original.json` records the prior provenance at bind time, before
instrumentation. `account-instrumented.json` must retain identical provenance.
`measurement-binding.json` uses schema `issue779-c4-account-measurement-v2` and
preserves the original source/image/report/native-receipt fields plus:

| Field | Meaning |
| --- | --- |
| `prior_kind` | `c3` or `accounts` |
| `prior_variant` | `C3` or `C4Accounts` |
| `prior_artifact_name` | Exact workflow predecessor artifact name |
| `prior_coverage_sha256` | Exact standard report supplied to Coverlet |
| `prior_input_binding_sha256` | Exact predecessor binding bytes |
| `prior_source_capture_sha256` | Exact predecessor/current source capture bytes |
| `prior_native_receipt_sha256` | Exact predecessor native receipt bytes |
| `prior_root_trx_sha256` | Actual C3 TRX digest; null for the immediate account predecessor |

Bind, target entry and finalization each revalidate prior inputs. Finalization
requires restoration of all fourteen original DLL/PDB bytes, unchanged prior input
hashes, a passing own native receipt and the completed seven-module report. It emits
its binding only after those checks. A nonzero collector exit remains the workflow
exit even if finalization also fails.

Each account collector receives exactly one `--merge-with` path. The first reads
the original C3 `native-supervision.json`; the second reads the successful first
account stage's combined `native-accounts.json`. Only standard Coverlet performs
these merges. Module-key compatibility across the private variants remains a
native validation obligation; seven-module shape checks fail closed if it is absent.

### Final staged paths and C# lineage validation

The original C3 files and `measurement-binding.json` remain unchanged at
`TestResults/issue779-native-measurement/` for the existing `NativeMeasurementTests`.
The workflow additionally stages exactly four fixed leaves under each of
`accounts-creation/` and `accounts-rollback/`: `source.json`,
`measurement-binding.json`, `native-accounts.json`, and
`native-accounts-receipt.json`. The runsettings `MergeWith` points to
`accounts-rollback/native-accounts.json`, a distinct path. No final report is
substituted for the C3 raw report.

[NativeMeasurementTests](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/NativeMeasurementTests.cs)
compares both complete staged source captures byte-for-byte with its original C3
capture. It calls `AccountMeasurement.ValidateMerged` for each v2 packet and
[AccountMeasurementLineage.Validate](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/AccountMeasurementLineage.cs)
with the original capture, C3 binding/report/native receipt/root TRX, then creation
and rollback binding/report/native bytes. The eleven-argument data API checks both
immediate predecessors, their shared C3 ancestor, exact run/attempt artifact names,
41 passing root tests, and each current variant's source-bound method witness.
Only reports allow 32 MiB; other inputs allow 8 MiB, with 160 MiB total and the
existing 1 MiB root-TRX bound. It constructs no account, owner, admission or proof.
A failed consistency test fails the solution run; the coverage gate is unchanged.

### Retention and validation status

[check-account-retention.py](../check-account-retention.py) accepts only the fixed
account artifact leaf names. Exactly `native-accounts.json` permits 32 MiB; every
other leaf remains capped at 8 MiB. Unknown names, directories, symlinks, hardlinks
and special nodes reject. The existing 128-file / 96 MiB aggregate limits remain;
no generic, product or C3 quota changes. Prior downloads are outside this artifact.

The [17 detached lineage/retention controls](../test_account_lineage.py) and
[eight original driver controls](../test_run_accounts.py) pass locally. The
[account packet tests](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/AccountMeasurementTests.cs)
and [lineage tests](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/AccountMeasurementLineageTests.cs)
pass 57 focused cases after source compilation with no compiler diagnostics. They
cover source-byte mismatch, rebound forked ancestors, wrong variant/order/schema,
false cleanup data, missing or failed root TRX, current-witness absence despite
positive predecessor hits, and actual file/link/FIFO/size retention boundaries.
These constructed inputs establish data checks only. The earlier standalone v1
creation and rollback controls passed genuine Linux execution; this new merged v2
chain still requires a fresh successful C3 → creation → rollback → full gate run.

The fixed original root-test inventory now contains 41 passing tests: the prior
39 controls plus the two retained-node type/read-bound rejection controls in
[`LinuxProtectedNodeNativeTests`](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxProtectedNodeNativeTests.cs).
Account lineage continues to require exact counters, hash-bound TRX bytes and
only passing results. A self-consistent obsolete 39-test packet, a missing test,
or an extra-count packet is rejected; this is an inventory update, not a relaxed
acceptance or coverage rule. The separate 41-test supplementary root report
retains its own filename and predecessor bindings.
