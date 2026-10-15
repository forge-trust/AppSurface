# N15 pending-start owner-death fixture source

The observer-specific notes below preserve the v3 component contract; the current source-only integration status is recorded in the v6 section at the end of this file. This packet is bound to product source `a6933fa3dc2c7a88832abf337447f88527477703`, direct parent `0adef6032217b4181898a4d6d129e1248cb0eda3`, and the common-source-v4 full 2,869-path capture receipt with SHA-256 `6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1`. The captured C# source is immutable in this packet; no product-source files were changed.

The observer accepts only the real source hook's four-field stderr frame: `case_name=N15`, `phase=start-transient-unit-reply`, the source-generated worker unit, and the actual systemd job path. That frame is a trigger only. Immediately after parsing the real frame, it samples the original `CLOCK_BOOTTIME` Admission deadline and rejects unless 5,000 through 30,000 milliseconds remain. The same absolute startup, Admission, job, and work deadlines continue to bound observation and signalling; the identity window remains at most five seconds. No deadline is reset, renewed, or extended. The partial result records the remaining Admission time at the trigger.

The observer continues to bind worker MainPID, `/proc` starttime, all four UID/GID slots, systemd User/Group, ControlGroup, and live cgroup membership. It derives the owner unit from the same generation, rechecks root identity, opens a pidfd, rechecks identity, and sends one SIGKILL through that pidfd. It has no numeric-PID fallback. It waits only under the supplied original work deadline for owner SIGKILL terminal state, worker nonzero termination and two empty cgroup-tree samples, then verifies the consumed owner marker and absent armed marker.

The observer emits only the closed `issue779-n15-native-observer-partial-v1` shape. It includes the actual generation, phase digest and job path, measured Admission remainder, observed process/unit/guard fields, and explicit false authority/acceptance/credit fields. It intentionally omits source identity and reactivation. `check_n15_records.py:assemble_final_record` is a pure adapter: it requires a separately supplied, closed `issue779-n15-outer-completion-evidence-v1` record with the same generation, exact source commit and capture digest, and all five externally measured same-generation reactivation outcomes. It copies those source/reactivation fields and the observer's existing values into the parser's unchanged exact final-record schema. Missing or mismatched evidence and any attempt to add native flags are rejected. The adapter does not establish the provenance of external measurements; the outer runner must acquire and bind them, and retain the partial record alongside the assembled record.

The adapter never launches or synthesizes the reactivation attempt. The following remain parent integration prerequisites: stage and pin the full source/runtime/request/parser inputs before owner launch; bind the original absolute BOOTTIME deadlines; perform and independently observe the same-generation reactivation rejection; bind the final runner/runtime/build/workflow digest DAG; join the original launcher and both pumps; and retain bounded logs/quarantine evidence under the existing cleanup reserve. Therefore this component is not integration-ready and has not established native Linux, root, systemd, .NET, or native qualification. All native credit and authority remain false.

The focused data-only controls use detached records and arithmetic boundary values. They create no unit, signal no process, and do not establish native execution.


## N15 current-source integration status

The v3 source-preparation packet adds a fixed owner-launch path and constructs one request before dispatch. After root transport and the same-host OS audit, the runner invokes the original C# `evidence supervise --request` command in a generation-named root transient unit. The fixture passes that process's actual stderr file to the pinned N15 observer, waits for the observer's PIDFD signal and terminal owner/worker/cgroup observations, joins the original `systemd-run --wait --pipe` client, and invokes the pinned v7 same-generation replay helper under the same absolute job/work deadlines. These are source-preparation mechanics only; the review gates remain false and no native attempt is authorized.

The v5 caller now invokes the separately pinned, source-bound case-workspace materializer after root transport and the OS audit. It consumes the materializer's exact `/run/appsurface-evidence-fixture/<generation>/request.json`, and the fixed owner fixture uses the materialized `/var/lib/appsurface-evidence-fixture/<generation>/{tool,runtime,subject}` trees. It does not create or overwrite that generation directory. The materializer and runner bind the original 240-second UTC and BOOTTIME deadlines; work stops 60 seconds before the original job deadline, while startup (30 seconds), admission (10 seconds), and stop (5 seconds) remain independent and unchanged.

The v6 source packet adds a fixed root-side finalizer after the original `systemd-run --wait --pipe` child has been waited and `/proc/<pid>` confirms the launcher is gone. It rechecks the worker's same-generation replay evidence and raw-file digests, verifies the generated account names still map to the recorded numeric IDs, records the root-owned workspace identity, deletes only those fixed generated names with fixed `userdel`/`groupdel` utilities, and requires both name and numeric lookups to be absent. Cleanup evidence is written before a cleanup failure is returned, so a failed utility does not get turned into a successful final record.

Only after cleanup succeeds does the finalizer call the existing `check_n15_records.py:assemble_final_record` and `validate_result` APIs to write the parser's nested final record and summary. The runner retains the bounded partial record, retry measurements, raw stdout/stderr and snapshots, owner-launch output, cleanup receipt, nested final record, request, and policy checksum. Its archive validator checks the fixed names, modes, sizes, canonical tar encoding, exact generation/deadline binding, cleanup outcomes, raw hashes, and the parser's real `validate_result` contract; there is no `validate_bytes` call. Acquisition compares `capture_input_sha256` with the digest passed through the verified `--arg capture` operand, while the distinct `capture_projection_sha256` remains separately pinned.

The original Admission phase still accepts only 5,000 through 30,000 milliseconds remaining. Job, startup, Admission, stop, work, and cleanup bounds remain the original absolute deadlines; this integration adds no timer, reset, extension, retry authority, or acceptance state. Detached controls cover the Admission endpoints and out-of-range values, account name/ID reuse, complete utility-row coverage and failures, launcher join, replay evidence, raw digest mismatch, and the actual acquisition capture operand. They do not invoke NSS, root utilities, systemd, or the native fixture.

This is source preparation only. The source-capture identity remains the immutable common-source-v4 capture; the seven workflow review gates and the current parser, retention, and native-handoff source-review gates are clear. Native execution, native credit, and acceptance remain false/pending. No native execution or qualification is claimed, and the bounded syntax/data-only checks listed in the source-ready manifest do not establish Linux behavior.


## v8 installation closure correction

The v7 packet listed `prepare-case-workspace.py` as a runner support input but omitted it from the exact installed Git delta. The v8 builder installs it as part of the fixed 20-path delta. The workflow's pre-build source-byte table now contains all 19 non-workflow inputs, including both `prepare-case-workspace.py` and `observe_n15_pending_start.py`; the runner also pins these files in its reviewed-file table. This closes the pre-bootstrap dependency gap before the runner can read the materializer.

The filename handling remains closed by fixed installed-file tables and path-component checks. The N15 acquisition/transport guards reject absolute paths, empty components, `.`/`..`, `.git`, controls, backslashes, and colons where applicable; they do not use an alphanumeric-only filename regex, so the underscore in `observe_n15_pending_start.py` is accepted. No filename guard was widened. This is source preparation only: current static source-review gates are clear; execution/acceptance/credit remain false and native qualification remains pending.

## v9 installed mode correction

The private source packet keeps `capture-receipt.json` physically at `0600`. Its committed installation is intentionally `0644` with Git mode `100644`, like the other 19 installed files. The builder requires the exact 20-path installation delta and checks every installed regular file against matching worktree, index, and committed-tree mode maps. Workflow preflight checks all 19 pinned files plus the workflow itself for physical mode `0644`; no installed input is executable. The data-only mode control covers staging `0600` to installed `0644` normalization and rejects mode mismatches and incomplete maps. These source checks have not been run against a publication clone; native qualification remains pending.

## Current N15 publication status (2026-10-13)

The prepared installation targets `codex/issue779-csharp-linux-native-20261013-n15-reviewed-promotion-v22`. Its workflow push filter names that same branch. The reviewed v20 packet targeted v19; this derivative gives the reviewed installation a fresh branch and keeps the push filter aligned. This changes dispatch routing only.

The captured product source remains `a6933fa3dc2c7a88832abf337447f88527477703` with 2,869 source paths. All seven workflow review gates and the parser, retention, and native-handoff source-review flags are clear under the current static review. Native execution, native credit, and acceptance remain false; this packet does not claim Linux qualification.

## N15 build recipe correction (2026-10-13)

The previous native attempt rejected the completed build before dispatch: its runner expected 43 commands, while the reviewed build includes 45 after the pipe regression restore/test pair. The runner now verifies all 45 sequential ordinals, phase membership, successful joined terminals, and the exact pipe pair. Data controls use the captured build rows and reject omissions, extra commands, changed procedure, nonzero results and incomplete joins. The full captured build/map validator is also exercised locally. Product source, root lifecycle, identities, limits and native acceptance requirements are unchanged. This correction provides no native credit until the corrected fixture executes and its retained evidence is verified.

The v24 correction also exercises the complete captured receipt through the actual workspace validator and the exact root transport jq predicate. Both require the same 45-command inventory and pipe pair before workspace/account/start I/O. Earlier local runner checks alone did not cover those downstream gates. No runtime policy or acceptance criterion is changed.

## Pending-start observer window correction (2026-10-15)

Run38699334933 rejected at the observer's admission-window calculation before any identity or signal operation. The private C# hook in `LinuxSystemdBackend.Start.cs` is inside the original `LinuxWorkerProcess.StartCoreAsync` Start30 token, before creation of the control server or its Admission10 interval. The runner had labelled launch+10s as the observer Admission deadline, while its data parser also required at least five seconds remaining. Startup consumed that unrelated window.

The existing `--admission-deadline-boot-ms` observer argument retains its ABI name, but now equals the same ORIGINAL `startup_deadline` computed once from launch+30s, capped by the original work deadline. The observer's 5–30second remainder and all identity, pidfd, group, signal, replay, account and join guards remain unchanged. No timer is renewed at the phase trigger. The actual protected request remains Admission10, Start30, job240, stop5; the product image and runtime behavior are unchanged. This is a fixture observation-window correction, not evidence that real admission gained time or that N15 passed.

## Numeric systemd account properties — 2026-10-15

Run38703260712 failed in account-snapshot/generated-worker-account-name before any pidfd signal. Immutable LinuxWorkerUnit emits numeric User and Group strings, whereas the observer required the generated evw name in those properties.

The observer now derives all three names from the exact generated worker kernel cgroup already bound by unit_identity, resolves numeric or named systemd properties through the existing bounded account parser, requires the same observed process UID/GID, all five distinct identities, matching primary groups and empty memberships, and adds reverse NSS equality checks. No signal, deadline, restart, guard, terminal, replay or cleanup rule changes. Data-only tests do not establish native account or signal authority.

# N15 terminal process polling

Run38810092731 failed in owner-terminal/proc-process-state after the pidfd signal, before terminal/guard/replay completion. The new interpretation is confined to post-signal polling.

`proc_identity(pid, include_terminal=False)` still rejects Z/X for authentication. In post-signal mode a Z/X stat or a FileNotFoundError/ProcessLookupError opening its proc names raises the fixed TerminalIdentityPending category. It returns no identity or physical-exit fact. `unit_identity(..., include_terminal=True, terminal_pid=original)` first requires an exact original MainPID or zero and unchanged unit policy. A live sample must still match all kernel UID/GID/cgroup guards. Both post-signal loops may only poll pending under their existing original deadline. Owner live samples additionally require the same captured start identity; replacement rejects. Permission, malformed and other errors still reject.

Final success still needs systemd MainPID0 with the original ExecMainPID/code/status, exact generated groups empty, original deadlines, consumed activation guard, reactivation denial, joined original launcher/output, strict account cleanup and bound final records. No signal, product limit, restart or acceptance rule changes.

Ten detached tests execute the actual identity and worker-terminal functions with fake proc/NSS/systemd data. They exercise Z/X and missing-name transitions, unchanged authentication rejection, original PID binding, wrong credentials/cgroup, nonempty groups and successful final terminal data. They grant no root/native/cleanup authority.

# Empty replay stdout retention

Static review found a deterministic contradictory prerequisite in the N15 finalizer: validate_replay_observation requires retry.stdout == empty, but read_fixed rejected st_size0 before reaching that check. This is a source-proven next-stage blocker, not a measured native finalizer failure.

The reader now has keyword-only allow_empty=False. Exactly the fixed retry.stdout binding opts in. All other reads stay nonempty. File type, root uid/gid, mode, single link, no-follow open, FD identity, actual EOF, length, hashes, original deadline and final replay rejection remain mandatory. An empty stream cannot supply an exit or guard fact.

# N15 initial activating-worker identity sampling

Run38813910715 failed worker-process-binding during initial process-identities, before account snapshot or signal. The existing five-second initial identity loop may query systemd before its activating/start child has reached final credentials/cgroup. Actual mismatching fields were not retained; do not claim which field failed in that run.

Only initial worker sampling opts into pending_start. A mismatch is pending only while unit ActiveState is activating and SubState is start/start-pre/start-post. The exact generated unit ControlGroup, no-restart policy and parsed PID remain required; no mismatched kernel sample is returned or used for signal. Active/running mismatches, owner checks and every pre-signal recheck remain strict. Polling uses the same original identity/start/job deadlines. A hard mismatch additionally writes at most1KiB fixed private numeric/boolean/closed-enum diagnostic data, omitting actual cgroup paths and arbitrary text. This data cannot change rejection or establish a claim.

# Exact N15 successful wrapper output order

Static source inspection found the runner expected two stdout lines in the opposite order from its actual shell+finalizer. The actual fixed path emits dispatch, successful final-record marker, then the measured original-launch join line. All three lines are now mandatory in that exact order, with the same numeric fields and byte-count checks. Extra bytes, reordered/duplicated/missing lines, failed observer status and unknown content reject. This is canonical data validation only; source analysis is not a measured native finalizer/validator success.

# N15 final worker poll diagnostic

Run38816938950 passed initial kernel identity, account checks and the original pidfd signal, then exhausted the original worker-terminal wait. Its final systemd values were not retained, so no exit-code, unit-state or underlying-stop cause is claimed.

Only on that existing failed-wait path, the observer now emits at most1KiB closed JSON from the last values already read: original/Main/ExecMain PIDs, ExecMainCode/Status, closed active/substate/restart, sample count and whether a live main sample was returned. Unknown/malformed numbers become null and unknown states become other; no raw path, error or property text is retained. There is no extra query, reopened deadline, new state acceptance, fallback signal, group relaxation or exit claim.

## Incomplete phase frame

The C# hook writes JSON and its newline separately. The observer waits for that newline within the original phase deadline before invoking the unchanged strict single-record parser. It never accepts a prefix, ignores extra lines, or renews time. The latest native failure occurred at phase-read; its exact intermediate read was not retained, so the source race is reproduced locally without claiming it as the measured intermediate bytes. Complete malformed input continues to fail.

Pinned-parser failures also retain only their known fixed reason in the existing closed diagnostic. Unknown reasons become phase-parser-rejected; raw JSON and exception text are never echoed. Parsing still fails.

## Retain terminal inspection metadata

Run38819540553 retained original workerPID16509 but final selected-unit MainPID/ExecMainPID/Code/Status allzero/inactive/dead across4399queries. These values cannot establish exit. Systemdv255 unit.c427 retains units while their D-Bus track count is positive; RefUnit/UnrefUnit track the client connection (dbus-unit.c584 and2399). The root fixture now holds one libsystemd system-bus RefUnit for the exact selected worker before identity sampling/signalling and through replay/account finalization. It neither starts, stops, restarts nor changes unit policy. All original nonzero terminal metadata/group/guard/cleanup checks remain required.

The observer flushes its measured partial record and an exclusive root0600 ready marker, stays alive with its connection under the original job deadline, then accepts only the fixed root0600 release marker. The wrapper owns/joins that exact child before its successful join marker; its EXIT trap requests release and joins on failure too. Missing reference, marker, release or joined0 fails the control. The original outer root utility deadline still contains a stuck helper. No C# product/image or timeout change; no native result is claimed by source preparation.

## Replay input schema

Run38821117205 now retains the genuine killed owner, original workerSIGTERM15, two empty group scans and consumed guard. Its replay helper rejected before writing worker-before.txt. The helper still expected obsolete issue779-n15-observer-partial-v1 while the pinned parser and actual partial record use issue779-n15-native-observer-partial-v1. Only that literal is corrected; every jq field/identity predicate remains. Fixed stage labels now identify future failures without raw properties/paths/errors. No native acceptance is claimed from this correction.

### Pinned owner recipe rendering

Systemdv255 systemctl-show.c1288-1299 prints a killed ExecStart status as9/KILL, so the exact regex now matches it. Conditions is a compound a(sbbsi) tuple not rendered by systemctl show; bus-print-properties.c380 skips unprintable compounds. The checker requests other owner properties with --all (retaining required empty fields) and reads the actual Conditions tuple via a bounded fixed busctl get-property. It requires exactly one ConditionPathExists with trigger=false, negate=false, exact original armed path and result1. This is strict policy verification, not permission from pretty-printed text.

### Empty environment-file tuple

The same v255 systemctl-show EnvironmentFiles printer loops array entries but emits no line for an empty a(sb), even with --all. The owner checker therefore verifies EnvironmentFiles directly via bounded fixed busctl on the exact owner Service interface and requires a(sb)0. A missing pretty-printed line is never assumed to prove absence; any actual file tuple rejects. Other owner properties, identity/deadline/exit/guard/replay checks remain strict.
