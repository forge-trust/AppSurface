# Source-pinned cancellation native preparation

This private fixture exercises the same [C# supervisor and worker executable](../../docs/designs/issue-779-csharp-supervision-core.md).
It does not add a production application, producer, proof or admission entry.

## Ordering and ownership

The private compiled N08 image pauses the original CliFx caller before allocation;
N09 pauses it after the original allocation handle is retained. The original C#
root owns the tracked checkpoint and sends SIGINT only through its retained
original worker pidfd after authenticated READY and a charged phase observation.
The fixture never selects a supplied process ID for delivery.

The original C# owner joins worker start/monitor, server and output pumps before
emitting its fixed failed result and bounded original stream bytes. The OS reader
then checks the selected generated layout, absence of both original processes,
fresh empty/absent worker group, and original NSS account names and IDs absent after account closure.
Worker FD closure is kernel closure on observed process exit; a successful managed
Dispose call or successful custody transfer is not inferred.

## Fixed data APIs and bounds

`checkpoint-n09-retained-slot.sh` runs the fixed N09 fixture and writes the bounded
root-owned `n09-observation.json` only after source-owned cleanup, account absence,
worker-group settlement and post-settlement path comparison. The runner retains that
file under its fixed 4096-byte cap and verifies the fixture result digest against the
actual retained bytes.

[`check_n09_records.py`](check_n09_records.py) is the installed detached seven-line record parser. It checks
consistency of the source-owned records; the runner separately checks the observation
member, source/base/generation bindings and original process/pump terminal facts.
Neither parser nor JSON metadata creates native authority or acceptance. The failed
root result and original deadlines remain prerequisites; malformed data cannot upgrade
the failed run.

Root retention permits only fixed file names, per-file bounds, root600 single-link
sources, canonical sorted USTAR headers and the existing 32MiB aggregate ceiling.
Raw streams remain in the private archive and are never echoed to public logs.

## Current state and pitfalls

The exact current-source static build, runtime, audit, transport, fixture, parser, cancellation and cleanup scopes are reviewed. The ten matching workflow gates and internal execution gates are promoted in this fresh packet. Independent review of this promotion and refreshed pin graph is still required before publication. No Linux execution, passing build receipt, native acceptance or case credit is asserted.
Local detached parser tests prove parser behavior only. A correct SIGINT syscall,
JSON join flag, unit-test pass or archive digest alone does not establish Linux
acceptance. N08/N09 remain pending until the exact deployed images execute and all
original signal, terminal, EOF, path, group and quarantine observations are verified.

The captured application source remains exactly 2865 hashes and modes in this common-source-v4 N09 candidate. Its exact harness installation adds 19 paths: the original 15 preparation members plus the 4 runtime-pinned members consumed by the dedicated N09 runner. The builder rejects any unlisted installation delta; the derived whole-tree membership is 2884 files. The unused N03 helper bundle is excluded from this N09 image. N03 remains a separate required all16 procedure on the final common source; this packet provides no N03 credit. Accounts and paths are preserved on failure, and cleanup consumes the original fixture allowance.


### Source-map digest fields

The pinned common-v4 capture has distinct digest roles. Capture validation uses `SOURCE_MAP` `a3dbb4a7be588b3e5234bf728e4eda4032f5ee8acab1d2ab7213cceef106586e`, derived from sorted compact JSON `{source_sha256: source.sha256, source_modes: source.modes}` with no trailing newline. The runner declares `SOURCE_MAP` `af4bee87511df5cc07f1505cb84e8607f4ca10dea74bf5830e23128622bd0dfd` for the full per-path `{sha256, mode}` projection. This is a recorded digest constant; it is not emitted by `source_check()` or consumed by the runner's build-fact validator. The FDD builder's `source_check()` emits build-phase `source_map_sha256` from the compact capture `source` object; `BUILD_SOURCE_MAP` `07e4be7c1c59989a70d88c345ff360e68a0a394a6729877afa7981d6d913d238` is that product digest for all 2,865 captured SHA-256 and mode rows. The acquisition and root-transport validators compare source-capture predicates to the first digest and build-receipt facts to the product digest. The runtime's `RUNNER_SHA` must match the refreshed runner bytes; all 11 active runner-pinned members are installed at the workspace paths the N09 runner consumes. Native acceptance remains false. All N09 static review gates are now promoted from their exact scoped receipts; this permits only the fixed test attempt after independent delta review and does not prove its runtime result. The inherited N03 helper validators and helper installation bundle have been removed from this dedicated N09 path; the separate N03 all16 procedure remains required and pending independent review and its separately required all16 validation.


### Retained-record consistency correction

The archive verifier compares the summary allocation and cleanup SHA-256 fields
with the exact first and sixth newline-terminated records in retained root stderr.
Its results GID must equal the parsed source-owned cleanup record. A well-shaped
summary cannot substitute different cleanup facts; malformed or mismatched data
keeps the original failed result and supplies no native authority or acceptance.
Four detached controls exercise one consistent pair and three single-field
mismatches. Linux execution remains pending.
