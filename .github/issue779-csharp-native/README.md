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

The runner pins and bootstraps the reviewed `.github/issue779-csharp-build/capture-receipt.json` input before privileged acquisition. Acquisition rechecks that same reviewed-root file and copies it to the protected `control/source-capture.json` path consumed by later stages; there is no `source-capture.json` file under the build output directory. The root bootstrap allows this fixed receipt up to 2 MiB; other bootstrap inputs retain the 128 KiB cap.

## Current state and pitfalls

The exact current-source static build, runtime, audit, transport, fixture, parser, cancellation and cleanup scopes are reviewed. The capture-path, root transport, and native handoff review gates are closed pending independent review of this correction and refreshed pins. No Linux execution, passing build receipt, native acceptance or case credit is asserted.
Local detached parser tests prove parser behavior only. A correct SIGINT syscall,
JSON join flag, unit-test pass or archive digest alone does not establish Linux
acceptance. N08/N09 remain pending until the exact deployed images execute and all
original signal, terminal, EOF, path, group and quarantine observations are verified.

The captured application source remains exactly 2867 hashes and modes in this common-source-v4 N09 candidate. Its exact harness installation adds 19 paths: the original 15 preparation members plus the 4 runtime-pinned members consumed by the dedicated N09 runner. The builder rejects any unlisted installation delta; the derived whole-tree membership is 2886 files. The unused N03 helper bundle is excluded from this N09 image. N03 remains a separate required all16 procedure on the final common source; this packet provides no N03 credit. Accounts and paths are preserved on failure, and cleanup consumes the original fixture allowance.


### Source-map digest fields

The v14 capture uses distinct digest roles: capture validation SOURCE_MAP caa0fb7bb7cb2e6628c85477c7864e8e9b063a1164e29fe6aeb7ac6d5ed54e26 is compact sorted JSON of source_sha256/source_modes; the runner SOURCE_MAP 38cb7e74689138ec3ba7b458001b7623e12b611abbf956605fd1863809ce193d is the sorted per-path sha256/mode projection; builder BUILD_SOURCE_MAP c1f0e63fe3e9a0f16ef41519e51f64970b786fddaed7b3420411a76bb8483811 is compact JSON of the source object used in build receipts; CAPTURE_PROJECTION_SHA b26fa59da0a4e32d54f9ac9205b1163300514112f9e54ca2cecfb6e39d7f7f4c uses the builder’s five-key validation/parent/count/tree/source projection. These values are separately recomputed from the exact installed capture. The runtime RUNNER_SHA, builder runner/prerequisite pins, runner PINS, and workflow file pins are refreshed from their actual final bytes. All v14 static review gates remain false pending fresh review; native execution and credit remain false.


### Retained-record consistency correction

The archive verifier compares the summary allocation and cleanup SHA-256 fields
with the exact first and sixth newline-terminated records in retained root stderr.
Its results GID must equal the parsed source-owned cleanup record. A well-shaped
summary cannot substitute different cleanup facts; malformed or mismatched data
keeps the original failed result and supplies no native authority or acceptance.
Four detached controls exercise one consistent pair and three single-field
mismatches. Linux execution remains pending.

## Retained build-count correction

Run 38112724904 built the normal CLI successfully: all 43 builder commands returned zero, with 2,865 retained source files and no compiler diagnostics. The old final workflow predicate still expected 2,849 files and rejected before native N09 execution. This packet corrects that exact count and its fixed success marker. All other build integrity, command settlement, inventory hash, identity and deadline checks are retained. The actual workflow verifier is exercised locally against the digest-verified retained build receipt and handoff inventories; those checks are fixture regressions, not a native N09 result. Independent delta review and a fresh native run remain required.
### N09 v9 bootstrap basename guard

The retained N09 run failed during root bootstrap at the source-shape check, before the N09 fixture or native case ran. The fixed parser filenames include underscores, while the runner's embedded bootstrap allow-pattern did not. The v9 candidate permits underscore for this fixed basename check and explicitly rejects empty, dot, dot-dot, slash/traversal, newline, and NUL-at-process-boundary cases. The pinned 11-name set remains fixed. This is a bootstrap validation correction only; it carries no native result or acceptance credit. The prior v8 candidate and failed run receipt remain immutable history. This v14 branch proposal remains unpublished pending fresh independent review.


## OS-audit helper argument correction

The exact N09 run 38123607945 rejected the metadata audit with the closed
`OS_AUDIT_REJECTED:arguments` result before native worker dispatch. The runner
omitted the audit API's required `--helper-root` argument. The corrected call
passes only the generated `empty-audit-helper` directory already created by
[`prepare-root-inputs-v2.sh`](prepare-root-inputs-v2.sh), root-owned and sealed
0555, separate from the runtime and tool trees. No caller helper path is accepted.
The audit retains its existing deadline, overlap, owner, link, mode, hash and
before/after checks. The directory is empty because this N09 procedure has no
helper payload; it grants no admission or result authority.

The publisher validates the actual runner call shape against the audit's required
argument declarations before private Git operations. Data-only regressions execute
that parser and call expression, showing the failed call rejects and the corrected
call parses with the selected directory. Parser success is not native execution:
Linux custody and the N09 case remain required on this exact image.

### v14 corrected-source retarget

This isolated packet binds corrected capture fde517820a8bc0af61ebd625337b6522d5c328986017364ac2d443841b97dbb5 at HEAD 0adef6032217b4181898a4d6d129e1248cb0eda3 with direct parent 7e1d87d502dab7df654299e4773a41c037bd1efd, 2867 captured source files, and whole-image count 2886 after the unchanged 19-path installation. The prior v13 review receipts are historical and do not clear changed v14 bytes. No native execution, acceptance, or credit is claimed.

## Terminal custody verifier correction (v18)

The v17 independent review found that the post-settlement verifier compared the
same worker-era record as both its initial and terminal state. Those states have
different owners, so that call could never establish root custody. The verifier
now validates worker-era records first, then compares them with independently
sampled metadata from the actual retained directory descriptors. It still
requires root:root 0700 terminal directories, matching device/inode/type, an
empty allocated slot and the original descriptor-byte binding.

Four focused controls invoke the complete verifier using real temporary directory
descriptors and an explicitly projected ownership seam. They cover separate
worker/terminal samples, wrong worker metadata, wrong terminal ownership or
substituted inode, and an occupied slot with unchanged sentinel bytes. These
portable controls establish no Linux root custody, admission, or native credit.
All 19 refreshed inputs require independent review before publication.

## Retained log versus JSON parsing

The archive verifier decodes only `fixture-result.json`, `n09-observation.json`,
and `retention-selection.json` as JSON objects. The root log `logs/n01.stderr`
stays as bounded bytes and is checked by `check_n09_records.py` as exactly seven
newline-terminated records. Treating that entire log as one JSON object rejects
valid retained evidence. Archive bounds, canonical metadata, generation and digest
bindings, and the root record parser remain required. The three new archive
projection controls are data tests; they establish no native acceptance. Run
38143138936 is preserved as a failed outer verifier attempt.
