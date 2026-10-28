# Completed replay worker

`completed_replay_worker.py` provides a fixed, local handoff entry point for replaying a previously prepared completed run. It accepts exactly four absolute paths: a canonical preparation JSON file, a canonical source-capture JSON file, a private output parent, and a result JSON path. The preparation and result files must be siblings in a caller-owned directory with mode `0700`; the source capture may be in that directory or another caller-owned private directory. The separate output parent must already exist, be empty, and have mode `0700`. Those directories must not overlap the validator, staged archives, completed source, or native source roots.

The preparation and source-capture inputs are bounded by the wire and capture limits in `completed_wire.py` and `completed_c1.py`. Each input must be a regular, single-link file owned by the current UID with mode `0600`. The worker opens inputs without following symlinks, holds their file and parent descriptors through replay, and checks their identities, bytes, and hashes again before publishing the result. The result is created exclusively with mode `0600`, flushed, read back through its held descriptor, and checked against the held inputs before return. If a later custody check fails, the worker removes only the result inode it created; caller-owned replacements are preserved.

The worker accepts only this process environment:

```text
PATH=/usr/bin:/bin
HOME=/nonexistent
LANG=C.UTF-8
LC_ALL=C
```

It also requires Linux, one non-root real/effective/saved UID and GID, only the current GID as a supplementary group, zero effective/permitted/inheritable/ambient capabilities, `NoNewPrivs=1`, and close-on-exec descriptors above standard input/output/error. Launch it with a credential-free environment, for example by clearing the environment before invoking the fixed script with four paths. It rejects unexpected environment entries rather than silently deleting credentials.

For example, after the caller has created and checked the four paths and established its outer isolation boundary:

```sh
env -i PATH=/usr/bin:/bin HOME=/nonexistent LANG=C.UTF-8 LC_ALL=C \
  /usr/bin/python3 -I -S -B /trusted/checkout/.github/issue779-protected-qualification/completed_replay_worker.py \
  /private/handoff/preparation.json /private/handoff/source-capture.json \
  /private/output /private/handoff/replay.json
```

The example only clears the environment and selects the fixed local module. It does not create the outer isolation boundary described below.

On success, the result is the bounded canonical `issue779-completed-replay-v1` wire value from `completed_wire.py`. It contains the fixed eight phase observations in order and literal false authority, acceptance, qualification, cleanup, and gate-eligibility claims. The worker invokes the fixed `completed_orchestration.replay_completed_run` API; callers cannot supply callbacks, executables, URLs, provider tokens, or phase selections.

## Decision and limits

Use this worker only after a trusted preparation step has resolved the provider run and staged the exact original archives. The worker revalidates the closed job set, preparation/source/capture bindings, archive inventory, and root identities before invoking the fixed replay chain.

Replay invokes source-owned validators and native/build procedures that process target artifacts. The worker does **not** create an operating-system sandbox. Its UID, environment, capability, and descriptor checks are process facts only. A caller must provide and verify an actual isolated execution boundary, such as a dedicated disposable job with no provider credentials, no unrelated same-UID processes or secrets, and bounded filesystem/process resources. In particular, a same-UID worker cannot protect a credentialed parent process from target code. The result does not attest that such an outer boundary existed.

The worker is not a provider resolver, support registration, Trusted admission path, qualification decision, original CI acceptance, or gate authorization. Its output is comparison data with fixed false claims. Do not convert successful process exit or a decoded result into any of those decisions. The protected validation workflow is not created or enabled by this module.

## Pitfalls

- Keep preparation/result files in the same private handoff directory; the result path must not exist already.
- Keep the private output parent separate and empty. Successful replay retains native and gate diagnostic trees there for the caller to review or dispose of.
- A failed replay may leave retained diagnostic trees if failure occurs after those trees are created. The caller owns their cleanup under its own custody policy.
- Preserve the four-path command contract. Extra flags or relative paths are rejected.
- Do not launch the worker with inherited tokens, runtime credentials, loader overrides, Git overrides, or unrelated descriptors. The worker fails closed if the environment/process checks see them.
- Keep the source-capture and preparation inputs immutable until the worker returns. It holds and rechecks their descriptors, but those checks do not replace an outer execution boundary.
