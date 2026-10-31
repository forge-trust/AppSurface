# Private completed-run isolation owner

This C# executable supplies the operating-system owner missing from the fixed
[prepare/close commands](../completed-entrypoint-README.md) and
[credential-free replay worker](../completed-replay-worker-README.md). It is built
separately for Linux CI. It is not a product command, capability registration,
Trusted execution mode, qualification record, or coverage policy.

## Commands and defaults

```text
controls REPOSITORY SOURCE_CAPTURE EVIDENCE_ROOT
completed REPOSITORY VALIDATOR_CAPTURE SOURCE_CAPTURE EVENT EVIDENCE_ROOT
```

`controls` exercises a closed set of real host probes with synthetic credential
canaries. `completed` selects only the existing fixed prepare, replay, and close
commands. Paths must be absolute and canonical; the evidence destination must be
fresh. The completed command additionally requires the real protected `workflow_run`
context checked by the existing entrypoints. Feature-branch controls never invent
that context. Missing protected provenance remains a rejection.

The root coordinator creates a unique dedicated non-root identity before creating
or pinning any phase input. A private ext4 backing filesystem limits storage to
8 GiB and 131,072 inodes; the phase cannot reach its root-only host parent. The
namespace contains only the fixed runtime/tool closure, immutable validator and
inputs, the two disposable build trees, handoff, output, and private temporary
storage. The workflow installs exactly SDK 10.0.401 into a fresh fixed `/var/lib/appsurface-completed-dotnet` tree and transfers that tree to root ownership before use; this prevents a newer preinstalled SDK from winning version selection. `/var/lib` is checked as root-owned and not writable by group or other; the selected SDK and every ancestor must also be root-owned directories with no group/other write bits. The runner image makes `/opt` group/other writable, so the pinned SDK stays under `/var/lib`; the namespace still binds the selected tree read-only at `/usr/share/dotnet`. SDK discovery must select exactly 10.0.401. Fixed runtime ELF dependencies and the Ubuntu 24.04 ICU 74/OpenSSL 3 libraries are copied with verified duplicate bytes; the host filesystem is never mounted wholesale. Unsupported Linux, process,
namespace, mount, cgroup, ownership, or quota facts reject before target release.

The internal `phase prepare|replay|close|control` command is a fixed launcher inside
an owner-created transient unit. It is not an arbitrary-command interface. It
waits for the root owner's bounded stdin release after actual process and mount
verification. Token material travels only in the private pipe to trusted prepare
and close launchers; it is never a command argument, unit environment property,
retained file, report, or log. Replay receives exactly PATH, HOME, LANG, and
LC_ALL, with no token or provider context.

The private managed launcher additionally requires the fixed
[`DOTNET_EnableDiagnostics=0`](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling#enable-diagnostics)
setting before the .NET runtime starts. Forced termination can leave the runtime's
diagnostic socket and debugger FIFOs in its persistent private `/tmp`; disabling
diagnostics prevents those runtime endpoints from being created. Disabling only
diagnostic IPC leaves debugger FIFOs. This setting applies to the owner launcher
in every phase, including controls. The fixed Python command independently clears
its environment, so the replay worker still receives exactly the four variables
above. Diagnostic tooling is unavailable for the private launcher; target execution,
coverage collection, custody checks and deadlines keep their existing contracts.
`BootstrapCommand.AppendLauncherEnvironment` constructs this fixed launcher prefix;
`PhaseLauncher.BuildCommand` constructs the separate fixed Python command. Both
are internal command-construction seams exercised by portable policy controls.
The custody scan still rejects every special node, including runtime sockets and
FIFOs; `filesystem-special-node` is only an allowlisted, path-free failure category.
The setting is a mitigation candidate supported by a Linux forced-termination
mechanism probe; only fresh source-bound Linux CI can confirm the control outcome.

## Phase ordering and lifetime

1. Verify all source captures and fixed tool bytes, reserve the identity, and
   create the roots. Preparation uses the original 1,320-second maximum.
2. After preparation's entire cgroup and streams settle, pin the original staged
   archives and preparation file. Replay sees those same inodes read-only.
3. Run the fixed replay worker inside the actual isolated unit, with no provider
   credentials and at most the original 1,200 seconds.
4. Join all descendants, including children that fork or change session. Recheck
   root identity, immutable validator/inputs/archives and both source captures
   before starting credentialed closeout. Closeout retains its 180-second bound.
5. Publish only the existing comparison observation and finite owner diagnostics.
   Ambiguous settlement, custody drift, failed kill/join, output overflow, or
   timeout prevents the next credentialed phase and quarantines the workspace.

Every phase deadline starts before dispatch and includes startup, stop, independent
KILL, cgroup observation and stream/process join. No grace interval is added after
expiry. The owner requires two consecutive observations of the retained original
cgroup as empty, or kernel removal of that exact retained node after launcher exit.
The original removed-node route requires a zero-link retained inode with matching
device/inode/mount identity and `ENOENT` for the original name; a replacement or
ambiguous error rejects. [Kernel cgroup deletion rules](https://docs.kernel.org/admin-guide/cgroup-v2.html#organizing-processes-and-threads)
permit removal only after all live processes and child groups are gone. The
launcher PID handle, command client and streams must still join independently. Process exit, unit-client success, or a JSON cleanup flag alone cannot
advance the sequence. The existing inner deadlines, coverage thresholds,
comparison base and admission table are unchanged.

### Kernel-removed dentry validation candidate

The additional fail-closed route handles a removed kernfs dentry whose held
inode still reports a positive link count. It was added as a validation
candidate after the source-bound revision-141 host diagnostic observed the same
held cgroup identity with positive links, launcher exit, and `ENOENT` at the
original name after the command client and stream pumps had joined. That
observation motivated the route; it did not itself establish settlement or
native acceptance.

The route requires the same pinned directory inode (device, inode, mount ID and
type), the exact generated host cgroup path recorded at capture, the exact
proc-fd target for that path plus the kernel ` (deleted)` suffix, `ENOENT` for
the original name, launcher-pidfd exit, and trusted procfs and cgroup mount
views. The two proof observations are sequential reads, not an atomic snapshot;
each independently rechecks the held identity, pathname state, launcher exit,
descriptor target and mount evidence, and both must complete within the original
phase deadline. A missing name, positive link count, suffix, or one successful
observation alone is insufficient. The original zero-link route remains
supported and unchanged.

Linux v6.8's [`d_path()` implementation](https://github.com/torvalds/linux/blob/v6.8/fs/d_path.c)
appends ` (deleted)` when the held dentry is unlinked; the
[`proc/<pid>/fd` implementation](https://github.com/torvalds/linux/blob/v6.8/fs/proc/fd.c)
exposes the held descriptor's path. The v6.8
[cgroup v2 contract](https://docs.kernel.org/6.8/admin-guide/cgroup-v2.html)
requires a cgroup to have no children or live processes before removal. A local
Linux ARM64 mechanism probe exercised `IsCgroupEmpty` for a
live cgroup, a linked empty cgroup, a removed positive-link dentry, a replacement
at the original name, and an invalid proc mount. These mechanism and policy
checks exercise the decision paths, but are not the protected Linux CI
qualification run. This locally retained probe does not establish exact-source
behavior on the protected runner. The route still requires exact-source native
CI evidence showing its behavior on the runner's kernel and systemd; until then,
report it as a validation candidate rather than a native pass. The implementation checks
the original path first and reads the proc-fd target and mount IDs only when the
path is missing and the held inode still reports positive links.

The decision seam `DecideRemovedCgroupSettlement` takes the captured
`CgroupIdentity`, original generated host path, capture-time proc-fd path, and
two `CgroupRemovalObservation` values. Both observations independently require
the same held device/inode/mount/type, confirmed `STATX_MNT_ID` result, positive
links, missing original name, exited launcher, exact deleted marker, trusted
procfs, and visible original cgroup2 mount. Both the held descriptor identity
and original named path are re-read for the second observation; no path error,
replaced node, or missing mount-ID mask is treated as absence. The runtime
adapter `TryReadTrustedDescriptorPath` verifies
`/proc/self/fd` is on the same captured procfs mount ID and matches an exact
visible `proc` mount entry before reading only the retained fd's link. The
capture path is recorded from that same fd at witness creation.
`HasVisibleCgroup2Mount` requires the statx-visible mount ID to match an exact
`/sys/fs/cgroup` `cgroup2` entry in the root process's mountinfo. Diagnostics
expose only finite match labels; paths remain private proof inputs.

When a phase cannot settle, the owner retains a finite settlement observation
from the same launcher, cgroup, event and process reads already used by the
decision. The removed-dentry route also reads the retained descriptor path and
mount IDs as proof inputs; they are not diagnostic-only observations. It reports
only closed labels for launcher exit, held-node identity,
link count, original-path state, event population, process presence and a
fixed failure category. It never reports PIDs, cgroup paths, raw proc/cgroup
contents or exception messages. Diagnostic collection performs no extra
settlement probe, and its result never participates in `IsCgroupEmpty` or
credential sequencing. `unknown` means that the corresponding observation was
not reached or could not be parsed; the labels describe observations, not a
cause for failure or proof that the phase settled. The removed-dentry route
records only finite descriptor/procfs/mount match labels and never outputs the
descriptor target path. Synthetic control reports may include only the finite
proof labels needed to distinguish the named control's result; credentialed
phases do not emit proof labels.

## CI controls and interpretation

The [Linux isolation workflow](../../workflows/evidencehost-completed-isolation-controls.yml)
builds this owner and runs fixed probes for writable build/result roots, immutable
validator/archive/capture/preparation files, inaccessible credentials and host
processes, fixed SDK, session-changing descendants observed live before bounded teardown, ignored stop, output overflow,
real byte/inode exhaustion, and source drift before closeout. The SDK version probe puts first-use state in `/tmp` through its fixed child environment; the replay worker itself retains its exact four-variable environment. The same owner and
phase-launch path are used for probes and completed-run execution. Synthetic
canaries are never provider tokens. Source captures and bounded original logs are
retained with the result. The workflow makes a fresh root-owned, no-follow source-capture copy before invocation and the owner keeps its strict current-UID input check. Empty Python standard-library package markers are retained as regular data files; executable inputs remain nonempty. Setup failures expose only a finite checkpoint name and an allowlisted category; they never expose exception messages, provider text or token bytes.

The internal [`ReadOnlyRenameProbe.RequireDenied(source, destination)`](ReadOnlyRenameProbe.cs)
immutable-replacement control uses Linux `renameat2` with
`RENAME_NOREPLACE`. `File.Move` is unsuitable here: on a read-only bind mount it
may attempt a copy followed by source deletion, leaving a copied sibling even
when deletion fails. The probe first requires the destination name to be absent
without following symlinks, then accepts only `EACCES`, `EROFS`, or `EBUSY` from
the atomic rename. It rechecks the source inode, metadata, and bounded contents,
and confirms the destination is still absent. An unexpected errno, successful
rename, or residual destination fails the control and preserves the observed
state for diagnosis; it never deletes a sibling to make the check pass. The
portable policy controls test the errno allowlist, while only the Linux workflow
can exercise the actual syscall against a read-only bind mount. Neither result
establishes product admission or native qualification.
The three fixed source files use the existing bounded custody reader with a
16 MiB ceiling, which accommodates the preparation input; source captures keep
their separate original 1 MiB limit. The helper cannot issue arbitrary commands
or change mount permissions. The existing full custody check still runs after
each settled control and rejects any unexpected handoff or stage node.

For synthetic controls, [`OwnerCoordinator.ControlStep`](OwnerCoordinator.cs)
records a finite operation checkpoint around phase execution, the host canary,
and custody verification. Top-level failure output reports this checkpoint with
an allowlisted category. The setup checkpoint can describe an earlier successful
SDK validation and must not be interpreted as the failing operation by itself.
These diagnostics expose no paths, child-authored text, or credential material.

The `credential-denial` control keeps its original environment, host-canary,
manager, home-directory and private-proc checks in the same order. A failed check
may expose only its existing fixed category (`token-environment`,
`host-credential-hidden`, `host-credential-readable`, `manager-hidden`,
`root-home-hidden`, `runner-home-hidden`, `private-proc`, `private-init`, or
`token-process`) through [`Program.SafeFailureCode`](Program.cs). Unknown or
decorated strings remain redacted. A category identifies the failed assertion;
it does not by itself establish that credential bytes were accessible. A minimal
Linux container passing this body also does not establish that the full systemd
and root-directory setup passes it on the CI host.

A successful controls report proves only its named host controls at the captured
source. It does not establish a genuine protected predecessor, successful full
completed-run replay, original gate success, support registration, native
acceptance, or product admission. Those require the existing protected-main
[qualification workflow](../../workflows/evidencehost-protected-qualification.yml),
original artifacts, fixed validators and unchanged numerical gate. No finalizer is
enabled merely by adding this tool.

For a failed synthetic control, already bounded stdout/stderr are retained only
after both capture pumps join and while the original deadline remains. This keeps
systemd and namespace startup errors available for diagnosis. These control phases
receive no provider token; the diagnostics never prove settlement or grant authority.
The parent's native validation failures use an exact allowlist of fixed `unit-*`,
proc, mount, statx and cgroup categories. Unknown categories, category suffixes and
ordinary exception text remain redacted. These labels identify the rejected check;
the last setup-stage label alone does not identify the phase failure's cause.

Synthetic controls also record fixed kernel UID/GID, capability, no-new-privileges
and seccomp fields immediately before `setpriv`, plus a finite security-label
classification. The fixed root-owned shell then performs the private runtime
setup below and execs the original command with its original arguments. This
distinguishes a missing bootstrap capability from a host filter rejection; it does not change the UID transition or accept a failed probe.
Prepare, replay and close perform the same runtime setup without diagnostic
output. No environment, credential, arbitrary security-label text or
caller-selected command is printed.

Every phase's fixed bootstrap overmounts `/run` with an empty, read-only `tmpfs`
(`nosuid,nodev,noexec`, 4 KiB, 16 inodes) after systemd has created the unit namespace
and before the original `setpriv` transition. Systemd namespace setup can add
[runtime mount-propagation plumbing](https://github.com/systemd/systemd-stable/blob/v255-stable/src/core/exec-invoke.c)
after the owner prepared its otherwise minimal root. The retained failure only
identifies the combined manager-path predicate; it does not identify which path
was visible or the runner's systemd version. The overmount hides any such `/run`
setup state from the nonroot phase; the existing
`manager-hidden` control still requires both manager paths to be absent.
The existing [`unshare --mount-proc`](https://man7.org/linux/man-pages/man1/unshare.1.html)
creates the private mount namespace before the shell runs, so the host runtime
directory and underlying owned root are not changed. The fixed `mount --no-mtab`
command writes no mount-table file. A failed mount exits 125 before `setpriv` or any
phase launcher runs. This uses the existing bootstrap capabilities, preserves the
four-entry target environment, and does not extend any phase or cleanup budget.
The empty mount provides no phase scratch space; scratch remains at `/tmp`.

The fixed bootstrap explicitly carries only `CAP_SETUID` in systemd's
[ambient capability set](https://www.freedesktop.org/software/systemd/man/256/systemd.exec.html#AmbientCapabilities=)
so the intended UID transition survives the `unshare` exec. The existing five-item
bounding set is unchanged. The original `setpriv` command clears bounding,
inheritable and ambient capabilities before execing the nonroot launcher; both
the launcher and the independent root readiness check retain their actual
credential, zero-capability and no-new-privileges requirements. Ambient retention
is bootstrap setup only and never grants a target capability or admission.

Readiness also binds `/proc` to the mount ID returned by `statx` for
`/proc/LAUNCHER_PID/root/proc`, requiring the `STATX_MNT_ID` result bit and the
same ID at `/proc` with filesystem type `proc` in that launcher's mountinfo.
[`unshare --mount-proc`](https://man7.org/linux/man-pages/man1/unshare.1.html)
can stack a new proc mount over an existing one; the kernel reports both in
[`mountinfo`](https://man7.org/linux/man-pages/man5/proc_pid_mountinfo.5.html).
An entry for a hidden proc mount cannot vouch for a different visible mount.
Missing or duplicate IDs, unsupported mount identity, and a visible non-proc
filesystem remain rejections. This check occurs before any phase release; it
neither changes namespace requirements nor accepts ambiguous containment.

Both disposable source trees retain explicit read-only bind mounts for their
`.git` directories, with sources under the owner filesystem and destinations
inside the unit root. The SDK uses the same fixed `BindReadOnlyPaths` property;
the source/build parents remain writable. This follows systemd's
[host-source and unit-root destination semantics](https://www.freedesktop.org/software/systemd/man/255/systemd.exec.html#BindPaths=).
Before replay or a control is released, the owner obtains each Git directory's
visible kernel mount ID and requires that exact mount at the exact Git path to
be read-only. A hidden read-only entry, writable visible entry, read-only ancestor,
missing ID or different path rejects. The existing negative write and replacement
controls and post-phase Git/content custody checks remain required.
Replay, control and close phases also bind the already enrolled preparation file
read-only and verify its exact visible mount ID. Its handoff parent remains
writable during replay so the worker can publish its sibling result. Preparation
itself has no such bind: the trusted prepare command must first create that file.
This preserves the enrolled file's inode and the existing custody checks across
phases while rejecting hidden read-only layers beneath a writable visible mount.

The phase's existing read/write path allowlist is passed with systemd's `+` prefix,
so it applies inside `RootDirectory` rather than naming host paths. Each required
writable directory also gets an explicit writable bind from its original owner
filesystem node to the same fixed destination: source, native source, output,
handoff and temporary storage in replay/controls, plus staged archives in prepare;
close has only report and temporary storage writable. No new writable destination
is introduced. Readiness requires the exact visible mount ID at each allowed path
to carry `rw`; a writable ancestor or hidden writable layer cannot pass. The
nested Git and preparation read-only binds and checks still apply independently.
See systemd's [unit-relative access paths](https://www.freedesktop.org/software/systemd/man/255/systemd.exec.html#ReadWritePaths=)
and [bind mount semantics](https://www.freedesktop.org/software/systemd/man/255/systemd.exec.html#BindPaths=).

## Source checkout trust

The root setup reads the runner-owned checkout through Git's local transport with
local-copy optimizations disabled. A fresh root-owned `0600` configuration inside
the inaccessible owner root trusts only the canonical source directory and its
`.git` directory. Git's upload-pack subprocess reads that file directly; client
command-line overrides alone do not survive its configuration boundary. System
and user Git configuration, wildcard trust, hooks, credentials and target-selected
commands remain excluded. The file is setup data and is never exposed to a phase.

Source captures record filesystem permission modes (`0644` or `0755`). Git's index
uses regular-file entry modes (`100644` or `100755`) for the same files. Checkout
verification maps only those two accepted Git modes to the captured permissions;
blob hashes, stage number, complete tracked-path set and on-disk ownership/mode
checks are validated independently. Symlink, submodule, unsupported mode and
nonzero-stage entries remain rejected.

## Pitfalls

- Keep all phases on the same underlying filesystem nodes and dedicated identity.
  Artifact transport or ownership changes between phases invalidate preparation.
- The preparation file is read-only above a writable handoff directory so replay
  can publish its sibling. Do not make the preparation itself writable.
- The two source trees intentionally allow build outputs. Their tracked bytes and
  Git state are still rechecked independently before credentials return.
- Preserve quarantined or substituted nodes. Cleanup never treats a familiar path
  as proof that the owner still owns the node.
- A caller cancellation cannot prove that a transient start was rejected. The
  owner retains pending ownership and attempts independent teardown.
- The controls intentionally mutate a disposable source in their final case;
  that workspace is quarantined rather than silently restored and called clean.

All JSON authority, qualification, native-acceptance, gate-eligibility and coverage
credit fields remain false. The ordinary product admission boundary stays closed.
