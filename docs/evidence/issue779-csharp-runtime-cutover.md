# EvidenceHost C# runtime cutover

## Fixed public replay controllers — 2026-10-27

The [revision-116 runtime](https://github.com/forge-trust/AppSurface/actions/runs/40536624574)
rejected one gate-controller substitution test because Linux could immediately reuse the inode
after its unlink/recreate sequence. The follow-up strengthens the controllers rather than
changing the rejection: they keep no-follow close-on-exec read descriptors for the copied
Cobertura input and all 24 native data leaves across execution and readback. The old inodes
remain allocated until cleanup completes, making same-content replacement detectable. Descriptor
closure runs on success and failure. Detached nonroot Linux verification under umask `0077`
passes all 164 qualification tests with five explicit fixture skips after that correction.

The [native-data replay controller](../../.github/issue779-protected-qualification/completed-native-replay-README.md)
and [public gate replay controller](../../.github/issue779-protected-qualification/completed-gate-replay-README.md)
now verify an exact clean disposable source checkout, select fixed retained inputs, build with SDK
10.0.401 in locked mode and invoke only the existing public test or coverage command. They reject
pre-existing ignored build outputs, preserve bounded logs/reports, recheck source and input bytes,
and leave generated build/cache state under caller-owned isolated workspace disposal. Their fixed
false authority fields and `cleanup_complete: false` do not certify a completed protected run.
The numerical evaluator, fixed base, thresholds, tolerance and Codecov mode are unchanged.

Detached Linux validation passed 163 qualification-data tests with five explicit original-fixture
opt-in skips. These include real harmless process-group deadline/output/closed-pipe-child controls
and synthetic stage/build/test orchestration boundaries. The actual SDK and public-method
feasibility runs below remain separate: no complete protected 37-artifact run has been replayed,
and run 109 still cannot pass the gate controller's required-original-report preflight.
Fixed orchestration of every completed-run role, original successful test/merge binding,
protected source/support anchoring and actual Trusted/downstream acceptance remain required.

The [revision-115 N05 job](https://github.com/forge-trust/AppSurface/actions/runs/40533927699/job/122293911966)
passed the genuine original procedure without skips after the current content pins and fixture
repairs. Its original provider archive `12748371126` has SHA-256
`2339b01d710338a419cd8f3c3986e7517d39711c4d58a0a07c50b1a2d37f04f3` and binds source
`5857a96419f51b4f1ba64257f9646b1c76025760` and the complete 4,013-file capture. The initial/final
ordinary OS passes took 73/79 seconds and the unchanged launch guard allowed the remaining native
procedure to run; its total phase elapsed was 375.7135 seconds with exit zero. This source-bound
negative control remains an Observation with native acceptance false. The full chain and numerical
gate are still running and this individual result does not establish their success.

## Completed validator preparation and N05 timing repair — 2026-10-27

The [revision-114 runtime validation](https://github.com/forge-trust/AppSurface/actions/runs/40532277987)
rejected preparation before native dispatch because the three changed procedure files still had
their preceding `ported_sha256` inventory pins. The follow-up updates only those current content
pins, retaining all historical file/provenance fields, and tests the production inventory loader
against every current port. The same run caught two portable input-fixture assumptions: a `0755`
directory inherited the runner's private umask, and unlink/recreate could reuse an inode on Linux.
The corrected fixtures set the intended mode explicitly and keep the original inode alive by
renaming it before substitution. The complete qualification suite passes under umask `0077`.
These corrections retain the original rejection checks. Revision 114's separate
[focused custody validation](https://github.com/forge-trust/AppSurface/actions/runs/40532277530)
passed all 29 filesystem/report controls and four peer-credential controls without skips; its
provider-bound artifacts and complete 4,012-file source capture match `d013674f`.

The [fixed native input adapter](../../.github/issue779-protected-qualification/completed-native-inputs-README.md)
now selects the public C# measurement validator's exact 24 input leaves from the four original
consumer/account/producer archives. It checks the full typed stage/inventory, provider-bound ZIP
bytes, role retention, declared expansion before allocation, source capture equality, private
inode custody and content readback. Its [result contract](../../.github/issue779-protected-qualification/completed-native-result-README.md)
accepts only the one public method's passing TRX and supplies a fixed VSTest argument tuple.
Neither API executes a completed-run controller or grants authority.

A separate feasibility run used the four authentic revision-109 role archives with their provider
sizes and SHA-256 values, verified the complete exact source before and after, and built the public
test using SDK 10.0.401 with zero warnings/errors and native opt-in absent. Direct filtered VSTest,
with native opt-in only for that process and no collector/runsettings, returned exactly one pass
and zero skips. The TRX SHA-256 is
`3cb75724f423d2987bf0e1b69a663f125cfb82e5275faf0b64130fff3c7bf1ae`.
This used a four-role mechanism fixture, not the full protected 37-artifact resolver; failed
candidate run 109 cannot satisfy that resolver's protected-main and completed-success requirements.

The [gate data preflight and comparison](../../.github/issue779-protected-qualification/completed-gate-README.md)
binds the exact original coverage archive and five fixed retained members, rejects a missing gate
JSON before Cobertura extraction, and compares report fields with exact decimal values. It retains
the fixed Git base, thresholds, tolerance and Codecov mode and performs no coverage arithmetic.
The isolated public-CLI execution controller and proof of original successful test/merge/gate
completion still remain required. Run 109 has no original gate JSON and remains rejected.

The [revision-113 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40524812444)
failed N05's existing prelaunch time guard: retained stage observations show 82 seconds for the
initial ordinary OS pass, 93 for ELF closure, 12 for sealed trees and 93 for the final OS pass.
The native launch checkpoint had 245 seconds remaining and requires more than 246. The preparation
removes the redundant 32-path limit from N05/N06/N12's existing `stat` snapshots while retaining
their 16,384-byte argument bound, every path, full before/after metadata and hash checks, alias
verification, scratch custody and original deadline/cleanup reserves. Detached controls require
every path exactly once, larger short-path batches and bounded splitting of long paths. A fresh
source-bound Linux run must establish whether this reduces cost enough; the guard is unchanged.

Local verification passed all 305 checkpoint-one portable controls and 138 qualification-data
controls with five existing explicit fixture opt-in skips. The gate parser selection covers all
its executable lines under the standard-library trace; this is diagnostic line coverage, not
branch coverage or the solution/patch gate. The new suites are included in the existing bounded
qualification-data workflow step. Review also closes post-creation directory-open cleanup and
pins native input readback to the original opened inode and stage attribution. No admission,
production catalogue, numerical gate command, threshold, exclusion or native clock changes.

## Revision 109 validation and revision 110 repair — 2026-10-27

The [revision-109 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40483696166)
used source `64e25cb406243a648edee31a9cf76935034759a7` and the 3,989-file capture
SHA-256 `a4764325c21c8934e4b9ec69975b32b8edb47308f442122801a80884e684488a`.
All fourteen worker selectors and reconciliation, C3/application and separate SDK lifecycle,
account creation and rollback, packaged C2 and all fourteen ordered measurement jobs passed.
Its only failed job was the unchanged solution/Codecov gate: two stream-read failure fixtures
threw `DirectoryNotFoundException` while deleting a destination parent that had not yet been
created. The resulting `ASCOV120` is a test failure, not a numerical threshold result. There
is no passing current solution/patch gate and no newly measured patch deficit from that run.

The incoming writer deliberately buffers bounded stream input before persistence. Revision 110
(`18e5f128d752a26c3764eaab94c1a7b67824f06b`) corrects only those fixtures and their
[stream API reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md): they
assert absent output during the read, create a caller-owned file or directory, throw the original
read exception, then require the identical exception, unchanged caller path, open caller stream,
empty artifact metadata and a successful fresh retry. The focused selection changed from eight
passes/two failures to ten passes/no skips. The full CLI suite subsequently passed 4,262 tests
with five platform skips under ordinary local permissions. An earlier restricted run's eight
environment failures are retained separately; no timeouts, skips or product behavior were changed.

The [revision-109 portable procedure](https://github.com/forge-trust/AppSurface/actions/runs/40484008132)
passed all six OS/mode jobs. Provider size/SHA-256 checks on all original archives confirmed passed
procedures and cleanup, with protected admission, native acceptance and gate eligibility false.
The [separate revision-109 custody run](https://github.com/forge-trust/AppSurface/actions/runs/40483696101)
also passed its 29 filesystem/report controls and four separate actual-peer credential controls
without skips. These are source-bound observations, not protected acceptance.

The [revision-110 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40517643465)
is bound to source `18e5f128d752a26c3764eaab94c1a7b67824f06b` and capture SHA-256
`abbe9285f6feec5b4a5e203e1c88335c490bfe103f1540f6f7d79e66539cac32` (3,989 files).
Its complete consumer/measurement/gate chain is still running. The separate
[focused custody run](https://github.com/forge-trust/AppSurface/actions/runs/40517643367)
has retained provider-digest-matching archives with all 29 filesystem/report and four peer-credential
TRX rows passed and unskipped. The fixed comparison base
`1991280e8af049db592ebcb705cbf66f4b4c6e8b`, thresholds, exclusions, clocks, quotas,
retention limits and closed production admission remain unchanged.

The next non-authorizing completed-run steps are the
[C2 receipt replay](../../.github/issue779-protected-qualification/completed-c2-README.md)
and [fourteen-segment measurement replay](../../.github/issue779-protected-qualification/completed-measurement-chain-README.md).
The C2 mechanism ran its original captured callbacks against run 109, checked all seven cases,
and rejected coherently rebound EOF loss. The measurement mechanism ran all fourteen original
segments, returned the original N16 report and all sixteen case rows, and rejected a changed
retained predecessor. These isolated replays use original artifacts from a failed candidate run;
they cannot satisfy the completed successful protected-run resolver. C3/account closeout adapters,
the full gate replay, fixed orchestration, provider/support anchoring and actual Trusted/downstream
acceptance remain separate required work. No projection grants admission or registers support.

The next [C3 application/lifecycle replay](../../.github/issue779-protected-qualification/completed-c3-README.md)
shares pure retained-data callbacks with native closeout. Its current adapter accepted the seven
original run-109 application cases and three lifecycle rows, and rejected coherently rebound EOF
loss in both streams. Those artifacts predate the new APIs, so this is adapter regression only.
The portable C3 selection passes 51 tests; the two-artifact wrapper tests exercise real bounded
ZIP staging and inode-owned cleanup with the final callback boundary isolated. A new source-bound
Linux run is required for these callbacks. The coverage artifact upload now preserves the existing
JUnit result XML alongside timings and logs, so later validators can inspect exact public data-test
identities; this adds retention only and changes no collection, execution or numerical policy.

The [account receipt replay](../../.github/issue779-protected-qualification/completed-accounts-README.md)
now invokes the captured original receipt, instrumentation, source-witness and predecessor
validators for creation and rollback. It checks the actual owner stdout/stderr binding, raw
capture equality, current/final receipt hashes and ZIP inventory projections before returning
non-authorizing data. Ten portable tests pass; an explicitly enabled synthetic wrapper fixture
also passes, with its predecessor handoff isolated and separate original-callback lineage tests.
This does not constitute an authentic completed protected-run replay. The original C#
Native.Tests data validators, [numerical replay](../../.github/issue779-protected-qualification/completed-gate-README.md),
fixed orchestration and reviewed protected support/acceptance still remain required.

## Local integration with current main — 2026-10-27

The prepared revision-104 files were preserved in local commit `6402d8edc64aca84523b94132afaa153749640c3`
before integrating GitHub main `ebb562be618c2649c1ef0ade12095e56468882c7`.
The integration retains the complete native measurement chain and the incoming
[coverage fan-in](../../guides/coverage-fan-in.md), readiness and package-validation APIs.
Both consuming coverage lanes use SDK 10.0.401, matching the EvidenceHost measurement
chain; the fixed comparison base, thresholds, exclusions and runtime limits remain unchanged.
The broker's fixed environment command now carries the fan-in directory and same-run
repository/run/attempt identity into the nonroot coverage process.

The incoming stream artifact overload uses the admitted writer's existing protected-root,
shared artifact-quota and owned-work boundaries. Restricted coverage registration accepts
only its existing Debug, serial, no-managed-results command with managed retention;
[ordinary coverage execution options](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md)
remain available to the ordinary producer. The integration needs its own source-bound Linux
and numerical gate results; previous receipts continue to describe their original source.

**Native-validation migration:** the [tool](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md)
and [workflow](../../.github/workflows/native-evidence-validation.yml) now explicitly select ordinary
`--operation procedure` execution rather than calling the legacy host. The local runner joins
the original procedures and cleanup, verifies retained artifact bytes, and writes
`procedure-plan.json` plus `native-validation-procedure.json`, without an Evidence manifest.
Admission, native acceptance, and gate eligibility remain fixed false. Missing/unknown operations
fail with `ASEVD401`, and `--operation protected` fails with `ASEVD404` before input/output access.
The three-OS workflow has separate storage/consumer entries under its existing 25-minute job cap;
the [procedure design](../designs/native-evidence-validation.md) records its checks and unchanged policy.
Protected execution remains open and requires genuine admission and supported consumer evidence;
using a test supervisor or relabeling Observation would not satisfy that requirement.
The incoming HTTP readiness host unit fixtures now use the existing internal admission test
seam; their simulated results provide no native acceptance. None of this local merge enables Trusted mode, accepts a platform proof or publishes
a completed acceptance record.

The local migration selection passed 393 tests with zero failures or skips on SDK 10.0.402,
including actual packed-storage and built-in/external installed-consumer procedure checks.
A focused collector measured 100% lines and branches in the tool's `Program.cs` and
`NativeValidationProcedure.cs`. That collector is diagnostic only, with a tool-specific include;
it is not a solution/patch gate result or protected native acceptance. The admission implementation,
production catalogue, protected gate, full coverage script and protected workflows are unchanged
by this procedure migration. The merged source still needs its complete actual Linux chain and
unchanged numerical gate before current coverage gaps can be recalculated.

The exact-source [revision-105 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40470302017)
completed unsuccessfully: all fourteen worker selector jobs and reconciliation passed, while the protected-parent
semantic fixture and C3 retention fixture failed before the downstream consumer/measurement/gate chain.
The former omitted the newly serialized public `CoverageArtifactRetention: "managed"` default; the latter
still expected rejection at 135 leaves despite the new peer TRX occupying that existing slot. Their fixture
repairs retain the production gate, all native callbacks, and the 135-file/96 MiB validation bound.

The separate [revision-105 portable procedure run](https://github.com/forge-trust/AppSurface/actions/runs/40470449150)
finished with five passing jobs and a Windows storage failure. All six builds and contract-test steps passed.
The failure report withheld every authority claim and showed failed cleanup; the transcript ended after
successful maintenance preview, before an apply result was recorded. The exact exception was masked by
cleanup. The follow-up tool diagnostic now retains runner failure code plus an allowlisted procedure/cleanup
category and numeric HRESULT, preserving both failures without raw exception text. It does not waive cleanup
or establish that the Windows cause has been fixed. Fresh exact-source results remain required.

The [revision-106 portable run](https://github.com/forge-trust/AppSurface/actions/runs/40473383309)
passed all six jobs on its first attempt. All six original procedure archives were checked against provider
size/digest metadata, plan/report bindings and artifact hashes, with all authority flags false. Windows storage
completed the apply command and cleanup; this does not identify or repair the earlier intermittent failure.
The [revision-106 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40473383872) passed all
fourteen worker selectors, reconciliation and repaired parent semantic controls, but failed the SDK negative
consumer before worker admission. Original coverage shows the application descriptor writer serializing the new
ordinary retention property into the strict eight-field v2 producer object, which the unchanged parser rejects.

The descriptor regression reproduces that mismatch before the correction. The writer now explicitly projects
the existing eight fields and rejects unsupported retention or explicit execution settings before projection;
the parser, admission checks and gate remain closed and unchanged. Local descriptor controls pass 35/35,
and the full supervision suite passes 3,105 with 91 Linux/root-only skips. The separate exact-source
[focused Linux file-custody run](https://github.com/forge-trust/AppSurface/actions/runs/40473383920)
retained 29 passing filesystem/report tests and a separate four passing actual peer-credential tests.
These do not replace the blocked consumer/measurement chain or numerical gate.

Revision 101's existing observer subsequently recorded [run 40445212390](https://github.com/forge-trust/AppSurface/actions/runs/40445212390)
terminal failure at 2026-10-27 07:02:45 UTC. All prerequisite jobs succeeded; the gate
stopped in the native-data validator with the known `Expected: 41 / Actual: 43` account-lineage
mismatch. That correction is included in revision 104. The failed run supplies no passing
numerical result and does not change the historical revision-91 coverage figures below.

### Revision 107 continuation and completed C1 mechanism

The [revision-107 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40476296231)
uses source `e1aed288b197a6ade0288f1372b49d3c574561db` and the 3,986-file capture
SHA-256 `a7c2f82a9f68fd6dddbcd2e6c0e9d74d81c64e69585cdb7a31e5407df5e19b03`.
All fourteen original selectors and reconciliation passed; the SDK negative consumer,
seven application/six workspace controls and separate SDK lifecycle controls also passed.
The application, account creation and rollback jobs completed successfully. The packaged producer
then failed at `sdk-pack-aspire`, so the subsequent measurement jobs and numerical gates were skipped.
Local reproduction found NU5118: Aspire included its README twice alongside the shared repository
pack target. Removing the duplicate project item preserves the README in the package. The prepared
ordinary package journey also packs the new Readiness dependency and requires it in the fresh
consumer dependency graph. Its 23 fixed logs and 29 curated leaves still fit the existing retention
bounds; a failed command now adds at most 32 KiB of its private log tail to the existing outer smoke log.
Fresh source-bound Linux execution remains required. These observations remain bound
to revision 107 even if later source preparations are committed.

The next [completed C1 callback mechanism](../../.github/issue779-protected-qualification/completed-c1-README.md)
consumes the staged run/inventory, checks exact selector and run/attempt identities, and invokes
the original retained-evidence callbacks from a complete verified checkout at its recorded path.
It compares the returned aggregate with the retained aggregate and requires invocation cleanup.
The mechanism does not register support, issue admission, validate the measurement/consumer chain,
or replace the unchanged numerical gate. The production catalogue and accepted-proof decision
remain closed.

<a id="latest-verified-consumer-checkpoint--2026-10-26"></a>

## Latest verified consumer artifacts — revision 101 — 2026-10-27

[Run 40445212390, attempt 1](https://github.com/forge-trust/AppSurface/actions/runs/40445212390)
binds these retained artifacts to source `3fb45d86151262152394405f7d8072c46ea9a1cf`
and the 3,624-file capture SHA-256 `13db6ee01c0e82d6a898140237c15dac29c8118767480315a401f624468a698b`.
Independent artifact validation checked the original archive digests and current source bindings;
the verification itself did not repeat native execution. The full run and its unchanged numerical
gate were still in progress when this checkpoint was recorded.

| Retained evidence | Verified result | Verification receipt SHA-256 |
| --- | --- | --- |
| Original C1 worker controls | All sixteen original callback rows, exactly once across fourteen selectors; fifteen original artifact digests and all source Git blobs/modes checked | `8539a43dd0b4fdd15c9d4ed6eeafa73f1451ab34712de70f1b29d183f07e469d` |
| C3 application and workspace matrix | Thirteen original records, 43 root-file tests, 44 supplementary tests and seventeen bundle/report-discovery tests; no failed checks | `223c5e32b944f9e8db48b1f80360f2ceea975e169b61d130a418f72d5b9ca683` |
| Separate SDK lifecycle cases | Configure stall, registered disposer stall and post-stop guards retained their expected failures, joined output, final empty groups and account absence | `e0aeed1d1f0a5d9b4844413dd049cb2278024894f20199f48dde0b7cb191860d` |
| Packaged C2 and account chain | Seven producer/artifact cases with both paired EOFs, 118 physical checks, 28 raw package-smoke leaves, and creation/rollback validation without failed checks | `e86c69faaf332133cce6ba1622df2989a58b4ba014a144abb2eacfa3e1752b28` |

The source-103 [focused Linux file-custody run](https://github.com/forge-trust/AppSurface/actions/runs/40452614282)
separately passed all 29 actual root bundle-audit, report-discovery and selected-results-root cases,
with zero skipped/failed rows. Its source is `d34330e9f0885f4ba7d0a683e986e0ddcc2bcc36`, capture
SHA-256 `a0a86aaddc5b2c824dd0b8614662a99b47d157e55bb8cab4958239133a5ea3eb`;
artifact `12711470047` is 268,640 bytes with SHA-256
`73f4a15e40786b0ee3310039eba63b2358694545faa2f1b2d8bfc5a12a0599c1`.
Those focused filesystem controls do not replace the complete consumer chain or numerical coverage
gate on that source.

The next prepared integration adds four separate actual Linux peer-credential controls and keeps their fixed four-row TRX apart from the existing 29 filesystem controls. Local supervision validation passed 3,098 tests, with 91 Linux/root-only cases skipped on macOS. Its protected support-record v3 parser/matcher passed all 55 focused data cases; it removes the impossible stored self-commit comparison while retaining the protected workflow content digest and separately resolved current commit. The [support-record reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/protected-support.md) explains that distinction. The [completed-run staging](../../.github/issue779-protected-qualification/completed-run-README.md) and [archive inventory](../../.github/issue779-protected-qualification/completed-validation-README.md) remain non-authorizing prerequisites; neither registers support or enables Trusted execution. The same preparation corrects the account-lineage data validator's obsolete 41-row expectation to the actual original 43-row root partition; the 43-row regression failed before that correction, and obsolete/different inventories remain rejected. The ordinary PR/main workflow also now requires and stages the complete existing measurement chain before enabling its native validator; its coverage command and threshold inputs remain unchanged. These prepared changes still require their own source-bound Linux run.

These candidate-branch results do not establish [protected qualification](../../.github/issue779-protected-qualification/README.md),
populate the production catalogue or accepted-proof registry, or enable Trusted execution. The latest
completed numerical gate below remains revision 91's failed result until a newer exact-source gate
finishes. Production limits, comparison base, coverage thresholds and exclusions are unchanged.

## Previous verified consumer checkpoint — revision 91 — 2026-10-26

[Run 40266921255, attempt 1](https://github.com/forge-trust/AppSurface/actions/runs/40266921255) binds the source-91 consumer evidence to `4b70e5dc228cdc3cea6c28bab2787668b9796c2e` and the 3,579-file capture SHA-256 `464aa36327a3efa2665538317ad1caff6f85dcdcd1418ed6981fa2c09ce34efb`. The terminal observation SHA-256 is `9abd83d04e5b6f36c08494ac351db5f37e53a44ac814a6113d610e18e638a2e4`; it records 12 bounded commands, all exit 0, joined and log-hash matched. C1 verified all 16 original callbacks across 14 selectors (receipt SHA-256 `1b9972cfac33ec976642e867ee2358099da93d0f2236255d36d3b8b9f8cf811f`), while keeping native-acceptance and gate-pass claims false. C3 records all 13 original native cases closed and empty, plus separate 41/41 root and 41/41 supplementary test results (verification SHA-256 `f977b520725cc4d3ea7738a0dd43caf598f3376a48ef9e84399f6bdaa8f8c14f`). Account and rollback verifications report 15,098 and 15,108 checks with no failures.

C2 verified seven producer/artifact cases and 118 checks, including paired stdout/stderr EOF observations. The original archive SHA-256 is `87f2ca3ec5b443d6856169539d8a649187f75771715d39b4aad877715aabcce7`. Its retained raw package-smoke detail receipt SHA-256 `bfccf490ceecb7a3659d806defae23808a58bf2bae47fc71c6d4dd868d9cdbec` validates 28 files totaling 32,490 bytes, including the 22 command logs; the C2 completion receipt SHA-256 is `4e00a9474f2381f3d97a4b7be0db46c172ff5fbd9d4b5b1df130c18fd6e15e0a`. These artifact checks do not establish Trusted acceptance or close the full acceptance plan. The revision-91 gate has since completed: aggregate coverage passed, while patch coverage failed ASCOV020, as detailed below.

Revision 91's full gate completed with aggregate coverage passing at 136,260/141,912 lines (96.0173%) and 62,439/71,169 branches (87.7334%). Patch coverage failed ASCOV020: 16,009/18,099 lines (88.4524%) and 24,873/29,988 branch outcomes (82.9432%), below effective thresholds 94.5%/84.5% by 1,095 lines and 467 conditions. This is bound to run 40266921255, attempt 1, source `4b70e5dc228cdc3cea6c28bab2787668b9796c2e`, capture SHA-256 `464aa36327a3efa2665538317ad1caff6f85dcdcd1418ed6981fa2c09ce34efb`, and unchanged comparison base `1991280e8af049db592ebcb705cbf66f4b4c6e8b`. The retained summary (`summary.json`, SHA-256 `7693502af833013b019e286de925c1363ed20fa5413a00b36dbec07bc3db8998`) binds artifact 12633545260 (2,824,174 bytes, SHA-256 `97fd2c460d405e4238352e828d2c9349fcb85ba4f21d4922f56f1712548c2f6d). The [gate job](https://github.com/forge-trust/AppSurface/actions/runs/40266921255/job/121433971413) records the result. This failed patch gate is not full acceptance.

### Prepared integration snapshot — `d7e5facf`

The pre-publication integration snapshot is `d7e5facf26528f78a0367469f222229c7d5bb3fe`; its 3,583-file source capture SHA-256 is `ae65ae4d39dbeb0945a2234dee95d2a3482732b3a4824d39da84b473ea36c089`. It was unpushed when recorded and has no Linux-native acceptance result. Local preparation records the original portable helper/runner suite at 41/41 and a separate generated-slot regression at 1/1 (receipt SHA-256 `2394d188270b73fe5c687f371471835caabf7969707ad456e9920b977747bfb7`); these were not run as one 42-case suite. The C# preparation reports 110 ordinary cases and four private CLI variants with zero diagnostics; the property selection reports three expected rejections. Two initial wrong-target attempts are excluded. Exact package `0.1.0-issue779-c4.d7e5facf2652` passed its local macOS installed-package journey in 53.94 seconds: 22 commands and 28 retained, source-verified raw leaves on the 3,583-file source (receipt SHA-256 `d87155631d5c3b9034b7981641730162012a2a50735175a8464d685d111143ac`). It exercised omitted/false/true legacy SDK overloads and schemas 0.9, 2.0, and 9999, with zero compiler diagnostics. This does not establish Linux package or Trusted acceptance.

For the remaining acceptance boundary, L07/N12 has the specified source-91 C1 descendant-work/paired-EOF evidence; L03/P03 post-stop launch/write guards and A04/L06/L08 configure/disposer additions still need Linux verification, and verifier/write/pump assertions remain open. U02 has the source-91 raw installed-package journey. U06's legacy overload and schema migration checks passed the local macOS package journey above; fresh Linux package validation remains pending. U07's fresh-user/operator manual trace remains pending; the source-91 receipt records `independent_human_usability: false`. Protected-parent A10/B04/G04/R06 implementation and supported-run acceptance proof remain incomplete; production Trusted registration and the accepted-proof registry stay closed. See the [acceptance map](issue779-acceptance-status.md), [plan](../plans/issue-779-evidencehost-test-plan.md), [runtime proof workflow](../../.github/workflows/evidencehost-runtime-proof.yml), [application lifecycle reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/application-lifecycle.md), [post-stop guard preparation note](../../.github/issue779-checkpoint3/post-stop-guards-source-preparation.md), [packaging reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/packaging.md), and [consumer acceptance record](issue779-consumer-acceptance.md).

## Previous verified consumer checkpoint — revision 90 — 2026-10-26

[Run 40255212264](https://github.com/forge-trust/AppSurface/actions/runs/40255212264), attempt 1 at source `0f2c91f777e476529506e5ce1236acf21ec78425`, has independently verified retained consumer artifacts for all 16 C1 worker controls, all 13 original C3 native cases, 41 root-class tests, 41 supplementary tests declared by the run binding, account creation and expected rollback, and seven packaged C2 producer/artifact cases. The complete source capture contains 3,577 files; its SHA-256 is `93ac64b60793aa3bdca5be9efb095e4b156ddb25396d918b89370ff2f1036cec`. All 11 bounded artifact-verification commands exited zero, joined, and matched their log hashes. The final C2 verification records 118 checks, including both original stdout/stderr EOFs for all seven cases. The retained local `terminal-observed.json` receipt is SHA-256 `46e884ad473d55ea36951fd92b2179509fea5416cfbb988f20be8dd00f7c6f2d`.

The 41 supplementary tests are a binding-declared count, not a claim of native acceptance. The 22 package-smoke rows match the reviewed command fingerprints, but the raw smoke logs and starter/plan/summary JSON are absent, so outputs were not independently revalidated. These consumer checks do not establish a passing full solution or patch coverage gate and do not close the 57-group acceptance map. The receipt expressly sets `full_gate_claim`, `consumer_receipt_gate_pass`, and `consumer_receipt_native_acceptance` to false. At the time this checkpoint was written, revision 90's full-gate analysis was still pending. That pending note is historical; the newer revision-91 numerical result above is bound to revision 91's own source. See the [coverage gate reference](issue779-coverage-gate.md) and [acceptance map](issue779-acceptance-status.md).

Revision 89's full run ended `ASCOV120` before numerical gating after two unrelated test failures: the PostgreSQL session-count check and the second-page form timeout. Revision 88's earlier `ASCOV120` is a separate event and also produced no numerical gate result.

The revision-57 CLI shutdown-test fix changes only the fixture clock: its Linux regression passed 578/578 focused CLI tests, while production deadlines and stopping allowances remained unchanged ([regression run](https://github.com/forge-trust/AppSurface/actions/runs/40104067585)). Production Trusted registration and accepted-proof tables remain intentionally closed; this checkpoint neither enables Trusted nor claims Windows/macOS or other unsupported-platform acceptance. Later source changes require fresh source-bound validation.

## Recorded Linux validation — 2026-10-24

[Linux run 40104067585](https://github.com/forge-trust/AppSurface/actions/runs/40104067585),
at source `b5461cfa3b73fa00aa8d8b15e1b724f5bdb0b9ad`, independently verified all
sixteen C1 worker controls, seven C3 application controls, six workspace controls,
39 original root controls, 41 supplementary root controls, account creation and
expected rollback, and seven packaged C2 producer/artifact controls. Both Linux
shutdown regression rows passed, the corrected doc-clock check passed, and the
packaged smoke completed 22 commands with six expected rejection results. The
source capture binds 3,498 files, bytes and modes; its SHA-256 is
`d72894c34ab245be825f7cfa4e971dd0ed0bf840173115e507ef956bf67ba465`.

The same run completed the full solution test set and reached the unchanged
[solution coverage gate](issue779-coverage-gate.md). Aggregate coverage passed at
95.9057% lines and 87.0851% branches against 94.5% and 84.5% thresholds. Patch
coverage failed `ASCOV020` at 86.9492% lines and 79.9151% branches; the current
denominator requires 1,337 additional fully covered lines and 1,394 conditions.
The coverage and [acceptance status](issue779-acceptance-status.md) retain the
original thresholds, comparison base and exclusions. Further source edits require their own source-bound Linux and coverage verification. These observations do not close all 57 groups,
populate the production Trusted catalogue or accepted-proof registry, or establish
protected Windows/macOS/native-platform acceptance.

The [supervision library](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md) is the sole runtime owner. The protected AppSurface executable runs `evidence supervise --request <protected-file>` as root and starts the same image as `evidence worker --control <protected-socket>` under its separate worker identity. The C# server owns authenticated protocol dispatch, process creation, application readiness, SDK/package inputs, reporter output accounting, retained artifact custody and strict cleanup. There is no Python runtime fallback.

<a id="current-correction-and-verification-status--2026-10-24"></a>

## Previous correction and verification status — 2026-10-24

The CLI shutdown defect reported from revision 56 was fixed in revision 57 by
using an explicitly controlled fixture clock and the existing fatal test seam.
The actual Linux regression rows subsequently passed together with 578 focused
CLI tests on source `b5461cfa3b73fa00aa8d8b15e1b724f5bdb0b9ad`.
No production deadline or stopping allowance changed.

[Revision 74, run 40124730961](https://github.com/forge-trust/AppSurface/actions/runs/40124730961)
at source `569cb730dc63035a2e0ce9f5e959169695505370` has verified all sixteen
worker cases, both original seven-control application and packaged-producer
consumer groups, six workspace rejections, 41 original root controls and 41
supplementary root controls, plus genuine account creation and expected rollback.
The packaged record binds 22 smoke/planning command intentions and current-run
source/report lineage. Its measured worker chain and final unchanged numerical
coverage gate were still running when this preparation was recorded.

The next source makes existing [settlement](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/worker-settlement.md),
[cleanup](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/account-cleanup-procedure.md),
[workspace state](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/producer-workspace-state.md),
[control state](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/control-server-state.md),
[DCP metadata](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/dcp-cgroup-metadata.md)
and [report custody](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/report-custody-metadata.md)
branches directly verifiable. The focused local collector observed all 131 lines
and 194 branches in the eleven extracted helpers, with 303/303 selected tests
passing. This bounded helper result does not establish solution or patch coverage,
physical Linux ownership, or accepted consumer proof. The [unchanged coverage
command](../../scripts/coverage-solution.sh), original comparison base, thresholds,
Codecov full-line rule, exclusions and production limits remain unchanged. Every
new source revision still requires its own genuine Linux controls and final gate.

## Deployment and startup

The [protected request](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#protected-launch-inputs) is bounded data, not authority. Before CLR startup the consumer must sanitize runtime hooks and environment, pin the actual runtime host and managed image, and arm an independently terminating systemd owner. The C# process then validates retained root-owned deployment/request handles and consumes that owner's single-use activation. Unsupported platforms, unprivileged entry, invalid metadata, stale owners and unavailable registrations reject before work starts. See [packaged consumers](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/packaging.md) for the installed tool and SDK dependencies.

Do not launch a second ordinary CLI command with inherited configuration and call it supervised. Reserved roles dispatch before normal command discovery, authenticate the exact kernel peer and process identity, and retain the original deadline. Root ownership does not replace the pre-CLR bootstrap. The [design](../designs/issue-779-csharp-supervision-core.md) explains the workload versus worker shutdown scopes and pending-start ownership.

## Retired runtime

Checkpoint four removes `scripts/evidencehost-linux-launcher.py`, `scripts/evidencehost_linux_application.py`, their direct runtime wrapper and runtime-only regression suites. The older stand-alone mechanism workflow is retired too. Git history retains those source revisions and their dated observations; historical records do not establish current acceptance. The former completion context is replaced by the C# [run custody](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/report-custody.md) and [producer artifact owner](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/producer-artifacts.md), which retain handles/accounts until collection, strict closure and publication finish.

Python remains in repository preparation and finite native test drivers. Those drivers stage reviewed bytes, invoke the protected C# image, observe kernel facts, and fail their tests when facts are missing. They supply no production admission, lifecycle, arbitrary command, callback, process or filesystem capability. The [cutover verifier](../../.github/issue779-checkpoint4/verify-cutover.py) rejects reintroduced legacy runtime files or active C# and workflow references.

## Retained regression intentions

The deleted Python suites tested the retired implementation. Their runtime responsibilities now have behavior controls in the C# graph and genuine same-product process fixtures; this is not permission to delete independent planner, admission, artifact or downstream-gate tests.

| Runtime responsibility | Current control surface |
| --- | --- |
| Closed protocol and malformed/replayed operation rejection | [Control codec](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/EvidenceControlProtocolTests.cs), authenticated server and native worker cases. |
| Pending start/stop, late unit creation and original task joins | [Pending start](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionPendingStartTests.cs), [application lifetime](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionApplicationLifetimeTests.cs), and native cancellation/owner-death controls. |
| Original paired pumps, EOF/errors and aggregate reporter/process quota | [Output ownership](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxOutputPipesTests.cs), [shared ledger](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionRunOutputBudgetTests.cs), and native quota/descendant cases. |
| Root deployment audit and fresh retained workspace/artifact binding | [Deployment controls](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxProtectedDeploymentTests.cs), kernel allocation controls and native custody/report-mutation cases. |
| Application health, exact kernel/DCP identity and denial prerequisites | [Application unit/health grammar](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxApplicationUnitTests.cs), retained workspace/DCP controls and the seven genuine application cases. |
| Physical group exit, strict account closure and quarantine | Native worker/producer/application failures plus the explicit busy-unmount and unsafe-workspace quarantine controls. |

Pure controls remain pure; only source-bound native runs establish actual systemd/kernel/custody outcomes. The [57-group plan](../plans/issue-779-evidencehost-test-plan.md) and dated [acceptance map](issue779-acceptance-status.md) continue to identify independent consumer requirements.

## Native evidence and current acceptance

| Surface | Last independently verified result before checkpoint four | Limits |
| --- | --- | --- |
| Worker/control owner | 16/16 native checkpoint-one controls; see [implementation record](../plans/issue-779-csharp-supervision-migration.md). | Historical source-bound Linux evidence, not a new coverage result. |
| Producer and artifact custody | [Checkpoint two](../../.github/issue779-checkpoint2/README.md), 7/7 Linux controls. | Test failure, cancellation, quota, descendant, report mutation and quarantine are expected negative outcomes. |
| Application and both callers | [Run 39076625148](https://github.com/forge-trust/AppSurface/actions/runs/39076625148), 7/7 controls at `4ce55f5407c9bcf48e1b8dd6f64195450272fbfc`. | Actual AppHost/DCP/resource, CLI/SDK numerical coverage and cleanup; private compiled registrations. |
| Packaged consumer | [Run 39138780771](https://github.com/forge-trust/AppSurface/actions/runs/39138780771), producer lane 7/7 and fresh SDK restore/build/load passed at `8218fb0281231fd10a82f46d489cfcdf666a9912`. | Whole workflow failed its separate coverage prerequisites; this does not establish a numerical gate pass. |
| Complete solution and patch coverage | Required at checkpoint four. | Exact [unchanged gate](issue779-coverage-gate.md); fixture coverage is not repository coverage. |
| Production Trusted/platform acceptance | Closed. | Production catalogue and accepted-proof registry remain empty; no Windows/macOS native execution support is asserted. |

The [runtime workflow](../../.github/workflows/evidencehost-runtime-proof.yml) calls the reusable [worker](../../.github/workflows/issue779-checkpoint1-worker.yml), [producer](../../.github/workflows/issue779-checkpoint2-producer.yml) and [application](../../.github/workflows/issue779-checkpoint3-application.yml) workflows on the same committed revision. The worker lane must reconcile all sixteen controls without skips before the unchanged full coverage command can run. Its fourteen isolated selectors use a fresh Release publication, real build logs and complete source/tool/runtime inventories; historical checkpoint-one receipts cannot satisfy this dependency. Original native deadlines, kernel observations, owner termination and custody predicates still apply. The new workflow requires a fresh Linux execution before it establishes a current sixteen-control result.

The application lane supplies its current, source-bound native measurement to the account creation and rollback controls, then the packaged producer lane. The ordinary producer image comes from a freshly packed and installed NuGet tool; the artifact-mutation image remains explicitly private and nonpackable. Application registration images are also private and nonpackable. All measured images use the same pinned SDK and Release configuration as the solution lane, so branches from different compiler configurations are not mixed. Source captures before/after each lane bind committed bytes and modes. Neither a workflow definition nor a passing metadata assertion establishes execution.

## Checkpoint-four measured attempts

At source `8218fb0281231fd10a82f46d489cfcdf666a9912`, [run 39138780771](https://github.com/forge-trust/AppSurface/actions/runs/39138780771) passed both seven-control native consumer lanes. The ordinary producer used the freshly installed NuGet tool, and the package smoke completed all 15 commands (13 zero exits and two expected rejection exits). The application matrix repeated actual CLI/SDK coverage, readiness failure, factory stall, cancellation, TERM-resistant descendant containment and busy-unmount quarantine. Artifacts `12140798375` and `12141930107` had verified SHA-256 `065703920174ddf37f126ef458eb8af246140f28a5eff9279449b58a1f4035c1` and `4f8a013daab3d7c4917e54c4969b3d4f602693a7e4bcdd9b573f6e38a939dd05`. Both complete 2,965-file captures matched committed Git blobs, hashes and modes with zero mismatches. These results retain informational Observation claims and closed Trusted eligibility.

The same workflow's coverage lane stopped before numerical gating because package-index metadata lacked the internal Supervision entry. The correction classifies it as bundled, excluded and non-publishable, preserves authored qualification paragraphs outside generated release guidance, regenerates the chooser/readiness docs, and stores restore cache outside the captured source tree.

The subsequent full run at `d950f6e5` reached test execution but ended `ASCOV120`: root protocol fixture inputs were missing, the new core test project lacked the wrapper-required JUnit logger, and ordinary-help verification inherited an earlier host exit code. Its raw merged 86.46% line / 73.47% branch figures are not a gate result. The corrected lane arms the existing authenticated protocol test fixture around the unchanged wrapper; the core project declares the same central logger packages as existing tests; ordinary-help setup models a fresh process while preserving CommandService's original sticky failure behavior. Local source verification passed 1,740 core cases with the real JUnit logger and 10 process-entry cases, zero failures/skips/compiler diagnostics. Fresh complete coverage remains required.

## Claims and recovery

Successful observations remain informational with no gate eligibility. Runtime cutover does not populate the compiled production application table, accept a consumer proof, grant secrets, or enable Trusted PR/release execution. Follow the [consumer acceptance contract](issue779-consumer-acceptance.md) and [protected gate reference](evidencehost-migration.md#protected-gate-and-artifacts) before changing that boundary.

An unsuccessful join, deadline, output ledger, retained file, unmount or account close must keep the original failure. Do not publish a success record or reuse quarantined output or identities. A retry requires a fresh generation after physical containment is independently confirmed. Diagnostic retention cannot upgrade a failed run.

## Measuring the native C# owner

The [native measurement project](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md)
consumes actual Coverlet hit data from the guarded root and first normal worker. The application workflow
builds the same Release configuration as the solution lane and instruments exactly CLI, Contracts, Planner,
Coverage, Aspire and Supervision with pinned Coverlet Console 10.0.1. Deployment audit sees the actual
instrumented bytes. The [private C3 measurement fixture](../../.github/issue779-checkpoint4/measurement/README.md)
retains the authenticated worker's private temporary directory and copies genuine hit bytes unchanged after
original normal exit. Coverlet's own tracker and reader perform all accumulation. The fixture is compiled
out of ordinary images. Worker isolation, writable paths, capabilities, limits and deadlines remain unchanged.
The initial measurement left the SDK image uninstrumented, and killed-process or SDK-worker hits were not presumed. The [dual-image measurement](../../.github/issue779-checkpoint4/measurement/README.md) now uses a separate pinned SDK collector and source-bound report. [Run 39168743553](https://github.com/forge-trust/AppSurface/actions/runs/39168743553) verified both seven-control consumer lanes on `10c0a6529b561634f453e95ab918b4f6f4778eb8`, all 29 mandatory root controls without skips, and unchanged raw hit-file handoffs for all six SDK-worker modules. Its separate SDK report records positive hits in the host bootstrap and restricted application adapter. Both complete 3,256-file captures match the source manifest. Producer artifact `12152590015` has SHA-256 `967093dea6a7582079b16584a9cac8ee6f6e744ab0806063b0ee612f800a119d`; application artifact `12151889500` has SHA-256 `6f5375ed9fdbd16f3e7ab0732504d7b747345d649eca209ba21b8aa9fb7039a6`. These consumer and measurement results do not establish a pass of the separate unchanged solution and patch gates, or Trusted eligibility.

The solution lane downloads only its successful application's current-run artifact, stages the five
fixed source/measurement/receipt files, and configures the pinned collector's supported `MergeWith`
setting. The Linux test rejects absent files, source or digest mismatch, incomplete controls, missing
physical settlement and a report with no guarded root hits. The regular [PR/main build](../../.github/workflows/build.yml) calls the same reusable consumer
lanes and stages the same current-run measurement before its existing coverage command. It preserves
its original `HEAD^1` PR comparison and empty main comparison. That job pins the same 10.0.401
SDK/compiler as the native lane so merged branch offsets come from the same compiler; setup still
installs the current LTS runtime servicing patch. The dedicated runtime workflow covers
feature pushes and manual validation; it does not duplicate PR runs. Both coverage jobs explicitly pass
the restored NuGet cache, selected native-measurement flag and docs-evidence location through the
broker fixture's closed child environment. The regular PR build's independent package/platform checks
continue alongside these required native jobs.

This is measurement evidence, not runtime
authority or an accepted consumer proof. The existing coverage script, exclusions, numerical thresholds,
codecov semantics and default `origin/main` comparison are unchanged. A successful test or collector
merge alone does not establish a numerical gate pass.

At [source 3396c580](https://github.com/forge-trust/AppSurface/actions/runs/39141240670), all 55 solution
test projects passed, but final merge stopped at `ASCOV114`: the fixture environment selected an empty
NuGet cache instead of the cache restored by the workflow. The corrected child command explicitly
passes that restored cache to the ordinary coverage wrapper. Its isolated protected producer still
uses its separately staged SDK/package inputs. No numerical gate result is claimed for that attempt.

### Named mutex prerequisite discovered by measurement

[Run 39144415301](https://github.com/forge-trust/AppSurface/actions/runs/39144415301) at
`87fa756597c5a18bce1cf08e67712e02c45b1f91` reached actual application readiness and physical
settlement, then failed its normal control. Coverlet's process-exit tracker threw a named-mutex
`IOException` in the worker and root image, so its zero-hit report is not acceptance or useful gate input.
The captured SDK tree contained runtimes 10.0.12 and 10.0.13: the setup action installs the latest LTS
runtime separately from the selected 10.0.401 SDK. A bounded Linux container probe with runtime
10.0.13 reproduced the same mutex failure for root and nonroot under `HOME=/nonexistent`; each
succeeded with `XDG_RUNTIME_DIR=/tmp`. This is a mutex prerequisite probe, not a systemd acceptance run.

The closed [bootstrap environment](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#root-owner-activation-and-process-lifetime)
now requires that literal sixth assignment for both reserved roles. The native fixture emits the same
six assignments before CLR startup. The worker uses its existing private temporary filesystem;
capabilities, input/output mounts, deadlines and other isolation settings remain unchanged. This does
not change the hit-file location or infer hidden worker hits. A fresh seven-control Linux run and the
complete numerical gate remain required.

At [run 39146333953](https://github.com/forge-trust/AppSurface/actions/runs/39146333953), source
`771d0538347998bdbcff0257fe5089c350ca68bc` passed all seven packaged producer/artifact controls and
all seven CLI/SDK application controls. The downloaded artifacts had SHA-256
`e9ad9f73c2a293fb8a69bf0ad65133f002d34e915ce460bf2c5a3e819d5f0703` and
`196843ff312daa9e12fc709d2d29e29f665c1097f78f978b8f36e898c05b4d46`. Both complete 3,239-file
source captures matched immutable Git blobs, SHA-256 values and modes. The actual application
receipt confirmed final group emptiness for every case, and the native measurement contained
guarded root execution hits. The mutex exception was absent. The 15-command package smoke also
passed, including its two expected rejections. The separate full solution/patch gate was still
pending at this observation; native fixture success is not a numerical gate pass.

The subsequent [failed-handler drain correction](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#pending-work-and-control-handlers)
starts the original descendant stop before joining handlers when a worker disappears without EXIT
intent. Two deterministic barrier controls cover that ordering and retained first-stop ownership;
the focused sequence/deadline suite passed 57/57 with zero compiler diagnostics. This local result
does not replace a new native run or the final unchanged coverage gate on the corrected source.

The same source `771d0538` completed all 57 solution project selections and merged real native input,
but the wrapper exited `ASCOV120` before numerical gating: the CLI suite passed 3,181 of 3,182 cases.
Its existing `CoverageSolutionScriptTests.BuildWorkflow_ShouldUseCoverageSolutionScriptForDefaultLane`
required the canonical `./scripts/coverage-solution.sh` spelling, while the workflow invoked the same
file as `scripts/coverage-solution.sh`. Both workflow calls now use the canonical relative spelling.
The existing 36-case script/workflow suite passed after that correction. No wrapper, threshold,
exclusion, test expectation or gate policy changed. Raw merged coverage was 132,522/139,297 lines
(95.1363%) and 60,095/69,945 branches (85.9175%); those values are not a full-gate pass, and the
official patch result was not reached. The retained coverage artifact was verified against GitHub's
SHA-256 `650318850432d88e0c87b76cf3c479e0e094cc67e541bc4709fbbf60b3ee1f10` before inspection.

At [run 39150305318](https://github.com/forge-trust/AppSurface/actions/runs/39150305318), source
`781f000fa9735bde18ad6db84fa0a1a34ed5428c` again passed all seven application controls, including
both CLI and SDK callers. Artifact `12145158540` had SHA-256
`e0f3dd05c55609603a2d89c5f67ce4debda69a7d3f81e1c6b73b5f49f6683307`; its complete 3,236-file
capture matched Git blobs, file hashes and modes. Every application control confirmed both initial
and final empty groups. The producer lane passed its first four controls but failed the fifth,
`ignored-cancel`, so its remaining two controls were not reached. That artifact's SHA-256 was
`a7e5d66bc898a5b31b4a57c760dd85e7e123e626a45d5217c3f6213553b79eb0`, with the same complete
source verification.

The failed producer state retained `deactivating/final-sigkill`, joined output pumps, and no double
empty-group or physical-settlement receipt. The worker failed while requesting stop. The code gave
systemd's automatic termination the worker's entire ten-second stopping allowance, leaving no
reserved time for the response and final checks. This is a source-backed timing conflict; the
artifact does not independently timestamp signal delivery. The
[producer stop procedure](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/coverage-execution.md#bounded-producer-stop-escalation)
now uses half of the existing stopping reserve for cooperative observations and then explicitly
forces the same selected group under the original cleanup token. Its 52 focused procedure/unit
controls passed locally; a new native seven-control run is required. No previous failure is upgraded
by the source correction or the fixture's later containment.

The measurement lane also runs [root retained-file controls](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#root-filesystem-measurement)
through the existing mandatory Linux factories and standard pinned collector. The extra report is
merged using Coverlet's supported report merge; no raw private-worker hit file is reconstructed.
The image hashes and zero-skip root TRX are required before that input can reach the unchanged gate.
This source preparation is not a measured coverage gain or a new native acceptance result.

### Current numerical gate and remaining work

The unchanged gate did reach numerical evaluation in
[run 39150305318](https://github.com/forge-trust/AppSurface/actions/runs/39150305318).
Aggregate coverage passed at 132,557/139,306 lines (95.1553%) and 60,113/69,949
branches (85.9383%). Patch coverage failed: 13,016/16,264 fully covered changed
lines (80.0295%) and 20,102/27,416 branch outcomes (73.3221%). The configured
95%/85% thresholds and 0.5 percentage-point tolerance were unchanged. This is a
failed gate, not permission to publish a completed checkpoint. The current
source still needs a fresh passing solution and patch result.

[Run 39153423641](https://github.com/forge-trust/AppSurface/actions/runs/39153423641)
on `5608180be49d52d08d7aec4e25024bae3fa9e51e` passed all seven producer/artifact
controls, including ignored cancellation after the stop correction. Its application
lane passed six controls, then the disposal control's observer raised
`JSONDecodeError` while polling the newly created descriptor; neither owner output
stream had bytes. The artifact does not establish an application failure cause.
Both artifacts' complete 3,239-file source captures matched Git blobs, SHA-256
values and modes. The [observer correction](../../.github/issue779-checkpoint3/README.md#descriptor-publication-observation)
waits for the existing descriptor seal; 12 portable controls pass, but the complete
native matrix must run again. The numerical gate job for that attempt was skipped.

## Latest measured gate and worker-measurement work

[Run 39155973682](https://github.com/forge-trust/AppSurface/actions/runs/39155973682) on `4f146641808ec1e24eb91023d522183a270b8d45` passed all 57 project execution records and both native consumer lanes. The unchanged numerical gate still failed `ASCOV020`: aggregate lines 132,573/138,529 (95.70%) and conditions 60,081/69,263 (86.74%) passed; patch fully covered lines 13,065/15,487 (84.36%) and conditions 20,048/26,056 (76.94%) failed. At those fixed denominators, the effective 94.5/84.5% requirements need 1,571 more fully covered lines and 1,970 conditions. Its coverage artifact `12148249302` is SHA-256 `aba10828b6a523c5707f4a62cbe8c528c1685559fd8f02eb07db4f3b60278ea8`. These are measured deficits, not predicted gains from individual tests.

The later source `995d2e1d8675764a4153e220d18b4a68b4f427d9` passed all seven producer, seven application, and 28 root filesystem/kernel controls in [run 39158246121](https://github.com/forge-trust/AppSurface/actions/runs/39158246121); its separate unchanged gate completed with aggregate 95.82% lines/86.91% branches passing and patch 85.41% lines/77.74% branches failing (`ASCOV020`). At that point, the six-module worker measurement and 140 recipe boundary controls still required a fresh run. The current [worker measurement contract](../../.github/issue779-checkpoint4/measurement/README.md) selects seven modules, including the shared Evidence.Cli workflow assembly, and requires fresh source-bound reports. The isolated worker-only helper proof is linked in the [native measurement reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#current-verification-boundary), and supplies no AppSurface coverage or Trusted acceptance itself. No gate threshold, comparison base, Codecov rule or exclusion has been changed.

[Run 39162572720](https://github.com/forge-trust/AppSurface/actions/runs/39162572720), source `54aa6974aff638538888c56baf088080fd2ac50f`, verified the seven application consumers, 28 root controls, and unchanged first-worker hit handoffs for CLI, Contracts, Planner, Coverage and Supervision; Aspire had no worker file in the CLI control. The same run's producer observer failed its final control, as recorded in the [observer reference](../../.github/issue779-checkpoint2/README.md#observing-the-sealed-descriptor). Its separate unchanged gate reached numerical evaluation: aggregate 133,317/139,003 lines (95.9094%) and 60,281/69,283 conditions (87.0069%) passed; patch 13,266/15,438 fully covered lines (85.9308%) and 20,451/26,088 conditions (78.3924%) failed. At those fixed denominators, 1,323 more fully covered lines and 1,594 conditions are needed. Artifact `12150457433` has SHA-256 `ee4662b5cbb7c5911725c454dc2e6e1185d69ee92ba9c0435c44b3e667afcac8`. The exact diff and streamed XML independently reconcile those counts. Later source changes require a fresh gate; this failed result cannot establish checkpoint completion.

The runtime workflow retains the gate's actual `coverage-patch-targets.json` and `coverage-patch-targets.md` filenames alongside its numerical report. These remediation artifacts do not alter evaluation, comparison base, thresholds or exclusions.

### Latest SDK measurement and workspace failure coverage

[Run 39168743553](https://github.com/forge-trust/AppSurface/actions/runs/39168743553), source `10c0a6529b561634f453e95ab918b4f6f4778eb8`, passed both seven-control consumer lanes and all 29 required root tests without skips. The unchanged gate reached numerical evaluation and failed `ASCOV020`: aggregate 132,819/138,377 lines (95.9834%) and 59,815/68,428 conditions (87.4130%) passed; patch 12,832/14,812 fully covered lines (86.6325%) and 19,517/24,378 conditions (80.0599%) failed. At those fixed denominators, 1,166 more fully covered lines and 1,083 conditions are required. Coverage artifact `12153400464` has verified SHA-256 `fe7f5db23135448c3238835f7de947d99000b0aacb5f6c7c9a6a9ad5e76ab90b`. The exact source capture, immutable-base diff and streamed XML independently reconcile those counts.

The subsequent [workspace rejection controls](../../.github/issue779-checkpoint3/README.md#native-workspace-rejection-controls) prepare real unsafe objects and require failure/quarantine before any fixture containment. Their local data and compilation checks are not native verification or a predicted numerical gain. The original coverage command, Codecov full-line rule, comparison base, thresholds, tolerance and exclusions remain unchanged.

### Current worker lane and measurement configuration

[Run 39212528881](https://github.com/forge-trust/AppSurface/actions/runs/39212528881),
source `2befb98ad5e1a214a4d33abb357c1f8dfd11f0cf`, completed the application,
account creation, rollback and packaged producer measurements. The standard
report chain was C3 → C4Accounts → C4AccountRollback → C2 → None, with identical
3,290-file source captures. Its unchanged numerical gate failed `ASCOV020`:
aggregate 134,886/147,193 lines and 62,668/87,973 outcomes; patch 13,553/18,123
fully covered lines and 36,067/68,746 outcomes. Coverage artifact `12171214181`
has verified SHA-256 `077b33412e76e5bb67ca1c60b416f8eaba89526585ad4c8d23d01b0239b8390c`.
These results do not establish checkpoint completion.

The [account](../../.github/issue779-checkpoint4/measurement/accounts-README.md#build-and-entry-contract)
and [producer](../../.github/issue779-checkpoint4/measurement/producer-roots-README.md#workflow-command-construction)
publications now use Release consistently with C3, the installed package and the
solution build. Debug and Release branch maps can differ even for equal source
hashes. The correction requires fresh collection and the standard report merge;
it does not rewrite report rows or predict a passing percentage.

The reusable [current worker lane](../../.github/issue779-checkpoint1/README.md)
now supplies all sixteen controls through fourteen isolated selectors. The
source-owned inventory records availability to run, with mandatory actual raw
archive, command and stream readback. It does not turn the historical sixteen
passes or portable test data into a current result. Local preparation passed
87 portable controls and actual locked restore, formatting and Release
publication of all three helper projects. The SDK-free runtime copy was also
executed locally. These macOS preparation checks supply no Linux qualification,
native acceptance or coverage credit; the final source still requires a new
Linux matrix and passing unchanged solution and patch gates.
