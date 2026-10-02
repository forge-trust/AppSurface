# Evidence subject cleanup supervisor

[`scripts/evidence-gate-subject-supervisor.py`](https://github.com/forge-trust/AppSurface/blob/main/scripts/evidence-gate-subject-supervisor.py) is a private, non-claiming cleanup helper for the rootless Podman subject launcher. It owns one per-run container identity and one quota-limited host tmpfs mountpoint. It does not evaluate subject output, establish the execution envelope, or authorize an Evidence claim.

## Launcher contract

The launcher must create a new scratch directory directly under `RUNNER_TEMP`, then create the mountpoint as its direct child. Both directories must be owned by the runner user and have mode `0700`. The mountpoint name must be `quota-limited-scratch-` followed by 16 lowercase hexadecimal characters. The container name must be `ase-subject-<run-id>-<attempt>-<12 lowercase hexadecimal characters>`, using the positive decimal run and attempt IDs.

Start the supervisor with a fixed argv and `shell=False`, as a direct child of the launcher:

```text
python3 scripts/evidence-gate-subject-supervisor.py start \
  --container-name <fixed-container-name> \
  --scratch-directory <absolute-RUNNER_TEMP-child> \
  --mountpoint <absolute-scratch-child> \
  --parent-pid <launcher-pid> \
  --parent-start-time <Linux-proc-start-time>
```

Provide only `RUNNER_TEMP`, `HOME`, and `XDG_RUNTIME_DIR` plus a fixed `PATH` to the helper. The `start` operation validates the host directories and the launcher's Linux `/proc` start identity, creates a mode-`0700` per-run state directory and mode-`0600` manifest, then starts a new-session supervisor process. It waits for a bounded ready handshake before returning. Its single JSON response includes `schemaVersion`, `claimEligible: false`, `ownerToken`, `ownerLabel`, `manifestPath`, `stateDirectory`, `supervisorPid`, `supervisorStartTime`, and `supervisorReady: true`. The launcher checks the returned paths and process identity before container setup. The state directory and manifest are outside the mounted tmpfs.

The launcher must add this exact label to the container at creation time, including when container creation can fail after making the object:

```text
--label=<ownerLabel>=<ownerToken>
```

The supervisor only removes the exact container name when Podman inventory and inspect identify that one container, its owner label equals the returned token, and its `/scratch` bind source equals the manifest mountpoint. It inspects the container ID and removes that immutable ID, so a same-name replacement cannot be removed in the inspect/remove gap.

After the launcher has finished consuming the result record and no longer needs the mount, request normal cleanup:

```text
python3 scripts/evidence-gate-subject-supervisor.py request \
  --manifest <manifestPath> --owner-token <ownerToken>
```

The supervisor also starts cleanup when the launcher process exits or its PID is reused. The launcher should wait for `cleanup-attestation.json` in the returned state directory for no more than 600 seconds. Treat any missing, malformed, or incomplete record as cleanup failure. The supervisor removes the container first. It unmounts only when container removal is verified and the live mount at the exact target is a tmpfs with the fixed 4 GiB, 262,144-inode, owner, and mode settings. After unmount, it removes only the identity-checked mountpoint directory and the exact bounded `evidence-subject-result.json` export, then removes the empty scratch directory with `rmdir`. It never recursively removes files; unexpected entries or a result file with the wrong type, owner, mode, link count, or size leave scratch in place and make the attestation incomplete. A normal cleanup request must therefore come after the launcher has consumed the result.

## Attestation

The JSON record is limited to 4 KiB and has schema version 1. It always includes `claimEligible: false` and `published: false`. `status` is `complete` only when `containerStatus` is `removed` or `absent`, `mountStatus` is `unmounted` or `absent`, and `scratchStatus` is `removed` or `absent`. Other statuses fail closed; in particular, an unverified container preserves the mount so a possibly running process is not detached from its scratch filesystem. Diagnostics are stable codes, not raw Podman, sudo, or subject output.

The [subject workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/evidence-gate.yml) runs the trusted [cleanup attestation checker](https://github.com/forge-trust/AppSurface/blob/main/scripts/evidence-gate-cleanup-attestation-check.py) after execution, including a launcher failure. The checker accepts exactly one runner-owned supervisor state directory, requires a private complete record, and copies at most 4 KiB to an attempt-scoped artifact. The runner's `RUNNER_TEMP` need only be owned by the runner; the supervisor state itself must be mode `0700`. A missing, ambiguous, unsafe, or incomplete record fails the subject job. The artifact remains non-claiming and is not yet bound into the trusted verifier.

## Bounds and limitations

The parent watcher has a fixed 30-minute lease, enough for the launcher's existing 20-minute maximum subject duration and setup overhead. If the parent remains verifiably alive at lease expiry, the helper does not clean up beneath it; it records an incomplete attestation with cleanup marked not attempted. If the parent identity cannot be verified, the helper also leaves resources in place and records an incomplete attestation. Once cleanup starts, it has a fixed 10-minute deadline; each trusted command has a 30-second timeout and combined output is capped at 256 KiB. The helper only supports Linux `/proc`, the fixed rootless Podman executable, and noninteractive `/usr/bin/sudo umount`.

Starting a new session lets the supervisor survive a launcher `SIGKILL`; it cannot survive runner VM destruction or an external mechanism that kills the whole job cgroup. It is not a substitute for a post-job runner cleanup hook. The forced-parent-kill test uses injected proc, Podman, and mount behavior and needs no privileged Podman or real mount. A live rootless Podman integration pilot is still required before relying on the cleanup contract in a workflow.
