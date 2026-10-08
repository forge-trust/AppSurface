# C# supervisor pipe correction: Linux validation candidate

Status: source implementation and independent composition review complete; Linux preparation/publication/execution pending. This harness validates the [C# migration checkpoint](../../docs/plans/issue-779-csharp-supervision-migration.md). It creates no acceptance proof or production registration.

## Revision and local result

Source `d5c8fe20dbef4903c3d438a31e2c092f8cd17eac` is the direct child of `87d94e1a487a2283862ab87930ed1341ec25200a`, tree `04e978bb45ae84a823bf64200f4be349190ab898`. The [capture](../issue779-csharp-build/capture-receipt.json) binds all 2,821 source hashes and modes. The normal harness merge `76e5053d56ef65b3b539ab6aa0090a0355749ab5` has ordered parents `8ea0caa0c5bead60e573e31e7a60c08d3e3a2362` and this source. The installation is its sole child on `codex/issue779-csharp-linux-native-20261008-retry-16`.

The three product changes correct raw Unix pipe-handle wrapping, add a real anonymous-pipe regression through the same internal wrapper, and document stream ownership. Platform, root UID/GID, FIFO, access-mode and CLOEXEC guards remain. [Source review](source-review.json) binds the actual local result: four commands exit0, core1,029/1,029 and pipe class17/17 passed, no skipped/failed cases or compiler diagnostics, 9.869653 seconds. The actual local receipt is `406e046ab439a3dc5ac43ef7e5699ed3a977eef6f7d6a0f062543dd0b2873ae4`; independent product review is `a128c9c659b39706cf6b5385deb4337f1719ed742aa9cfdf7b797740be3137d0`. Historical source records remain identified in that JSON and are not additive current totals.

## Linux library regression and genuine worker

[The builder](../issue779-csharp-build/build-fdd.sh) executes one named test, `OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs`, as a nonroot Linux user using the selected SDK host. Locked restore and source test share the original builder deadline and owned-process cleanup. A bounded256KiB TRX must contain exactly one valid test identifier, the expected method and class, one passed/executed case, and no failure, skip, timeout or abort. The two additional commands make the exact build sequence41 commands; both [acquisition](acquire-inbound.sh) and [runner](run-native.py) require that sequence and the closed library-result record. This test supplies no root-factory, systemd or admission evidence.

Then [N01/N02](checkpoint-n01-n02-v5.sh) run unchanged. N01 requires the genuine root supervisor, same executable nonroot worker, authenticated peer/cgroup, retained slot and ordered physical joins. N02 rejects an unprivileged supervisor. All sixteen required native cases remain pending until actually measured; N03 is excluded from this correction. A passing library test cannot complete the migration checkpoint.

## Bindings and enforcement

Thirteen installed inputs and28 exact hash edges bind [runtime preparation](prepare-ubuntu-runtime.py), [root transport](prepare-root-inputs-v2.sh), [OS audit](prepare-os-audit-v2.py), [retention](retain-native.sh) and workflow. SDK10.0.401, deployment runtime10.0.12, caps, path grants, unit properties, original600-second fixture and30-second cleanup reserve are unchanged. The library regression, its closed result validators and revision metadata are the only harness behavior changes. No refreshed deadline or enlarged policy is introduced.

The preparation driver requires a SHA-bound independent-review receipt; publication verifies the complete source/index/tree, uses an ordinary push and rejects unexpected remote heads. This candidate has not run on Linux yet. The production catalogue/proof registry remains closed and old failed attempts remain historical evidence.
