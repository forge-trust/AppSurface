# Issue #779 acceptance map — 2026-10-02

## Protected entrypoint continuation — 2026-10-28

The [current command and replay preparation](issue779-csharp-runtime-cutover.md#protected-commands-and-credential-free-replay-preparation--2026-10-28)
adds fixed credentialed preparation/closeout commands, strict comparison-data wire files and a
separate credential-free replay worker. No finalizer workflow is enabled. Revision 119's focused
run passed; its runtime failed because the first SDK lifecycle owner remained live at the original
observation bound. Worker selectors and reconciliation passed, but dependent measurements and
unchanged numerical gates were skipped. The failure diagnostic gap is corrected for a fresh run;
neither a root-cause fix nor a repaired native result is claimed. Outer isolation/settlement,
protected full-stage replay, support registration and Trusted/downstream acceptance remain open.

## Native-validation migration and current continuation — 2026-10-27

The [current completed-run preparation](issue779-csharp-runtime-cutover.md#protected-completed-run-source-and-provider-checks--2026-10-27)
adds separate validator/completed-source anchoring and provider revalidation around the fixed replay
sequence. The credentialed comparison APIs never execute target callbacks or builds; no finalizer
workflow is enabled. Revision 118 passed every standard root measurement, including N06, but failed
original N08 prerequisite acquisition before native execution. Its solution/patch gates were
skipped. Source/support anchoring preparation does not establish protected execution, admission,
native acceptance, numerical gate success or Trusted/downstream acceptance.

The [fixed replay-controller preparation](issue779-csharp-runtime-cutover.md#fixed-public-replay-controllers--2026-10-27)
adds exact-source isolated build/test and public CLI replay controllers with bounded output,
deadline and process-group checks. Detached Linux validation passes 163 tests with five explicit
fixture opt-in skips; genuine protected full-stage replay and original-success binding remain open.
Revision 115's genuine N05 procedure now passes under the unchanged bounds. Its full runtime chain
and numerical gate remain pending, and admission/catalogue/support registration stay closed.

The [latest completed-validator preparation](issue779-csharp-runtime-cutover.md#completed-validator-preparation-and-n05-timing-repair--2026-10-27)
adds fixed native input/custody and single-method TRX contracts plus original gate archive/report
checks. The public C# data validator passed once against revision 109's exact captured source and
authentic four-role inputs using SDK 10.0.401; that mechanism fixture does not satisfy the protected
completed-run resolver. The later revision-113 runtime N05 failed the unchanged time guard with
245 seconds remaining where more than 246 is required. A batching preparation preserves every
file check and original bound; a fresh Linux run must verify its effect. Full orchestration,
original-success proof, numerical gates and protected Trusted/downstream acceptance remain open.

The [current revision-109/110 record](issue779-csharp-runtime-cutover.md#revision-109-validation-and-revision-110-repair--2026-10-27)
supersedes the continuation results below. Revision 109 passed all original selectors,
consumer/account/C2 jobs and fourteen ordered measurements, then failed the unchanged gate on
two stale streaming fixtures before a numerical gate result. Revision 110 repairs those fixtures
without runtime changes; ten focused tests and the full 4,262-test CLI suite pass locally, with
five platform skips. Its new complete Linux chain is running. The six revision-109 portable
procedures and separate 29-plus-four custody checks passed with all authority flags false.
Completed C2, measurement-chain, C3 and account replay are mechanism checks only; protected qualification,
numerical gates, final orchestration and actual Trusted/downstream acceptance remain open.

The [revision-113 focused Linux run](https://github.com/forge-trust/AppSurface/actions/runs/40524812393)
passed all 29 filesystem/report and four separate peer-credential tests without skips. Its original
provider-digest-verified archives bind source `1d5b8a00f04da6e0cfd6f81ee22b51ad4f06127d`, tree
`eef32358a06073ca1dae5a516b38fec2ba42c4a5` and all 4,004 immutable Git paths/blobs/modes.
The [runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40524812444) is still running.
The local combined qualification selection passes 101 tests with five explicit opt-in fixture skips;
the C3 selection passes 53. Source review also corrected the exact multi-path coverage-capture member,
bound lifecycle account names/tables to the generation, and rejected account replay completion after
its original deadline. These follow-up fixes require their own source-bound candidate run.

Latest remote `main` (`ebb562be618c2649c1ef0ade12095e56468882c7`) is merged into the
continuation branch. The [native-validation tool and workflow](../designs/native-evidence-validation.md)
now run an explicit portable procedure, write a procedure report instead of an Evidence manifest,
and fix protected admission, native acceptance and gate eligibility to false. Unsupported protected
operation rejects before input/output access. The migration preserves numerical coverage policy,
the fixed comparison base, native deadlines and the closed production admission boundary.

The [cutover record](issue779-csharp-runtime-cutover.md#local-integration-with-current-main--2026-10-27)
records the 393-test local migration selection, the six passing revision-106 OS/mode procedure jobs,
and the revision-107 repair of the closed application descriptor writer. The strict parser remains
unchanged; unsupported settings are rejected before its existing eight-field wire projection.
Full supervision passed 3,105 local tests with 91 explicit Linux/root-only skips. These local and
portable results supply no protected acceptance or current solution/patch gate result.

The [revision-107 runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40476296231)
is bound to source `e1aed288b197a6ade0288f1372b49d3c574561db` and its 3,986-file capture.
All fourteen original selectors and reconciliation passed, as did the SDK negative consumer,
seven application/six workspace controls and separate SDK lifecycle controls. The application job
and both account jobs completed successfully. The packaged producer then failed while packing Aspire;
the dependent measurement jobs and numerical gates were skipped. Local reproduction identified
the duplicate README pack item (NU5118), and the prepared repair uses the shared pack target and
includes the Readiness dependency in the ordinary package journey. A fresh run remains required.
The [focused custody run](https://github.com/forge-trust/AppSurface/actions/runs/40476295971)
completed successfully: original artifacts matched provider digests and the immutable source tree,
with 29 filesystem/report rows and four separate actual peer-credential rows passed and unskipped.
These source-bound checks do not enable Trusted execution.

The [completed C1 callback mechanism](../../.github/issue779-protected-qualification/completed-c1-README.md),
[C2 receipt replay](../../.github/issue779-protected-qualification/completed-c2-README.md) and
[measured C1 chain replay](../../.github/issue779-protected-qualification/completed-measurement-chain-README.md)
are bounded preparations, along with [C3 retained closeout](../../.github/issue779-protected-qualification/completed-c3-README.md)
and [account lineage replay](../../.github/issue779-protected-qualification/completed-accounts-README.md).
Full completion validation still requires the original C# Native.Tests data validators,
full orchestration and unchanged coverage/Codecov replay checks. Protected-main qualification,
authenticated provider/commit/tree/file anchoring, reviewed exact support registration, an ordinary
Trusted CLI/SDK journey and downstream protected acceptance remain outstanding. The production
catalogue, request mode and accepted-proof decision remain closed. The historical numerical figures
below describe their original source and must not be projected onto this continuation.

<a id="latest-verified-consumer-checkpoint--2026-10-26"></a>


## Revision 96 verified results and revision 97 correction — 2026-10-27

### Current isolated preparation and focused Linux validation — revision 101

[Revision 101](https://github.com/forge-trust/AppSurface/actions/runs/40445212390), source
`3fb45d86151262152394405f7d8072c46ea9a1cf`, retains the unchanged production limits,
comparison base, thresholds and exclusions. Its full consumer and numerical-gate chain is still
in progress as this status is written. The fifteen C1 artifacts independently matched their API digests
and all 3,624 immutable source blobs/modes. Original callback replay returned N01–N16 exactly once,
all passed and unskipped, and reproduced the retained aggregate. The C1 verification receipt has
SHA-256 `8539a43dd0b4fdd15c9d4ed6eeafa73f1451ab34712de70f1b29d183f07e469d`. The separate
[focused Linux run](https://github.com/forge-trust/AppSurface/actions/runs/40445212296) completed
successfully. Independent verification checked artifact `12706909955` (266,061 bytes,
SHA-256 `78d7414656466df87dd277a4c9d08ca6ba0ed44b61fa17451ed25eb99cd62147`), its exact
3,624-entry source capture and all seventeen passed, executed, unskipped TRX rows: six application
bundle-audit cases and eleven report-discovery cases. Restore/build/source/TRX checks exited zero;
the build reported zero warnings and errors. These are actual root Linux file controls, not a
full consumer, coverage or Trusted result. The focused verification receipt has SHA-256
`a3434121117f0ede28ca65c091091c8ceeb89f67b7d2439a5f98769b9f070d9d`.

The same revision's application job has since completed successfully. Independent verification of
artifact `12710686457` (3,419,277 bytes, SHA-256
`e287e90c60bbd1b9b4b116423b44a5bf4eaf2ff0d0f61b4cd28a072ea772aa97`) checked the
complete source capture, all thirteen original application/workspace records, the expected SDK
failed-test result, 43 root-file rows, 44 supplementary rows and all seventeen exact audit/discovery
test identities. Its verification receipt has SHA-256
`223c5e32b944f9e8db48b1f80360f2ceea975e169b61d130a418f72d5b9ca683`.
The separate lifecycle artifact `12710526990` (516,045 bytes, SHA-256
`831d7bf2ecf299f653946cc55612e0d0aad1b6cb4cfb3631895982d55b4cd482`) passed the
three fixed SDK lifecycle controls and their terminal group/path/account checks; its verification
receipt has SHA-256 `e0aeed1d1f0a5d9b4844413dd049cb2278024894f20199f48dde0b7cb191860d`.
The consumer chain and numerical gate remain in progress. These source-bound results do not enable Trusted.

The next isolated preparation preserves the control server's five existing work replies through a
[serialization-only codec](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/SupervisionControlReplyData.md)
and directly checks the original [final worker-settlement decision](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/worker-settlement.md).
The integrated local supervision suite passed 3,081 cases with 77 Linux/root-only skips. Twelve new
[selected-results directory controls](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxSelectedProducerResultsRootNativeTests.cs)
exercise the original mandatory-openat2 descriptor chain, separately from producer authentication.
They are source-compiled on macOS and require fresh Linux execution; the combined audit/discovery/selected-root
measurement now requires all twenty-nine exact methods. Production limits, original deadlines, the
comparison base, exclusions and coverage thresholds are unchanged. No numerical coverage gain is claimed.

The reviewed [protected qualification prerequisite](../../.github/issue779-protected-qualification/README.md)
now calls the complete runtime-proof graph. Its data-only collector requires the protected context
and all thirty-five runtime leaf jobs to succeed for the exact current head and attempt before
retaining the original C1 aggregate. Twelve detached boundary controls passed. The internal
[support-record parser and matcher](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/protected-support.md)
also passed 52 detached canonical-record and current-fact controls after aligning its worker protocol
with the existing v2 application descriptor. They do not authenticate their caller-supplied digests
or provider facts and do not issue admission. This workflow has
not run on protected `main`; a candidate run cannot establish that prerequisite. Full post-run
artifact verification and the separately reviewed support registration remain required before
runtime Trusted admission can be enabled and accepted.

### Current follow-up preparation and verified C1 boundary

[Revision 98](https://github.com/forge-trust/AppSurface/actions/runs/40438904270), source
`a9c7f5132422827fbb16ca8ce97a1b0b3f5f9f41`, has independently verified all fifteen
C1 artifacts against their API digests and complete 3,611-file source capture. Replaying
the original selector callbacks returned N01–N16 exactly once, with zero skipped cases,
and reproduced the retained aggregate. The verification receipt SHA-256 is
`15e11240e243650afb7b12511b83f121277dd76aec8a64405e043785adbf3065`.
This records C1 only; the consumer chain and numerical coverage gate remain separate.

The next prepared candidate extracts the existing
[report-discovery procedure](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxProducerReportDiscovery.cs)
from the custody owner without changing root selection, ownership, native resolution,
rechecks, limits or deadlines. Eleven actual-root discovery cases join the six application
audit cases in the same standard collector pass. The
[closed measurement contract](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#separate-application-bundle-audit-measurement)
requires exactly seventeen named passed rows; the original 43-row root and 44-row
supplementary measurements remain independent. On macOS, ordinary supervision passed
3,013 cases with 77 explicit Linux/root skips; these skips establish no native outcome.

Local validation also reproduced and corrected the account verifier's stale source pin
after the settlement-policy extraction. Its
[reviewed hash and method spans](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#reviewed-producer-root-witness-mappings)
now match the actual source. Rebound historical hashes and the obsolete rollback span
still reject; the lineage zero-hit control zeros the actual current hit map. The full
measurement test project passed 395 cases with one native-only skip, and the combined
TRX parser passed three portable methods. Those are local data/source checks, not new
Linux coverage or Trusted acceptance. No process limit, deadline, coverage threshold,
exclusion or comparison base changed.

[Revision 96](https://github.com/forge-trust/AppSurface/actions/runs/40418448002), source `3a650e86eafc1553e54aad0b0d2c12713a2c27f1`, has independently verified C1 and C3 artifacts. All 15 C1 archives matched their API digests, the complete 3,603-file source capture, immutable Git blobs and modes; the original selector callbacks returned exactly 16 unique passing, unskipped controls and reproduced the aggregate. The C1 verification receipt is SHA-256 `21f675f819ecb8b99707e475e98ef65494fd9277391c927f0f27b1b8f694e9e9`.

The application artifact `12699233184` (SHA-256 `c76b756ad20405c5a08341177567d822efa0214c93ef373bd83882adbf2ea48c`) passed independent source/receipt checks for 13 original records (seven application/lifecycle cases and six bundle/scratch cases), 43/43 root tests and the separate 44/44 supplementary root tests. The intended SDK failed-test control retained five passed tests and one expected failed test, with cleanup and account absence. The separate lifecycle artifact `12699308275` (SHA-256 `adfdb7f9c464fb2558fc6c53cc57595d9d22db8a9be159ca47af398cb2d894ad`) verified all three fixed lifecycle cases and their final group/path/account absence. Verification receipts are SHA-256 `6880cf6427d2b4de0c7bec9c49d5691871262f607ff06c6c33294245f1930591` and `56f70f10509536e78224d6bc00284d21e80284bb92472d3cdfb5a2f893c1db14`. These are retained source-bound mechanism and consumer results, not Trusted acceptance.

The account job published its image successfully, then rejected its C3 predecessor with `account-control-prior-root`: its [lineage validator](../../.github/issue779-checkpoint4/run-accounts.py) still required 41 results instead of the current 43. Revision 97 pins the current exact count and preserves the hash-bound TRX, complete passing rows, failure-counter checks and separate supplementary receipt. Its 19 portable lineage controls passed, including coherent obsolete/adjacent-count rejection. The full ordinary supervision suite passed 3,010 tests on macOS; 65 Linux/root-only cases were skipped and remain native obligations. Four private qualification builds passed with warnings as errors. No limit, threshold, base or exclusion changed.

[Revision 97](https://github.com/forge-trust/AppSurface/actions/runs/40433493800), source `fb3e6e24c8c7f3ea3668985825ae4bfab7e3e052`, is the in-progress Linux validation for that correction; no terminal result is recorded in this section. It also validates the reviewed worker-settlement and producer-seal decision controls. Revision 96's account failure prevented rollback, packaged-consumer, segmented measurement and full coverage jobs, so it supplies no new numerical gate result. Revision 91 below remains the last fully analyzed patch result; a proposed test does not reduce its measured deficit.

### Protected Trusted work still required

The [protected parent entry point](../../.github/issue779-protected-consumer/entrypoint-README.md) has verified Linux transport and actual semantic-child controls, but its registry is empty and it has not run as a protected-main consumer. Its semantic result checks an existing Trusted manifest; it issues no runtime admission. The [root launch request](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/EvidenceSupervisorRequest.cs) currently accepts Observation only, the [root descriptor](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/EvidenceWorkerDescriptorData.cs) emits Observation with no acceptance proof, and [Planner's accepted-proof decision](../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceProtectedWorkerInputs.cs) remains false. Closing this gap requires a reviewed protected qualification/bootstrap, actual immutable build/deployment and workflow facts, a separately reviewed exact support registration, an ordinary production Trusted CLI/SDK run, and protected downstream acceptance of that completed run. Candidate green jobs, serialized assertions and matching hashes alone cannot supply those facts. The catalogue and production Trusted registry remain closed.

### Prepared application inventory measurement

The [application bundle audit](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md#application-bundle-source-audit)
now shares the workspace's existing retained-descriptor inventory and recheck code without granting another
owner or workspace capability. Six [root-native filesystem controls](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxApplicationBundleAuditNativeTests.cs)
cover real selected bytes, substitution, unexpected or unsafe entries, cancellation and depth boundaries.
Their [separate collector and exact TRX validator](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#separate-application-bundle-audit-measurement)
preserve the original 43-row root suite and 44-row supplementary suite and merge only actual collector output.
Local ordinary supervision validation passed 3,010 cases with 71 Linux/root skips; the measurement parser
suite passed 389 cases with one native skip. The repository fixture-path policy passed after adding the
existing shared testing helper reference; no package version changed. These local results do not establish
the six native outcomes or reduce the revision-91 measured coverage deficit. Fresh source-bound Linux
execution and the unchanged full coverage gate remain required.

### Revision 97 portable predecessor correction

Revision 97 passed the C1 reconciliation, application consumers, account creation and rollback jobs.
Independent verification of the fifteen C1 artifacts reproduced all sixteen original callback rows
(receipt SHA-256 `037192ad449c30a9f216cd9ffc5cbf32ec38a95022aeecb0429a160365caedc4`). Its original
C3 and separate lifecycle artifacts also verified the thirteen native records, 43 root rows, 44
supplementary rows and three lifecycle cases (receipt SHA-256
`d064107f36094ddb72f1eb4e2d5888bfc60b0d8b81b1cb32040e86a474877e58`).

The packaged consumer then stopped in its portable preflight, before native execution: nine of the
31 predecessor controls used an obsolete rollback receipt missing `cleanup_identity_guards_checked`.
The current [validator](../../.github/issue779-checkpoint4/run-accounts.py) correctly rejects missing
fields; the [fixture](../../.github/issue779-checkpoint4/test_producer_roots.py) now supplies explicit
`null` for rollback and separately rejects missing, true and false neighbors. All 32 portable controls
passed after this fixture-only correction. No production validator, process limit or threshold changed.
The downstream segmented measurements and full gate were skipped, so revision 97 establishes no new
numerical coverage result. Revision 98 was published before this failure was diagnosed and does not
include the fixture correction; its new native audit results remain separate.

The [protected qualification prerequisite](../../.github/issue779-protected-qualification/README.md)
is now source-prepared with ten offline boundary controls. Its protected-main review/merge, genuine
dispatch, independent completed-run and selector-artifact verification, required consumer/gate results,
support registration and ordinary Trusted runtime wiring remain outstanding. Its partial receipt
explicitly claims none of those outcomes. The production accepted-proof and record registries remain empty.

## Revision 93/94 follow-up and protected-parent qualification — 2026-10-26

[Revision 93](https://github.com/forge-trust/AppSurface/actions/runs/40407960173), source `d69899a521400305c201a122fd18b5f3e9d84d9c`, has independent verification of all 15 C1 artifacts: the 14 original selector callbacks cover exactly 16 case rows and the recomputed aggregate matches the CI aggregate. The verification receipt SHA-256 is `e6eb24d1be066c499f8d45aa0f80a883bececd3f7625d4348f6bcf874929f74c`. This is C1 evidence only; the application consumer failed before full acceptance. Its private post-stop fixture has since been corrected to keep the original single coverage slot.

[Revision 94](https://github.com/forge-trust/AppSurface/actions/runs/40411752585), source `f0e55c64e5d808780c9a76342842405bca66bb2c`, passed C1 and its original application/workspace measurement stages, then failed the added SDK configure-stall control. The retained lifecycle artifact `12695118287` is 504,174 bytes with SHA-256 `d03f4d8de8f84fd35bcf03b06c308808e9298a8a6fdeff31cfb2f368985bd7e7`. The worker reached the exact callback-unsettled fatal reason. The fixture rejected an exact empty `settled` observation from the pending application holder, despite no application startup; the [fixture correction](../../.github/issue779-checkpoint3/lifecycle-stalls-source-preparation.md#pending-holder-before-application-start) requires that same generation/unit, no main status or bytes, and all pending-holder joins. Its 21 portable methods passed locally; fresh Linux execution is still required. Later lifecycle cases were not executed, and the full gate was skipped.

The new [protected-parent qualification entry point](../../.github/issue779-protected-consumer/entrypoint-README.md) selects only a reviewed, hash-bound record committed on protected default-branch source. It is prepared to bind authenticated repository metadata, exact completed producer run/workflow/artifact bytes, protected policy/diff, actual collected bodies, and the existing semantic gate when invoked from that protected workflow. This parent implementation has not performed a live API resolution or protected-workflow execution. Local validation passed 44 transport/entrypoint controls, 11 actual semantic-child methods and two dispatch-to-child methods; locked restore/build had zero warnings/errors. These public-data/process controls issue no admission. The reviewed-record table is empty; the workflow still requires protected-main review/merge and independently accepted consumer records. The production catalogue, Trusted registration and accepted-proof registry remain closed.

The last fully analyzed numerical result remains revision 91's unchanged patch failure, documented below. No threshold, comparison-base, exclusion or production-limit change is part of this follow-up. The fresh native run must complete consumer acceptance and the exact full coverage command before any gate-pass claim.

## Latest verified consumer checkpoint — revision 91 — 2026-10-26

[Run 40266921255, attempt 1](https://github.com/forge-trust/AppSurface/actions/runs/40266921255) is bound to source `4b70e5dc228cdc3cea6c28bab2787668b9796c2e`, a 3,579-file capture with SHA-256 `464aa36327a3efa2665538317ad1caff6f85dcdcd1418ed6981fa2c09ce34efb`. The retained terminal observation (SHA-256 `9abd83d04e5b6f36c08494ac351db5f37e53a44ac814a6113d610e18e638a2e4`) records all 12 bounded verification commands at exit 0, with direct children joined and log hashes matching. The C1 receipt verified all 16 original callback rows across 14 selectors (SHA-256 `1b9972cfac33ec976642e867ee2358099da93d0f2236255d36d3b8b9f8cf811f`); its receipt explicitly sets native acceptance and gate pass to false. The C3 receipt records all 13 original native cases closed and empty, 41/41 root tests and a separate 41/41 supplementary TRX, with no failed checks (SHA-256 `f977b520725cc4d3ea7738a0dd43caf598f3376a48ef9e84399f6bdaa8f8c14f`). Account and expected-rollback verifications report 15,098 and 15,108 checks, respectively, with no failed checks.

The C2 verification records seven producer/artifact cases and 118 checks, including the paired stdout/stderr EOF checks. Its original archive is bound by SHA-256 `87f2ca3ec5b443d6856169539d8a649187f75771715d39b4aad877715aabcce7`. The retained package-smoke detail receipt (SHA-256 `bfccf490ceecb7a3659d806defae23808a58bf2bae47fc71c6d4dd868d9cdbec`) validates 28 raw files totaling 32,490 bytes, including the 22 command logs. The C2 verification-complete receipt is SHA-256 `4e00a9474f2381f3d97a4b7be0db46c172ff5fbd9d4b5b1df130c18fd6e15e0a`. These are source-bound consumer artifact checks; they do not close the 57-group map, imply Trusted acceptance, or establish the full coverage gate. The revision-91 full gate has since completed: aggregate coverage passed and patch coverage failed ASCOV020, as detailed below.

The revision-91 full gate completed with aggregate coverage passing at 136,260/141,912 lines (96.0173%) and 62,439/71,169 branches (87.7334%). Patch coverage failed ASCOV020: 16,009/18,099 lines (88.4524%) and 24,873/29,988 branch outcomes (82.9432%), below effective thresholds 94.5%/84.5% by 1,095 lines and 467 conditions. The result is bound to run 40266921255, attempt 1, source `4b70e5dc228cdc3cea6c28bab2787668b9796c2e`, capture SHA-256 `464aa36327a3efa2665538317ad1caff6f85dcdcd1418ed6981fa2c09ce34efb`, and unchanged comparison base `1991280e8af049db592ebcb705cbf66f4b4c6e8b`. Retained `summary.json` has SHA-256 `7693502af833013b019e286de925c1363ed20fa5413a00b36dbec07bc3db8998` and binds artifact 12633545260 (2,824,174 bytes, SHA-256 `97fd2c460d405e4238352e828d2c9349fcb85ba4f21d4922f56f1712548c2f6d). The [gate job](https://github.com/forge-trust/AppSurface/actions/runs/40266921255/job/121433971413) records the workflow result. This failed patch gate is not full acceptance.

### Prepared integration snapshot — `d7e5facf`

The pre-publication integration snapshot is `d7e5facf26528f78a0367469f222229c7d5bb3fe`, with a 3,583-file source capture SHA-256 `ae65ae4d39dbeb0945a2234dee95d2a3482732b3a4824d39da84b473ea36c089`. It was unpushed when recorded and has no Linux-native acceptance result. Local preparation evidence reports the original portable helper/runner suite at 41/41 and a separate generated-slot regression at 1/1 (receipt SHA-256 `2394d188270b73fe5c687f371471835caabf7969707ad456e9920b977747bfb7`); these were not run as a single 42-case suite. The C# preparation reports 110 ordinary cases and four private CLI variants with zero diagnostics; the property selection reports three expected rejections. Two initial wrong-target attempts are not credited. These local results do not establish native post-stop enforcement or coverage gain.

| Requirement area | Current evidence and remaining boundary |
| --- | --- |
| L07 / N12 descendant EOF | The revision-91 C1 N12 record supports the specified descendant-work/paired-EOF observation. This does not establish every lifecycle, pump, or write-closure requirement. |
| L03 / P03 post-stop guards | New private post-stop launch/write guards are in the unpushed `d7e5` preparation. Their Linux-native verification remains pending. |
| A04 / L06 / L08 configure and disposal | The added configure/disposer cases still need Linux execution; verifier, write, and pump assertions remain outstanding. |
| U02 / U06 / U07 | U02 has the source-91 raw installed-package journey. Exact package `0.1.0-issue779-c4.d7e5facf2652` passed its local macOS installed-package journey in 53.94 seconds: 22 commands and 28 retained, source-verified raw leaves on the 3,583-file `d7e5` source (receipt SHA-256 `d87155631d5c3b9034b7981641730162012a2a50735175a8464d685d111143ac`). It checks omitted/false/true legacy SDK overloads and schemas 0.9, 2.0, and 9999. A fresh Linux run remains pending. U07's fresh-user/operator manual trace remains pending; the source-91 verifier records `independent_human_usability: false`. |
| A10 / B04 / G04 / R06 protected parent | Protected-parent implementation and supported-run acceptance proof remain incomplete; production Trusted registration and the accepted-proof registry remain closed. |

See the [acceptance plan](../plans/issue-779-evidencehost-test-plan.md), [runtime proof workflow](../../.github/workflows/evidencehost-runtime-proof.yml), [application lifecycle reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/application-lifecycle.md), [post-stop guard preparation note](../../.github/issue779-checkpoint3/post-stop-guards-source-preparation.md), [packaging reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/packaging.md), and [consumer acceptance record](issue779-consumer-acceptance.md). The source-91 consumer verification and the local `d7e5` preparation are separate evidence snapshots; neither updates the historical group rows below.

## Previous verified consumer checkpoint — revision 90 — 2026-10-26

[Run 40255212264](https://github.com/forge-trust/AppSurface/actions/runs/40255212264), attempt 1 at source `0f2c91f777e476529506e5ce1236acf21ec78425`, has source-bound consumer-artifact verification for all 16 C1 worker controls, all 13 original C3 native cases, 41 root-class tests, the 41 supplementary tests declared by the run binding, account creation and expected rollback, and seven packaged C2 producer/artifact cases. All 11 bounded verification commands exited zero, joined their children, and matched their retained log hashes. The C2 packet records 118 checks, including both stdout/stderr EOFs for each of its seven cases. The source capture contains 3,577 files and has SHA-256 `93ac64b60793aa3bdca5be9efb095e4b156ddb25396d918b89370ff2f1036cec`. See the [consumer run](https://github.com/forge-trust/AppSurface/actions/runs/40255212264), the [cutover record](issue779-csharp-runtime-cutover.md#previous-verified-consumer-checkpoint--revision-90--2026-10-26), and the retained local `terminal-observed.json` receipt (SHA-256 `46e884ad473d55ea36951fd92b2179509fea5416cfbb988f20be8dd00f7c6f2d`).

The 41 supplementary tests are a run-binding count; this receipt does not claim they were natively accepted. These results verify the listed consumer artifacts and checks only. They do not close the 57-group acceptance plan or establish a passing full solution/patch coverage gate. The retained receipt explicitly records `full_gate_claim: false`, `consumer_receipt_gate_pass: false`, and `consumer_receipt_native_acceptance: false`. The 22 package-smoke command fingerprints match the reviewed exit/argv/log-digest matrix, but raw smoke logs and starter/plan/summary JSON were not retained, so those command outputs are not independently revalidated here.

Revision 89's full run ended `ASCOV120` before numerical gating after two unrelated test failures: the PostgreSQL session-count check and the second-page form timeout. This is not a revision-90 gate result. The later revision-90 numerical result is recorded in the section above; this paragraph preserves the status as it was recorded at the time.

The revision-57 CLI shutdown-test correction remains a fixture-clock change only. Its Linux regression passed 578/578 focused CLI tests, with production deadlines and stopping allowances unchanged ([regression run](https://github.com/forge-trust/AppSurface/actions/runs/40104067585)). Production Trusted registration and accepted-proof records remain intentionally closed; this consumer verification does not enable Trusted or claim unsupported-platform acceptance. Earlier dated group-map entries below are historical and do not constitute current 57-group closure.

<a id="current-c-runtime-validation-status--2026-10-24"></a>

## Previous C# runtime validation status — 2026-10-24

The original revision-56 shutdown-test failure is fixed and passed its subsequent
Linux regression checks. [Revision 74](https://github.com/forge-trust/AppSurface/actions/runs/40124730961)
verified all sixteen worker controls, the original application and packaged
producer consumer groups, workspace/root controls and both account transitions.
Its measured chain and unchanged numerical gate remain pending. See the
[current cutover record](issue779-csharp-runtime-cutover.md#current-correction-and-verification-status--2026-10-24)
for source identities and the limits of these results. This does not close the
remaining 57-group acceptance matrix, enroll the production catalogue or enable
Trusted execution. Later source changes require fresh evidence.


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
The coverage and [C# cutover reference](issue779-csharp-runtime-cutover.md) retain
the original thresholds, comparison base and exclusions. Further source edits require their own source-bound Linux and coverage verification. These observations do not close all
57 groups, populate the production Trusted catalogue or accepted-proof registry,
or establish protected Windows/macOS/native-platform acceptance.

This is a bounded test and fixture audit against all 57 groups in the [approved plan](../plans/issue-779-evidencehost-test-plan.md). It maps current test methods and fixture cases; it is not a production-code review or acceptance decision. Test links point to files in the live, shared worktree, which has concurrent uncommitted changes. **Presence is not a pass:** only the explicitly recorded focused results below establish execution of their selected cases; no complete acceptance decision is made.

Receipt paths under `TestResults` are local-only ignored execution records. They are not included in a published
checkout, and their recorded results do not verify later source edits. Public CI observations link to their
recorded run through the consumer acceptance record.

## C# supervision migration — 2026-10-17

The [migration plan](../plans/issue-779-csharp-supervision-migration.md#checkpoint-3-restricted-application-and-sdk-parity)
now records the implemented C# application owner and its current checkpoint-three Linux boundary.
[Run 39076625148](https://github.com/forge-trust/AppSurface/actions/runs/39076625148) passed all seven
checkpoint-three native controls on `4ce55f5407c9bcf48e1b8dd6f64195450272fbfc`, including genuine CLI/SDK
coverage and the required readiness, factory, cancellation, descendant and disposal failures. The disposal
case verified busy-unmount rejection, joined work and retained accounts; it returned no successful root
result. The [validation record](../../.github/issue779-checkpoint3/README.md#verified-checkpoint-result)
binds the artifact digest, complete source inventory and test counts. Checkpoint three is complete.
The historical group inventory below is not a current acceptance total; full
cutover, the complete unchanged coverage gate and consumer/platform proof remain separate requirements.
The production application catalogue and Trusted proof registry remain closed.

## Checkpoint-four cutover — 2026-10-18

The legacy Python runtime and its active workflow/wrappers are retired. The [current C# cutover record](issue779-csharp-runtime-cutover.md) links the same-product root/worker roles, installed-tool/SDK packaging checks, reusable Linux consumer workflows and exact unchanged full coverage command. The work below remains a dated historical group audit. It must not be counted as a current 57-group pass or a Trusted enablement decision. New native and numerical results must bind the final committed source and are recorded separately.

[Run 39146333953](https://github.com/forge-trust/AppSurface/actions/runs/39146333953) passed the seven
packaged producer/artifact controls and seven application CLI/SDK controls on source
`771d0538347998bdbcff0257fe5089c350ca68bc`. Both artifacts' complete 3,239-file source captures,
immutable Git blobs, hashes and modes matched. The fresh installed CLI and SDK package smoke passed
all 15 commands, including two expected rejections. The application positive cases produced real
9/9 line and 7/7 branch coverage, while negative cases retained their required failure/quarantine
outcomes. These fixture values are not solution or patch coverage. The separate unchanged full gate
was still running at this observation. Subsequent failed-handler cleanup changes require their own
final native and numerical verification; production Trusted registration remains closed.

## Execution boundary

- The supplied `integration-pass2` ledger reports CLI 289 passed, repaired Aspire 41 passed, five isolated lifecycle cases, and 16 control-protocol cases from the predecessor. These are historical run results, not verification of later/current edits. The stored `resume-status.json` is an earlier checkpoint (CLI 289; Aspire 35 passed/4 failed; lifecycle 5; control 16) and predates the reported Aspire repair.
- The five lifecycle cases use an isolated POSIX process-group fixture; the 16 control cases exercise protocol mechanics and explicitly exclude systemd/cgroup/admission. Neither is an actual systemd run.
- A separate actual GitHub Ubuntu 24.04 mechanism run used systemd 255/cgroup v2 and recorded nine lifecycle, twelve subject-boundary, and fourteen allocation cases. Its artifact says `admission: none`; it is mechanism evidence on its recorded older source/checkout, not proof of the current dirty implementation, CLI/Aspire admission, or protected downstream acceptance. See [the observed run record](issue779-consumer-acceptance.md#actual-ubuntu-mechanism-observation-2026-10-02). Local-only captured metadata: `TestResults/issue779-recovery-20261002/actual-ubuntu-850/mechanism-verification.json`.
- Production Trusted provider/platform allowlists remain intentionally empty pending the required acceptance proofs.

### Earlier focused verification

The results in this subsection bind earlier captured revisions. The measured-gap
recovery below records the newer focused checks; neither set is an acceptance
group total or verification of subsequent source changes.

The [execution record](../plans/issue-779-execution.md#single-use-admission-recovery-and-linux-sdk-diagnosis-2026-10-02)
records **81/81** current native macOS Aspire Evidence tests, exit **0**, with no
failures, skips or warnings, including the new protected broker fixture class.
It includes single-use rejection after authentication/admission failure, cancellation
before ownership, concurrent Run/Dispose with a held producer, and disposal before
execution. The current native macOS CLI Evidence filter passed **328/328**, exit
**0**, with no failures, skips or warnings, and compiles the new direct broker cases, including the mode-conflict cleanup regression.
The parent recovered its completed process exit from the runtime command record.
These selections overlap prior
runs and are not acceptance-group totals.

The portable Python launcher controls pass **39/39**, and the runtime proof-driver
controls pass **2/2**, exit **0**. A [new root-broker fixture](../../tests/evidencehost-consumer/ExecutionBroker-README.md)
and paired [CLI](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedCliExecutionTests.cs)/[Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceProtectedAspireExecutionTests.cs)
cases exercise actual consumer entries with synthetic subject results and cgroup metadata.
Their Linux production paths remain unverified. The x64 SDK and direct VSTest attempts
both failed before test results under QEMU on the ARM64 Docker engine. Native Linux
x64 validation is now available through the confirmed AppSurface GitHub Actions
owner. Its first run stopped at a launcher fixture path error before C# or coverage;
the fixture is repaired and a revised run is required. Native macOS controls do
not replace the Linux execution paths.
The unchanged solution coverage gate remains unverified on current source.

The current CLI package `0.1.0-issue779-current.20261002.1` built and installed from
an isolated local feed, both exit **0**. Seven installed-tool checks passed: three
help commands, sample generation, doctor with synthetic `GITHUB_ACTIONS=true`, and
two rejected run modes. Doctor left the trusted envelope unverified; omitted mode
and missing authenticated control rejected with `ASEVD401` and `ASEVD402`, empty
stdout and no output files. These are macOS package checks, not packed SDK or
supported Linux execution acceptance. The local-only completion receipt
`TestResults/issue779-recovery-20261002/current-packed-cli/completion.json` binds the package and source hashes
for that recorded run; it does not verify later source edits.

Statuses below describe test presence and scope, not pass state. “Unit/fixture” means a local test seam or test-owned process; it does not imply a supported runner, protected credentials, or real systemd enforcement.

### Measured-gap recovery and current native boundary

The full 2725-file candidate reached the unchanged solution gate in
[run 37037592365](https://github.com/forge-trust/AppSurface/actions/runs/37037592365):
15269 tests passed, zero failed and two existing tests were skipped. The gate
failed with `ASCOV020` because patch coverage was 82.852% lines and 76.6374%
branches. Aggregate coverage was 94.88% lines and 88.522% branches, within the
existing tolerance. This is failed gate evidence for that frozen candidate.

The next test-only recovery batch uses public or intentionally exposed APIs and
deterministic lifecycle controls. Scoped macOS source builds passed:

- Worker execution: **73/73**, including late completion, stop/join, collector
  failure and cleanup ownership controls.
- Run budgets: **36/36**, with exact-fit, overflow and reserve controls. The first
  source build stopped on a sibling producer test's decimal-threshold compile
  error; the corrected source and successful rerun are recorded separately.
- Restricted producer and Linux artifact root selection: **57/57**. Producer
  declaration, report metadata and optional capacity controls executed. Linux
  artifact syscall cases used their unsupported-platform checks on macOS; the
  actual Linux filesystem behavior remains required.
- Host bootstrap: **42/42**, including registration drift before callbacks,
  partial configuration cleanup, malformed results, completion failure and
  concurrent single-use controls.

Each final command exited zero with no failures, skips, warnings or errors.
These selections are test counts, not acceptance-group totals or a recalculated
patch gate. Ignored source/hash receipts are under
`TestResults/issue779-recovery-20261002/native-validation/coverage-v5b-*-fix/`.

The subsequent complete snapshot failed both
[systemd Observation](https://github.com/forge-trust/AppSurface/actions/runs/37039914626)
and all five [Aspire mechanism controls](https://github.com/forge-trust/AppSurface/actions/runs/37039916803).
The corrected [child fixture](../../tests/evidencehost-consumer/AspireChild/README.md)
now builds without warnings and passes forty portable controls, including bounded
identity diagnostics. Its DCP execution still requires a fresh native run. The
[consumer acceptance record](issue779-consumer-acceptance.md#full-source-gate-and-next-startup-failures-2026-10-02)
records the exact failures and limits. No new Trusted entry or acceptance group is
closed by this recovery batch.

## Group map

### Latest bounded verification (2026-10-03)

The [latest consumer record](issue779-consumer-acceptance.md#restricted-aspire-mechanism-and-production-failure-diagnostics-2026-10-03)
records **5/5 native restricted Aspire mechanism controls passed** on their exact
2736-file candidate. It also records the separate production Observation failure
with `ASEVD211` and incomplete execution; the producer cause remains unknown.
Scoped macOS source tests passed **209 catalogue and 129 application-protocol
cases**, including the selected 128-task bound and rejected 129-task neighbor.
The new root application module and integrated CLI/Aspire shared path still
require native consumer execution. These results do not close all 57 groups or
enable a Trusted provider/platform entry. The complete current-source solution
and patch coverage gate remains required.

| Group | Current test / fixture anchors | Current verification scope | Remaining gap |
| --- | --- | --- | --- |
| A01 | [CLI mode](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceCommandModeTests.cs): `MissingUnknownOrConflictingModeRejectsWithoutEcho`, `ExplicitModeOrLegacyTrueHasOnlyItsDeclaredMeaning`; [CLI entry](../../Cli/ForgeTrust.AppSurface.Cli.Tests/ProgramEntryPointTests.cs): `EntryPoint_EvidenceRunModeBindingRejectsImplicitAndConflictingModesBeforeSideEffects`; [Aspire caller](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAdmissionCallerTests.cs): legacy run cases | Partial: CLI and Aspire rejection fixtures exist; current tree unverified. | No paired caller matrix proving every rejected mode precedes all CLI and Aspire callbacks. |
| A02 | [Caller ownership](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAdmissionCallerTests.cs): held-producer concurrent Run/Dispose, disposal-before-run, and failed-admission retry; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): shared disposal | Unit cases passed in the 76-case focused run. The host consumes one attempt before authentication/admission; the broker rejects ready after stop/wait/exit in handler controls. | The paired production same-host/control retry case remains unverified on native Linux; real supervisor ownership remains a separate proof. |
| A03 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): missing/mismatched supervision; [paired broker callers](../../tests/evidencehost-consumer/ExecutionBroker-README.md): actual root peer, PID/UID/GID and declared policy | In-memory controls and portable exact-peer checks pass in their focused suites. Direct broker caller cases are present, not yet verified on Linux. | Actual launcher-to-production-caller proof with independently armed systemd supervision remains required; the root-broker fixture deliberately uses a synthetic cgroup. |
| A04 | [Worker unit](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `ExecuteAsync_SynchronousPreTaskStallDoesNotBlockDeadlineControl`; [isolated fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): `test_synchronous_stall_before_task_return_uses_failfast` | Partial: isolated subprocess/watchdog behavior is covered; five older fixture cases were reported passing. | Exercise configure, verifier, and factory stalls through the current launcher with an independent outer watchdog. |
| A05 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): `CoherentHeadSubstitutionOrHashDriftCannotAdmit`; [planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): conservative resolution and rename cases | Present: unit fixtures cover head substitution/hash drift and conservative planner behavior. | Two-checkout protected-base and same-diff admission remains external; current tests unverified. |
| A06 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): `CliWorkflow_ShouldRejectUnresolvableSnapshotsAndMismatchedPlanBindings`, `EvidenceDiffSnapshot_ShouldDefensivelyCopyBytesAndRejectInvalidDigests` | Present: snapshot, binding, and changed-input unit cases. | No current protected caller run proving exact source snapshot is the one admitted and executed. |
| A07 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `Registration_ShouldRequireOneDistinctEntryForEachExplicitCapability`, `RunSharedCore_RejectsMismatchedCompleteRegistrationBeforeReadinessOrProducer`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): exact assertion case | Partial: distinct/mismatched registrations and exact assertion fixture exist. | Full missing/extra/duplicate/stale replacement matrix is not evidenced as a current executed suite. |
| A08 | [Protected inputs](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedWorkerInputsTests.cs): `AcceptedConsumerProof_RemainsFalseForValidlyShapedDescriptor`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): `TrustedRequiresAcceptanceAndExactlyMatchingRegisteredAssertion` | Partial: local descriptor and acceptance matching fixtures; current proof deliberately remains false. | No protected consumer CI proof binding run, workflow, base/head, roots, and capability digest to a trusted descriptor. |
| A09 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): Observation/verifier and producer-class cases; [Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): dependency-free success and resource-backed rejection | Partial: Observation verifier bypass and resource restriction have unit coverage. | Complete projection, privilege, release, and undeclared-subset matrix across both public entries. |
| A10 | [Runtime subject fixture](../../tests/evidencehost-consumer/RuntimeSubject/Program.cs): `Subject_reads_its_declared_input_and_cannot_read_or_modify_protected_state`; [consumer record](issue779-consumer-acceptance.md) | Partial: older systemd mechanism run recorded 12 subject-boundary assertions; no current protected consumer execution. | Actual protected-secret map and disposable subject credentials must be demonstrated in the consumer workflow. |
| B01 | [Linux root unit](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceLinuxArtifactRootTests.cs): retained-handle, collision, parent-substitution, link, and hardlink cases; [Linux fixture](issue779-csharp-runtime-cutover.md#retired-runtime): allocation cases | Partial: unit fixtures plus 14 allocation cases in the earlier actual Ubuntu mechanism artifact. | Re-run against current allocation/writer path and admitted parent on each supported platform. |
| B02 | [Aspire ownership](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAspireApplicationOwnershipTests.cs): `StartOwned_ClosesAfterBuildWithoutLosingOwnership`, `StartOwned_FailedBuildDoesNotRegisterOrStart`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): activation completion | Partial: build/cancel and activation unit fixtures are present, unverified on current tree. | Explicit both-order allocation-versus-cancel race with no resource or subject activation after rejection. |
| B03 | [Aspire ownership](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAspireApplicationOwnershipTests.cs): `StartOwned_RegistersBeforeStartingAndRetainsLeaseUntilCleanup`, `StartupFailure_JoinsWorkBeforeDisposingPartiallyStartedApplication` | Partial: startup ownership fixtures exist; repaired Aspire 41-pass result is historical. | Re-run latest host changes and prove admission/binding precede real factory/build/start in consumer execution. |
| B04 | [Runtime subject fixture](../../tests/evidencehost-consumer/RuntimeSubject/Program.cs): denied tooling/output/control access and declared-input read | Partial: subject-boundary fixture and older actual Ubuntu mechanism assertions; not current production worker/subject integration. | Prove current restricted subject cannot load trusted code or alter final output under the actual protected consumer job. |
| C01 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `RunAsync_ShouldProtectResourceOrderingAndRequiredArtifactIntegrity`, `RunAsync_ShouldProtectLifecycleBoundsSharedDependenciesAndCallerCancelledProducers` | Present: ordering, shared dependency, cycle/bounds, and artifact cases are represented in unit fixtures. | Current exact suite execution remains unverified. |
| C02 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): resource deadline, unavailable-resource, caller-cancel, and producer-failure cases | Present: readiness success/failure/cancel/deadline paths are represented in unit fixtures. | Confirm every readiness failure closes producer launch in the latest joined lifecycle. |
| C03 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): explicit producer registration, failed producer, and invalid output cases; [manifest](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): missing required artifact | Partial: producer outcomes/artifact obligations are tested locally. | Direct spoofed subject pass/known assertion versus registered producer outcome through the restricted producer path. |
| C04 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): claim tampering and missing required artifact; [gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): failed producer/open obligation | Partial: invalid claim and obligation cases exist. | Full stale, extra, duplicate, and mapping-mismatch result matrix has no current run evidence. |
| C05 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): `ArtifactWriter_ShouldRejectUndeclaredDuplicateAndOversizedWrites`, `ArtifactWriter_ShouldReleaseItsReservationWhenWritingFails`; [quotas](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs) | Partial: slot/media/size and reservation unit cases; current aggregate integration unverified. | Cross-producer aggregate limit and required-slot closeout through collection. |
| C06 | [Linux root](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceLinuxArtifactRootTests.cs): name/size, namespace drift, symlink, hardlink, and replacement cases; [launcher fixture](issue779-csharp-runtime-cutover.md#retired-runtime): path rejection cases | Partial: Linux unit/fixture cases and older 14-case systemd-run artifact. | Windows/macOS reparse/path semantics are absent; current Linux code has not been proved by a new systemd run. |
| C07 | [Writer](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): reservation release; [budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): concurrent reservation; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): joined write | Partial: reservation and join fixtures are separate. | Concurrent retained writers racing finalization, including failed-write release, need one lifecycle assertion. |
| C08 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `RunAsync_ShouldInvalidateAProducerWhenItsWrittenArtifactChangesBeforeCollection`; [Linux root](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceLinuxArtifactRootTests.cs): retained descriptor verification | Partial: mutation and retained-handle unit cases exist. | Prove bounded final hashing uses the originally opened handle through current writer/collector integration. |
| G01 | [Gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): targeted, no-evidence, and release cases; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): accepted Trusted assertion | Partial: claim shapes and closed-obligation rules have unit coverage. | Production Trusted remains disabled; no accepted current protected run or packed consumer proof. |
| G02 | [Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): dependency-free Observation and resource rejection; [gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): Observation rejection | Present: informational Observation and ineligible-gate controls are represented locally. | Current exact tree unverified; no consumer fork Observation execution. |
| G03 | [Gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): `Verify_IsStructuralAndDoesNotMakeAForgedAssertionGateEligible`; [JSON](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): canonical bytes | Partial: digest/structural verification limits are tested locally. | No protected downstream proof that Verify cannot confer runtime or uploaded-pair authority. |
| G04 | [Protected gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): expected-fact, run, receipt, and claim rejection cases | Partial: unit gate fixtures cover mismatches; no actual protected gate workflow run. | Exercise current-run/base identities, stale receipt, missing manifest, and failed proof in consumer-owned CI. |
| G05 | [JSON input](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): byte limits, duplicate/case-colliding keys, versions/enums, additive fields; [protected input](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedWorkerInputsTests.cs): bounded policy/diff | Partial: generic bounded/schema unit fixtures and protected policy/diff fixtures exist. | Verify assertion JSON and both actual entry callers reject before parsing/allocation; latest additions unverified. |
| G06 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): `CliWorkflow_Summaries_ShouldExplainTargetedObservationAndReleaseClaims`; [gate](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedGateTests.cs): release opt-in | Present: release opt-in and summary claims have unit coverage. | No current consumer output/docs run confirming registered ID/version/scope without correctness overclaim. |
| U01 | [CLI workflow](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): starter creation, overwrite, malformed inputs; [doctor](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceDoctorTrustTests.cs): protected facts remain unverified; local-only installed tool receipt: `TestResults/issue779-recovery-20261002/current-packed-cli/completion.json` | Current macOS installed CLI generated a starter and rejected implicit mode/missing control before output. Synthetic `GITHUB_ACTIONS=true` left trust unverified. | Supported Linux live execution and fresh-user usability remain required. |
| U02 | Local-only exact-version CLI receipt: `TestResults/issue779-recovery-20261002/current-packed-cli/completion.json`; local-only packed SDK fixture snapshot: `TestResults/issue779-recovery-20261002/packed-sdk/consumer/Program.cs.fixture` | Current CLI package install/help/starter and negative mode/control checks passed on macOS; the SDK snapshot is not a verification result. | Packed SDK proof and supported Linux execution against the exact current candidate remain required. |
| U03 | [Paired broker callers](../../tests/evidencehost-consumer/ExecutionBroker-README.md); [CLI mode](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceCommandModeTests.cs); [Aspire caller](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceAdmissionCallerTests.cs); [shared admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs) | Paired production-entry Observation/rejection cases are present; current native macOS controls do not execute their Linux paths. | Run paired cases on native Linux, then prove equivalent admission/claim behavior under the actual protected consumer supervisor. |
| U04 | [Doctor](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceDoctorTrustTests.cs): environment-value secrecy; [coverage](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageEvidenceProducerTests.cs): nonfatal failure secrecy; [Aspire](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): stable diagnostics | Partial: selected canary/diagnostic paths are covered locally. | End-to-end diagnostic code/stage/cause/fix/link/exit/manifest secrecy matrix remains unverified. |
| U05 | [Linux fixture](issue779-csharp-runtime-cutover.md#retired-runtime): bounded wait/handler settlement; [lifecycle fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): owned-process stop cases | Missing: no same-root rapid retry/concurrent allocation recovery fixture identified. | Prove retry only after owned exit with a new root; reject concurrent reuse of the old root. |
| U06 | [CLI mode](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceCommandModeTests.cs): explicit/legacy meaning; [JSON](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): supported and rejected versions | Partial: local migration/schema fixtures exist. | Packed old bool/alias/enum-builder compatibility and no automatic Trusted need exact-version consumer proof. |
| U07 | No manual acceptance trace found. | Missing: no fresh-user planning and live-execution record. | Record the declared planning endpoint separately from actual supported execution. |
| U08 | [Starter workflow tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): starter and summary cases; no issue-779-specific snippet assertion identified | Missing: generic snippet infrastructure is not evidence that these docs match the built CLI. | Verify claim tables, snippets, canonical links, generated starter, and non-attestation wording against the packed version. |
| R01 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): conservative fallback, precedence, rename, unknown path, limits | Present: regression cases cover the plan's stable selection rules. | Re-run current planner changes; current run status is unverified. |
| R02 | [Coverage adapter](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageEvidenceProducerTests.cs): planning snapshot/numeric gate; [restricted producer](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRestrictedCoverageProducerTests.cs): subject result and numeric gate | Partial: unit coverage exists; the reported 11 coverage-runner tests are historical. | Packed-consumer proof that coverage gains no mode-specific alternate trust. |
| R03 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): `DisposeAsync_ShouldDisposeRegisteredResourcesAndProducersOnce`, cleanup failure; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): shared disposal | Present: deduplication, repeat/shared disposal, and cleanup-failure fixtures exist. | Re-run the repaired/current suite; no cleanup success may be inferred from an older pass. |
| R04 | [JSON](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs): canonical V1 bytes and additive fields; [shape](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonShapeTests.cs): nested round-trip | Present: canonical and compatible-shape unit fixtures exist. | Current golden output and supported-schema matrix have not been reverified. |
| R05 | [Planner](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): marked starter, unmarked overwrite refusal, malformed inputs | Present: requested starter/overwrite paths have local unit tests. | Current CLI suite unverified; no change needed to the retained test intent. |
| R06 | [Protected input fixture](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProtectedWorkerInputsTests.cs): shaped descriptor stays unaccepted; [acceptance record](issue779-consumer-acceptance.md#versioned-support-allowlist) | Missing: no matching provider/platform/build/proof matrix enables Trusted. | Keep all production allowlists empty until immutable current acceptance entries exist; Windows/macOS proofs are absent. |
| L01 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): cooperative cancel and original cause; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): caller cancellation | Partial: cancel/deadline latch cases are present in unit fixtures. | Demonstrate first-cause preservation at every admission/start/readiness/producer/collection boundary in current lifecycle. |
| L02 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `ExecuteAsync_LatePassedResultDuringGraceCannotUpgradeTimeout`; [admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): late pass latch | Present: deterministic late-pass/grace cases exist. | Current test additions remain unverified; include completion on both sides of the deadline. |
| L03 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): joined in-flight write and terminal registration closure; [output quota](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProcessOutputQuotaTests.cs): drain on overflow | Partial: selected callback/write/pump paths have unit or isolated process fixtures. | One current lifecycle test must show every callback, child, pump, and write settled before disposal. |
| L04 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): active-stage cancellation/cleanup; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): cooperative stop | Partial: cancellation reserve mechanics are represented. | Assert fresh stop-token usability and grace bounded by remaining cleanup/job time. |
| L05 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `StopAndDisposeAsync_ExposesJoinAndCleanupBeforeCollection`; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): reverse/deduplicated disposal and cleanup failure | Present: joined cleanup ordering and failure-manifest unit paths exist. | Current exact suite and real consumer collection remain unverified. |
| L06 | [Isolated fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): synchronous stall, ignored cancel, blocked write/pump, stuck disposer, cooperative timeout; [worker unit](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): fatal path | Partial: five older isolated POSIX-process cases passed; they are not a systemd runtime. Earlier systemd mechanism run is separate and admission-free. | Run the current worker/launcher under the actual systemd supervisor; fatal cases must have no dispose/hash/manifest markers. |
| L07 | [Worker unit](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): `ExecuteAsync_ChildWithoutExitAcknowledgementUsesFatalPath`; [Linux mechanism record](issue779-consumer-acceptance.md#actual-ubuntu-mechanism-observation-2026-10-02) | Partial: unit fake models missing child acknowledgement; earlier actual systemd mechanism artifact exists but does not admit current production integration. | Link a current systemd case proving active/unacknowledged descendants keep output quarantined. |
| L08 | [Worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): stalled disposer blocks the next; [isolated fixture](../../tests/evidencehost-consumer/test_lifecycle_worker.py): `test_nonsettling_disposer_uses_failfast`; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): cleanup fault | Present: unit and isolated process cases cover stuck/throwing disposal. | Current worker revision and systemd fatal termination remain unverified. |
| L09 | [Bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): critical/nonfatal producer failures; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): unjoined fatal path | Partial: unit exception/fatal handling is represented. | Prove fatal runtime exception through current entry produces no success manifest. |
| L10 | [Admission](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceAdmissionTests.cs): canceled/unknown modes; [bootstrap](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs): single-use and caller cancel | Partial: separate cancellation and single-use cases exist. | One entry/lease matrix for cancel-before, during admission, and after terminal result is not evidenced. |
| P01 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): exact limit, overflow latch, concurrent reservations; [writer](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidencePlannerTests.cs): oversized writes | Partial: quota boundary and concurrency are unit-tested. | Prove the 256 MiB run-wide reservation is shared by every producer through collector closeout. |
| P02 | [Output quota](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProcessOutputQuotaTests.cs): received-byte accounting, shared streams/commands, overflow drain; [coverage](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageEvidenceProducerTests.cs): buffered coverage output | Partial: 1 MiB prefix/16 MiB aggregate logic has unit fixtures; current run unverified. | Actual child output must count discarded/streamed bytes on both paths under the current launcher. |
| P03 | [Output quota](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceProcessOutputQuotaTests.cs): `ProcessRunner_OverflowCancelsAndDrainsChildBeforeReturning`; [worker](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceWorkerExecutionTests.cs): quota failure closes registration | Partial: overflow and stop/join are tested separately. | End-to-end overflow must latch failure, join, dispose, and prevent an eligible truncated result. |
| P04 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): exact-fit, one-tick-short, invalid settings, and cleanup reserve | Present: deterministic budget unit cases exist. | Current caller must reject the summed job budget before invoking any callback. |
| P05 | [Budgets](../../Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceRunBudgetTests.cs): monotonic elapsed time, later-stage recheck, concurrent start, cancellation | Present: wall-clock shift and reservation unit cases exist. | Re-run latest lifecycle integration and prove no concurrent reservation extends the job. |

## Latest exact-source mechanism failures

Frozen snapshot `8a964188e85561d57119133d26b552f9412e2445` failed both
[systemd Observation](https://github.com/forge-trust/AppSurface/actions/runs/37047555055)
and [Aspire mechanism execution](https://github.com/forge-trust/AppSurface/actions/runs/37047560583).
All 2725 source hashes, dispatched workflow bindings and downloaded artifact
digests matched. Runtime readiness and wait completed, but output allocation or
activation reported `ASEVD409` without its underlying exception; no final
manifest or runtime proof was produced. Aspire passed native restore/build,
then failed all five controls: four resource startups failed during thread
creation, and the factory-stall identity guard rejected an observed root UID.
No healthy resource or valid readiness-failure control was established.

These results leave the native groups incomplete. The next diagnostics preserve
closed allocation facts, completed executable startup and bounded cgroup task
and memory counters. See the [exact observation and its limits](issue779-consumer-acceptance.md#frozen-source-allocation-and-resource-startup-failures-2026-10-02).
The [unchanged solution coverage run](https://github.com/forge-trust/AppSurface/actions/runs/37047553718)
also exited **1**, with `ASCOV120`: its fixture-path policy rejected dynamic
`Path.Join` in the Bootstrap artifact test. The solution recorded 15,393 passes,
one failure and two existing skips; 53 of 54 projects exited zero, with no
compiler warnings or errors. Measured aggregate coverage was 94.98% line and
88.79% branch, but the patch gate was not reached. The local correction uses
`TestPathUtils.PathUnder`; a fresh unchanged native gate remains required.
All 57 groups and the protected CLI/Aspire positive remain required.

## Five highest-value remaining gaps

1. **Protected consumer acceptance:** run the real AppSurface protected/fork workflow through CLI and Aspire admission, restricted subject, and current-run downstream gate (A10, B04, G04, U03). Existing unit/fixture cases do not prove protected secret mapping or current run/base binding.
2. **Current admission-to-ownership integration:** connect a real armed supervisor and protected descriptor to both callers, then cover cancellation/allocation races in both orders (A03, A08, B02). Present lease fixtures are not a production launcher proof.
3. **Current systemd lifecycle integration:** exercise the current worker under the actual systemd/cgroup supervisor for stuck callback/write/pump, child acknowledgement, stop/join, and no-finalization behavior (A04, L03, L06, L07, P03). The five passing POSIX process-group fixture cases and older nine-case Ubuntu mechanism record are distinct evidence.
4. **Trusted allowlist acceptance:** add immutable matching provider/platform/build/proof records before registering Trusted (R06). Linux mechanism evidence alone is insufficient; Windows and macOS remain unproved, and production allowlists are intentionally empty.
5. **Consumer usability proof:** run exact-version packed CLI/SDK migration checks and the fresh-user/manual plus docs/snippet/starter trace (U02, U06, U07, U08). No matching current acceptance result was found.

## Frozen v13 result update (2026-10-03)

These observations use immutable source
`6518b17e1bd587143314cfa2ea1a73e8113ab9cf`; they supplement the history above
without marking additional acceptance groups complete. See the
[full result and scope record](issue779-consumer-acceptance.md#frozen-v13-actual-results-and-pending-corrections-2026-10-03).

| Actual run | Observed result | Acceptance limit |
| --- | --- | --- |
| [Aspire mechanism-v9](https://github.com/forge-trust/AppSurface/actions/runs/37083994959) | **5/5 controls passed**; parent verified artifact digests, all 2740 source bindings, per-case owned exit and cleanup. Factory stall and stuck descendant required forced termination. | Proves the [restricted child fixture mechanism](../../tests/evidencehost-consumer/AspireChild/README.md). Shared CLI/Aspire, the [root application module](../../tests/evidencehost-consumer/LinuxApplication-README.md), protected downstream acceptance and Trusted positive remain unproved. |
| [Native-v11](https://github.com/forge-trust/AppSurface/actions/runs/37083997761) | Launcher/proof/broker suites passed **91/46/4** controls; root portable watchdog-oversize control failed on a Linux connection reset. | Focused C# verification and the [unchanged coverage gate](../../scripts/coverage-solution.sh) were not reached. |
| [Runtime-v15](https://github.com/forge-trust/AppSurface/actions/runs/37083999238) | `worker-unsuccessful`; retained manifest: failed producer, approximately 10 ms, `EvidenceAdmissionException`, None/None/Incomplete, cleanup confirmed. | No successful production Observation or [shared producer](../../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md) result. Fresh-results directory ownership is a source-confirmed defect; the native inner cause remains unmeasured. |

### Subsequent correction results

[Native-v12](https://github.com/forge-trust/AppSurface/actions/runs/37087508180)
tested correction source `9894c62c525724b0d62c9f4859faa20b464ac737`:
all **310 preceding Python controls** passed, followed by **914/915 CLI tests**.
The root-peer diagnostic assertion failed; the solution coverage gate was not
reached. [Runtime-v16](https://github.com/forge-trust/AppSurface/actions/runs/37087509927)
retained a completed restricted process exit **1** after **95,410 ms**, with no
report artifact and confirmed cleanup. Its underlying subprocess cause remains
unmeasured. Both artifact digests and post-run source checks were verified.

The [handshake diagnostic correction](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#authenticated-outer-worker-descriptor)
preserves the root-peer `ASEVD402` before broader exception normalization.
Its scoped macOS source build passed **124/124** tests without compiler
diagnostics; the real Linux peer path still requires native execution.
A private latest-main gate is pending. The production catalogue and accepted-proof
registry remain closed; no Trusted support is enabled by this update.
