# Linux allocation probe

`linux-allocation-probe.c` is a dependency-free, provisional Linux fixture for
the output-allocation and writer portion of the EvidenceHost consumer proof. It
is scoped to the fresh protected-parent/run-root/file binding described in the
[EvidenceHost trust-boundary design](../../docs/designs/issue-779-evidencehost-trust-boundary.md#supervisor-api-and-output-root-binding)
and the allocation cases in the
[consumer test plan](../../docs/plans/issue-779-evidencehost-test-plan.md#critical-paths).
It does not implement or exercise worker supervision, subject isolation, CI
provenance, a verifier, a manifest, or production EvidenceHost APIs.

## Build and run

Run on Linux with a C compiler and Linux headers that define the `openat2`
system call and `struct open_how`:

```sh
cc -std=c11 -O2 -Wall -Wextra -Werror \
  tests/evidencehost-consumer/linux-allocation-probe.c \
  -o /tmp/linux-allocation-probe
/tmp/linux-allocation-probe
```

The program creates a private temporary test tree under `/tmp`, runs every
scenario, writes exactly one JSON object to stdout, and removes the tree.
Diagnostics and per-scenario progress go to stderr. The JSON schema is
`issue779-linux-allocation-proof-v1`; its top-level `admission` is always
`none`. It records the user-confirmed first consumer as AppSurface GitHub
Actions and CI owner as Andrew, while marking consumer acceptance `unverified`.
Each scenario records its fixed `expected` status, whether it was
`observed`, its `actual` status, and `passed`. The report also states that the
content method is bounded byte equality and that no cryptographic hash was
computed. This object is directly saved as `allocation.json` by the combined
fixture runner; its artifact index hashes the saved report file.

Exit `0` requires every required scenario to have been observed and to match
its exact expected result. Exit `1` means a scenario or the harness failed;
the emitted JSON reports `status: failed` and marks unexecuted cases as not
observed. Exit `77` explicitly means the runtime does not provide or permit the
required `openat2` resolution flags; it emits `status: unsupported` and is
never a passing test. Other feature-probe or setup errors emit a failed report
and exit nonzero. Do not treat the report as EvidenceHost admission or as a
manifest.

The mutation cases deliberately change inode ownership. Run as root or with a
Linux capability set that permits `fchown`; if ownership mutation is denied,
the affected scenario reports a harness failure and the fixture exits `1`.
This avoids silently counting an unexecuted owner-change case as a pass.

## Allocation and writer behavior

The probe opens `/` and resolves the absolute temporary parent relative to that
retained descriptor with `openat2` and `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS`.
It records the parent's device/inode, owner, and exact `0700` mode. It then
creates the run root with `mkdirat(parent_fd, ...)`, opens it beneath the
retained parent with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_XDEV`,
and checks the named entry and opened handle identify the same owned `0700`
directory. The file is created with `O_CREAT | O_EXCL | O_NOFOLLOW` through the
retained root descriptor and the same no-follow/beneath/no-cross-device
resolution constraints. Its named entry and retained descriptor must identify
the same owned regular file with link count one and exact `0600` mode.

After writing a deterministic 64 KiB payload through the retained file
descriptor, the probe reads it back in 4 KiB chunks from that same descriptor,
with a 64 KiB maximum, and compares each chunk byte-for-byte. It checks the
bounded file size before reading, checks for an extra byte, and compares the
file identity and size afterward. This is a bounded content-equality proof; it
does not calculate or claim a cryptographic hash or a manifest hash.

Immediately before the fixture treats the slot as publishable, it re-resolves
and compares the parent path, run-root name, and file name against their
retained descriptor identities and checks owner, mode, type, and file link
count again. The actual file contents are written and read through the same
retained file handle. This is a local allocation/writer probe, not a complete
race-proof output collector or filesystem sandbox.

## Cases and expected outcomes

| Scenario | Required result |
| --- | --- |
| Fresh root and neighboring allowed file | `OK`; neighboring file retains its inode and bytes |
| Existing run-root directory | `ROOT_COLLISION` |
| Existing slot file | `FILE_COLLISION` |
| Symlink as parent | `PARENT_SYMLINK` |
| Symlink as final run root | `ROOT_SYMLINK` |
| Symlink as slot file | `FILE_SYMLINK` |
| Extra hardlink to the slot | `FILE_HARDLINK` |
| Parent replaced after its descriptor is retained | `PARENT_IDENTITY_CHANGED` |
| Parent owner or mode changes | `PARENT_OWNER_CHANGED` / `PARENT_MODE_CHANGED` |
| Run-root owner or mode changes | `ROOT_OWNER_CHANGED` / `ROOT_MODE_CHANGED` |
| Slot owner or mode changes | `FILE_OWNER_CHANGED` / `FILE_MODE_CHANGED` |

Each row is an executable scenario with a fixed expected enum, and the harness
fails if the actual enum differs. The three owner/mode pairs cover all
allocation levels. The positive control proves the declared neighboring file
remains readable and byte-identical while the run root is freshly allocated.

## Constraints and sharp edges

- This fixture requires a Linux kernel/runtime that supports `openat2` with
  `RESOLVE_BENEATH`, `RESOLVE_NO_SYMLINKS`, and `RESOLVE_NO_XDEV`. There is no
  fallback to path-based allocation. A blocked syscall, including one denied
  by a container seccomp policy, is reported as unsupported with exit `77`.
- The test parent and run root use exact owner and permission checks. The
  fixture runs in a temporary directory it creates itself; it does not accept
  caller-selected output paths or establish CI ownership of a real artifact
  parent.
- Ownership mutation needs privileges. A failed `fchown` is a test failure,
  not an expected rejection result.
- The parent-substitution case renames the bound parent directory and creates
  a different directory at its old name. It demonstrates identity rechecking
  around this controlled race; it does not establish every possible concurrent
  namespace attack against an arbitrary filesystem.
- The neighboring-file control is scoped to the parent. It is not a general
  allowlist, and the probe does not authorize any CI path or consumer.
- Passing output is provisional local fixture evidence only. It does not name
  an accepted provider/platform, establish AppSurface GitHub Actions behavior,
  prove the selected worker/UID supervisor, or satisfy the mandatory real
  consumer proof and acceptance record in S5/S6. The consumer/owner fields
  record the confirmed mapping only; acceptance remains unverified. No release
  acceptance is implied.
