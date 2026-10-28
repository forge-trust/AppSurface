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
storage. SDK discovery must select exactly 10.0.401. Fixed runtime ELF dependencies and the Ubuntu 24.04 ICU 74/OpenSSL 3 libraries are copied with verified duplicate bytes; the host filesystem is never mounted wholesale. Unsupported Linux, process,
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
expiry. Process exit, unit-client success, or a JSON cleanup flag alone cannot
advance the sequence. The existing inner deadlines, coverage thresholds,
comparison base and admission table are unchanged.

## CI controls and interpretation

The [Linux isolation workflow](../../workflows/evidencehost-completed-isolation-controls.yml)
builds this owner and runs fixed probes for writable build/result roots, immutable
validator/archive/capture/preparation files, inaccessible credentials and host
processes, fixed SDK, session-changing descendants observed live before bounded teardown, ignored stop, output overflow,
real byte/inode exhaustion, and source drift before closeout. The same owner and
phase-launch path are used for probes and completed-run execution. Synthetic
canaries are never provider tokens. Source captures and bounded original logs are
retained with the result. The workflow makes a fresh root-owned, no-follow source-capture copy before invocation and the owner keeps its strict current-UID input check. Setup failures expose only a finite checkpoint name; they never expose exception messages, provider text or token bytes.

A successful controls report proves only its named host controls at the captured
source. It does not establish a genuine protected predecessor, successful full
completed-run replay, original gate success, support registration, native
acceptance, or product admission. Those require the existing protected-main
[qualification workflow](../../workflows/evidencehost-protected-qualification.yml),
original artifacts, fixed validators and unchanged numerical gate. No finalizer is
enabled merely by adding this tool.

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
