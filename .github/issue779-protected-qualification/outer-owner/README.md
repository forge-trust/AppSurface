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
The latter requires a zero-link retained inode with matching device/inode/mount
identity and `ENOENT` for the original name; a replacement or ambiguous error
rejects. [Kernel cgroup deletion rules](https://docs.kernel.org/admin-guide/cgroup-v2.html#organizing-processes-and-threads)
permit removal only after all live processes and child groups are gone. The
launcher PID handle, command client and streams must still join independently. Process exit, unit-client success, or a JSON cleanup flag alone cannot
advance the sequence. The existing inner deadlines, coverage thresholds,
comparison base and admission table are unchanged.

## CI controls and interpretation

The [Linux isolation workflow](../../workflows/evidencehost-completed-isolation-controls.yml)
builds this owner and runs fixed probes for writable build/result roots, immutable
validator/archive/capture/preparation files, inaccessible credentials and host
processes, fixed SDK, session-changing descendants observed live before bounded teardown, ignored stop, output overflow,
real byte/inode exhaustion, and source drift before closeout. The SDK version probe puts first-use state in `/tmp` through its fixed child environment; the replay worker itself retains its exact four-variable environment. The same owner and
phase-launch path are used for probes and completed-run execution. Synthetic
canaries are never provider tokens. Source captures and bounded original logs are
retained with the result. The workflow makes a fresh root-owned, no-follow source-capture copy before invocation and the owner keeps its strict current-UID input check. Empty Python standard-library package markers are retained as regular data files; executable inputs remain nonempty. Setup failures expose only a finite checkpoint name and an allowlisted category; they never expose exception messages, provider text or token bytes.

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

Synthetic controls also record fixed kernel UID/GID, capability, no-new-privileges
and seccomp fields immediately before `setpriv`, plus a finite security-label
classification. A fixed root-owned shell then execs the original command with its
original arguments. This distinguishes a missing bootstrap capability from a host
filter rejection; it does not change the UID transition or accept a failed probe.
Prepare, replay and close do not add this diagnostic prefix. No environment,
credential, arbitrary security-label text or caller-selected command is printed.

The fixed bootstrap explicitly carries only `CAP_SETUID` in systemd's
[ambient capability set](https://www.freedesktop.org/software/systemd/man/256/systemd.exec.html#AmbientCapabilities=)
so the intended UID transition survives the `unshare` exec. The existing five-item
bounding set is unchanged. The original `setpriv` command clears bounding,
inheritable and ambient capabilities before execing the nonroot launcher; both
the launcher and the independent root readiness check retain their actual
credential, zero-capability and no-new-privileges requirements. Ambient retention
is bootstrap setup only and never grants a target capability or admission.

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
