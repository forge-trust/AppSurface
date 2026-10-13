# N13/N14 owner-death inputs — source preparation only

This fixed installation binds source `fc59bb1254294d624f6779f928020edfe988dd5e`, direct parent `1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38`, and 2,869 captured source files. The installed harness must contain exactly the 22 added paths declared by `build-fdd.sh`, for 2,891 tracked physical files.

`prepare-ubuntu-runtime.py` and `prepare-os-audit-v2.py` are reused as generic prerequisite mechanisms from the reviewed N09 basis. They do not establish an owner-death result, admission, acceptance, or native proof. `retain-native.sh` preserves fixed bounded custody/archive handling with an N13/N14 record allowlist; the N16 case labels and schemas are excluded. The N13/N14 wrappers forward every caller operand unchanged and append only their closed selector to the v3 builder.

The workflow contains two sequential explicit case stages, N13 then N14. Each stage invokes its fixed build wrapper and the actual owner-death runner. The original build, runner, fixture, stop, and owner-death bounds remain unchanged. The v9 review records are historical only. This v10 source/capture retarget is pending independent whole-packet review; source, fixture, transport, retention, and handoff gates are false. No native execution, credit, or acceptance is claimed. The embedded fixture preparation is inert pinned data used only by the existing acquisition ABI. The pinned `prepare-case-workspace.py` materializer runs after acquisition, inbound transport, and OS audit, and before either selected owner-death fixture. It validates the actual selected N13/N14 build receipt and physical source/tool/runtime inventories, preserves the request deadline, stages generation-owned read-only trees plus a private request, and emits a digest-bound staged-only marker. The fixture requires those staged trees and request before arming its guard; it does not create or replace them.

Acquisition CLI is fixed to `--execute --case N13|N14 --generation --build-root --reviewed-root --deadline-monotonic-ms --reviewed-script-sha256 --transport-sha256 --fixture-sha256 --source-capture-sha256`. The builder requires six completed .NET commands in order: restore, publish, restore/test, restore/test; every selected command carries one matching private selector, has joined logs/group, no timeout/forced cleanup, and the receipt has empty diagnostics and the two exact qualification result keys. Those are source contracts only here.

No .NET, root acquisition, build, Git network, CI, or native command was run. All runtime TSV identities and the eventual workflow/source review remain pending independent review.


## Timing metadata correction

The earlier v10 preparation metadata declared a 180-second owner runtime and a 185-second N14 bound without matching the pinned fixture policy. The actual unchanged common fixture and record parser use `RuntimeMaxSec=240s`, `TimeoutStopSec=5s`, and an N14 upper bound of 245 seconds. This v11 correction updates build/runner metadata only; the original 240-second request-job deadline, 600-second fixture ceiling, 30-second fixture cleanup reserve, and systemd limits remain unchanged.


## Common source capture v4

This v10 source candidate binds validation commit `fc59bb1254294d624f6779f928020edfe988dd5e`, its direct parent `1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38`, tree `c9ed8adcc08d050dc70af6885d31171038a36f86`, and 2,869 source files from the pinned common-source capture (`6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1`). The capture's nested `source.sha256`/`source.modes` maps are validated with a root-bound jq predicate and by the builder/runner. Map encodings retain separate roles: `02b833b330267cafc6e12e75ac82439c6ee80a7e839f7fad27d7097fac8da843` is the compact sorted full-row SHA/mode map used by `source_capture_map_sha256` and root transport; `bf7334e8a273dc5693bb9f2226c4b44c90c988fb2f2c8d56ddb0b840adc494c6` is the compact sorted nested source object used by builder product `source_map_sha256`; `f47737aaad2455132ae7f0a475babc9d3bc721c75140aaa7d1883779c6771f47` is the two-space indented full-row map with a final LF recorded in source-review metadata. The builder capture projection is `8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0`. These digests are not interchangeable.

The owner timing contract remains the unchanged 240-second request and owner runtime, 5-second stop, 245-second N14 upper bound, 600-second fixture ceiling, and 30-second cleanup reserve. This packet is source preparation only; all review, execution, and native-credit gates remain closed.

### Pre-measurement failure cleanup

If the fixture exits after launching the original owner-service launcher but before the accepted phase and identity measurement complete, its EXIT handler preserves the original nonzero status and leaves the quarantine marker in place. Within the existing cleanup reserve and absolute fixture deadline, it addresses only the generation-derived owner and worker unit names. It samples both units, exact matching systemd jobs, `MainPID`, terminal state, `ControlGroup`, and the fixed physical cgroup `cgroup.events` value. A not-found unit with a matching pending job is still treated as unsettled and is included in the generated-unit stop request.

Cleanup requests a nonblocking stop for unsettled generated units, joins the captured launcher process group, and polls for both units to be terminal, job-free, and physically unpopulated. If the stop request errors or the five-second stop window expires, it issues a unit-scoped KILL to those same generated names and continues bounded settlement checks. The launcher group is signalled only through its captured PID/PGID pair; a confirmed zombie launcher is waited before the group-absence check can complete. Unknown facts or exhausted time leave the fixture failed and quarantined. No arbitrary sampled `MainPID` is signalled, and account/workspace state is retained.

The bounded `PreMeasurementCleanupControls` in `test_owner_death_data.py` extract and execute these fixture shell procedures with mocked `systemctl` responses and temporary cgroup facts. They cover ordinary pre-measurement cleanup, a not-found unit with a queued start job, already absent generated units and physical groups without jobs, STOP timeout and error fallback, foreign cgroup facts, deadline exhaustion, and a disposable launcher process group. These controls are data/procedure tests only; they do not exercise systemd, root acquisition, or native owner-death behavior.

An absent physical cgroup accepts an empty sampled path or the exact generation-selected path. A not-found unit passes that selected path to the physical check so a residual group can still be inspected. Its absence is settled only after the matching job check reports no job. Foreign paths and links remain unresolved; settlement preserves the original failed fixture result and quarantine.


## Generation-owned workspace materialization

After root acquisition, inbound source/tool/runtime transport, and the existing OS audit, the runner invokes the pinned `prepare-case-workspace.py` Python helper from its root-owned reviewed bootstrap. It supplies the selected N13 or N14 build receipt, all three inbound roots/maps/node inventories, the current common-source capture, workflow identity, and one original 240-second UTC/boottime deadline. The helper validates the exact selected-variant command inventory and source/map bindings, materializes generation-specific readonly source/tool/runtime trees, writes the fixed empty-profile policy and request, and emits a staged-only marker; the runner then reads the root-owned checksum record with a bounded, no-follow command and verifies both exact generated files before fixture dispatch. It does not launch the owner or worker. The existing owner fixture then validates and consumes those staged paths; it no longer creates a competing workspace or request. The helper's 60-second cleanup reserve stays within the supplied request deadline. Existing 600-second outer fixture ceiling, 30-second fixture reserve, 240-second owner runtime, 5-second stop, and 10-second admission/start/collection values remain unchanged.

The added `test_workspace_arguments.py` and `test_build_receipt_contracts.py` cover closed helper arguments and synthetic build-receipt data only; they are not staging or native proof. All independent-review and native gates remain closed.


### Candidate history and embedded preparation binding

The earlier v2/v3 candidate reports of 19 installed inputs and 46 literal edges describe those historical packets only. This v6 packet has 22 installed paths and derives its bindings from the frozen 22-file map. Its embedded fixture-preparation record repeats the current source head, parent, and count; each of its four file rows is checked against the corresponding physical input. The N13/N14 review gates remain closed until independent review of this current packet.

## Original launcher join before measurement

The cleanup launcher is retained before process creation. Its setsid-ready marker
binds the exact direct child before any group signal. Pending launch cleanup first
settles the generated units, then terminates only that retained child if its group
has not formed. The original cumulative cleanup deadline also bounds this path.
Every `wait` in the premeasurement cleanup procedure follows proof that the original
child is already terminated (zombie or absent from procfs), so the wait cannot block
on a still-running child. Shell-reaped children retain their numeric wait status.
Unknown process state or group membership remains failed and quarantined.

Focused development-host controls exercise the extracted shell procedure with
real disposable child processes. Their portable process-state projection is not
Linux systemd, root authority or native N13/N14 acceptance evidence.

### Pending direct child and late unit cleanup correction

Before the setsid-ready marker exists, cleanup keeps the exact original child
wait handle. After the initial generated-unit join, it sends TERM only to that
child, then KILL if the same cumulative cleanup allowance permits and the child
has not exited. If its matching ready marker appears meanwhile, cleanup switches
to the retained process group and verifies its absence. Zombie or shell-reaped
children are waited before clearing ownership; no sampled service PID becomes a
launcher signal target. After the launcher has joined, cleanup samples the two
original generated units again and stops any start submitted while that launcher
was still alive. It never resets the cleanup deadline or upgrades the original
failed result.

The local final class ran thirteen extracted cleanup controls successfully. Five
new FIFO-coordinated real-child controls cover an unregistered child, ignored
TERM, late group registration, a leader already exited, and a generated start
submitted during final launcher termination. The initial class run retained one
older group-control error caused by an escaped ready marker and sandbox signal
denial; the corrected affected test passed separately outside the sandbox before
the final thirteen-control run. These are local procedure controls, with no
Linux systemd owner-death credit.

## Unreaped launcher custody correction

The fixture now owns its launcher through a private standard-library parent. It
creates the control socket before forking, publishes the child PID before releasing
the child, and uses `waitid(WNOWAIT)` without a reaping SIGCHLD handler. Until group
join finishes, the launcher remains a live child or unreaped zombie, reserving its
PID and process-group number across TERM/KILL. The parent performs the only
`waitpid`, after child exit and absence of other group members; only then may the
shell clear launcher ownership. A saved Bash status, procfs check, or jobspec is
not an identity-bound signal handle. See the [waitid reference](https://pubs.opengroup.org/onlinepubs/9799919799/functions/waitid.html).

This parent is validation-harness custody, not the product supervisor. It runs no
Evidence operation and issues no admission or acceptance. The protected product
continues to execute C# supervisor/worker roles. Fixed unit policy, deadlines,
account retention, source binding, and failure quarantine are unchanged.

The shell additionally verifies physical exit of the fixture parent within the
same cumulative cleanup deadline before waiting its retained status. This query
never authorizes a signal to the parent's numeric PID. Deadline/error cleanup
signals the parent's still-unreaped child/group without invoking an already
expired inventory scan; failed or ambiguous cleanup remains quarantined.

Error finalization retains the unreaped leader until the same deadline-bounded
scan shows no other process-group members. A real exited-leader/live-descendant
control checks that malformed control input cannot bypass this group join. If
the final deadline expires or group sampling fails, finalization does not claim
join or publish a successful receipt; the outer fixture stays quarantined.
