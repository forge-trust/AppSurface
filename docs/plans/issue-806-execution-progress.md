# Issue #806 execution progress

Goal: implement the approved [Durable worker template](../designs/issue-806-durable-worker-template.md) through a validated draft PR.

Branch: `codex/make-it-so-durable-worker-template`. Base: `origin/main` at `7ae38084`, including the landed external activation service, schema-11 preflight fixes and current release infrastructure. Pre-existing plan/TODO changes were preserved; the TODO stash is retained as a recovery copy.

## Scope and owners

- Starter/package: standalone application-owned host, typed Work and passive composition, template manifest and pack project.
- Generated tests: published-package-only deterministic host scenarios and real first-Work lifecycle/export proof; repository template verification.
- PostgreSQL asset: canonical recipe source/archive/direct-consumer output byte identity.
- Documentation: canonical first-use guide, generated/package docs and shape/manual upgrade guidance.
- Package contract: bounded manifest/archive/generated inventory and exact dependency shape.
- Native smoke: private native PostgreSQL cluster acquisition and owned process/root cleanup.
- Parent integration: version staging, local-only artifact consumer workflow, evidence/publication binding, OS/release matrix, coverage, enhancement and QA.

## Required validation

`COVERAGE_GATE=./scripts/coverage-solution.sh`

The unchanged repository gate requires aggregate 95% line/85% branch and the configured patch 95% line/85% branch, against `origin/main` with Codecov line semantics. It requires a non-sandboxed runner by default. Run it on final current code, retain output/commit/tree identity, and repeat after coverage-affecting changes before shipping.

Other required validation: exact-nupkg and feed native template installation, independent authored/generated restore/build/format/non-Docker tests, generated PostgreSQL first-Work checkpoints, canonical recipe byte chain, ordinary native OS startup, provider/prior-binary/preflight regression, meaningful failure/security/lifetime tests, five independent primed/cold clocks, docs/snippet/package-index gates, Standard user-facing QA, bounded enhance review, formatting and warnings.

## Current evidence

- .NET SDK 10.0.102 and Docker daemon available locally.
- GitHub repository authorization available; original case #806 remains open.
- Feature branch established from refreshed main; no conflict in preserved TODO change.
- Earlier macOS private-cluster experiment was narrow feasibility only; no implementation credit.

## Remaining coordination

- Representative Skoolit lane/evidence owner/report location requested from the user. Implementation proceeds while pending.
- Doctor #801 coordination must reflect current landed or case-owner disposition before #806 completion; inspect current main before assuming it remains open.
- Actual Linux/macOS/Windows candidate and timing evidence remain required. No green result is inferred from planned jobs.
- No PR, coverage pass, generated proof pass, or implementation facet completion has been claimed yet.

## Resumed integration checkpoint — 2026-10-04

- Six native subagents retain disjoint ownership: Peirce starter; Poincare generated fixture; Mill SQL asset (finished) then consumer-proof tests; Bernoulli docs; Lovelace artifact contract/tests; Hubble native PostgreSQL ownership/process helper.
- SQL pack-to-clean-direct-consumer byte identity test passed (1/1); targeted format/diff check passed. Template pack project builds with zero warnings.
- Local diagnostic pack produced nine relevant first-party archives at `artifacts/durable-template-local`, current authored preview13; this dirty-tree diagnostic bundle is not release evidence. The first inventory check correctly rejected incomplete current content; do not claim generated proof passed.
- Parent implemented `DurableTemplateConsumerProof`, deterministic staging of csproj/CPM versions, command dispatch, safe schema-v1 receipt, source/complete archive/content identity, and publisher validation before credential read. Receipt tests pass37/37 after correcting the test's macOS temp-path alias.
- Parent added blocking reusable three-OS template workflow and exact-ID receipt downloads to package/prerelease/stable workflows. Real native smoke callback is connected; generated native test is still being authored. No CI result exists yet.
- Doctor#801 remains OPEN (queried current GitHub). Human trial/adopter lane/owner/destination async question remains pending; do not infer any answer.
- Coverage has not run. Initial solution no-restore build showed missing assets; restore is required before full validation. No commits/push/PR yet.
- Remaining: finish/reconcile shape and actual generated fixture/host tests; source/generated real candidate proof; native tool identities/90s combined setup and20s total cleanup; failure/security/coverage tests; five serial primed/cold clocks; public replay; enhancement; Standard user-facing QA; full coverage gate; outside human/adopter/doctor evidence; format/ship draft.

## Integration and first coverage checkpoint — 2026-10-04

- Artifact contract: focused 26/26 tests and native hash generation passed. The parent compares the generated-content hash with the exact archive and permits a signing envelope only for separately labeled public replay.
- Consumer orchestration: focused 46/46 deterministic cases passed, including replacement failures, graph substitution, local-feed isolation and cleanup. Real installed consumers are still being verified. Custom template creation no longer uses the unsupported `--no-restore` option.
- Timing policy: focused 15/15 validator tests passed. The runner executes the exact three commands in five serial independent roots; a private fixture marker enforces the 55-second primed preparation group and the full root watchdog remains active. Failed samples are retained with finite JSON values. The publisher reevaluates samples and summaries using the recorded clock frequency against the independently expected full producer archive set. Both primed and cold receipts are now mandatory before credentials.
- Release evidence: focused 75/75 tests passed for current reader/validation changes. The reusable workflow now includes three native OS jobs plus two Linux timing jobs, uses five private daemons for cold samples and downloads all five receipts by immutable artifact ID. Existing workflow transport tests passed 31/31; package-index and affected Markdown snippet verification passed. These are source/test checks, not executed Actions evidence.
- Fresh local diagnostic packs produced nine archives at `artifacts/durable-template-local`. They use dirty-tree preview13 source and are not release provenance. An isolated installed-consumer diagnostic is running; official-source PostgreSQL16.5 tools were built under a private temporary root for local smoke testing.
- Exact unchanged coverage gate attempt1 is running unsandboxed, with monitoring delegated and output in `/tmp/issue806-coverage-attempt1.log`. Intentional new source was staged so patch coverage includes it. Final code/test changes require a fresh gate; no coverage pass is claimed.
- Remaining: resolve actual generated fixture copy/cancellation behavior and host-test lifetime failures; final source/staged artifact proof; native adapters/meaningful changed-code coverage; complete enhancement, clean baseline and Standard functional QA; actual OS/timing Actions receipts; approved developer trial, named representative Skoolit certificate and doctor#801 case-owner disposition. No commit, push or draft PR exists yet.

- Actual local installed-consumer diagnostic passed 30 phases: independent authored, exact archive and feed install/restore/build/format/non-Docker tests, sample replacement and owned cleanup. Safe receipt `/private/tmp/issue806-consumer-diagnostic.json`. This macOS diagnostic did not run FirstDurableWork/native and uses a dirty candidate bundle, so it does not authorize publication.
- Actual macOS native diagnostic failed safely at `native-smoke` and still cleaned the consumer root. Narrow helper replay showed initdb exits0 with observed process/pipe termination; the subsequent helper incorrectly rejected initdb's normal `postgresql.auto.conf`. The native owner is correcting this with a regression. No native success is claimed.
- Executable template workflow transport tests pass for complete five receipts and missing/expired/duplicate/prior-attempt failure cases. Candidate LF checkout is now explicit across platforms.

## Fresh-binary and runner checkpoint — 2026-10-04

- Recovered current approved plan and continued the existing goal/branch. No PR exists; main is now refreshed.
- The generated startup contract stall is runner-specific: the exact single test passes unsandboxed in152ms, and the full template repository verifier passes70/70 with fresh coverage in6.29s. Sandbox runs stalled before WebApplication.CreateBuilder. No hosted-service defect inferred.
- Fresh Durable PackageIndex focus passes304/304 with coverage at `/tmp/issue806-durable-focused-current/a755e92a-168e-4f14-9e4c-2ceb43498f95/coverage.cobertura.xml`. CLI success-fixture repair is separately pending.
- Template pack/proof orchestration tests now pass3/3 after including the mandatory Web manifest entry and a narrow fixture payload audit; private staging/pins, failed-proof manifest withholding and pack-failure cleanup are exercised.
- Timing/evidence owner passes150/150: timing workflow100%line/100%branch, release evidence98.54%/90.56%, timing proof99.60%/89.78% in the owner's isolated report. Native helper tests pass35/35.
- Real Docker first-Work and opt-in acquired native PostgreSQL proofs are being rerun unsandboxed. No passing proof or final coverage gate is claimed.
- Fresh GitHub query confirms doctor#801 OPEN. Outside-checkout human/adopter owner and destination question remains pending; no downstream certificate is inferred.

- Unsandboxed generated Docker diagnostic now PASS: FirstDurableWork1/1,4.48s total; all four exact checkpoints and cleanup158ms. Fresh fixture copied into earlier installed generated root, so this is actual behavior evidence but not an immutable/current archive certificate. Log `/tmp/issue806-firstwork-unsandboxed.log`.
- Acquired PostgreSQL16.5 native helper diagnostic PASS: intended opt-in test1/1,1.90s, successful auth and owned cluster/root cleanup. Log `/tmp/issue806-real-native-unsandboxed.log`. Missing-filter earlier run receives no credit. Native ordinary generated-host callback and actual three-OS matrix remain separate obligations.

## Current review and consumer checkpoint — 2026-10-04

- Exact archive/path/feed installation, isolated authored/generated restore/build/format/non-Docker tests, SQL identity and three-location replacement passed. Real Docker first-Work and replacement-first-Work both emitted all four assertions and cleaned owned roots; corrected fixed test namespace prevents the documented filter selecting native smoke. Safe diagnostic receipt: `/private/tmp/issue806-current-consumer-docker.json`.
- Native PostgreSQL16.5 ordinary-host startup also passed in a separately labeled local proof (`/tmp/issue806-current-consumer-native-complete-tools.json`); hosted three-OS evidence is still unexecuted.
- Enhancement fixed case-insensitive NuGet template-selection enforcement, full cache-tree hashing, public replay cleanup, initdb cancellation/drain ownership, malformed ZIP safe projection and the test filter collision. The second independent review adds hosted runner image revision binding and rejects missing identity before publication.
- Focused current validation before the image fix: template orchestration457/457, repository/host72/72, native/publisher73/73, executable workflow transport2/2; repository formatting and diff check passed. Final image-boundary verification is pending.
- Coverage attempt2 ran all56 projects and failed only two package visual baselines; those baselines were refreshed and rechecked. Aggregate95.06%line/89.01%branch remains diagnostic because ASCOV120 stopped before patch measurement and code changed afterward. [Attempt2 analysis](issue-806-coverage-attempt2-analysis.md) records the limits. A later prematurely started run was interrupted as stale and has no gate verdict. The unchanged exact gate is still required.
- No push or PR has occurred. Standard functional QA requires a clean committed baseline. Actual OS/timing receipts, outside-checkout developer trial, named Skoolit evidence lane/owner/report and doctor#801 disposition remain open; they cannot be replaced by the local template result.

## Baseline, functional QA and fourth coverage attempt — 2026-10-04

- Baseline `12d048d8b5dc49eb64ee403c2745a9a8321bf5d2` contains the implementation and prior review fixes. The independent runner-image remedy recheck found no actionable finding in that narrow scope. It does not certify the unexecuted hosted matrix.
- Standard Full CLI/API/worker QA exercised native template installation, project creation, the exact first-Work filter with Docker, native ordinary host startup, malformed input, archive substitution, missing publication receipts, uninstall and short-name absence. Safe evidence and the report are retained at `/private/tmp/issue806-qa-12d048d8/report.md`. All four first-Work assertions were observed. Browser QA is inapplicable to the generated worker.
- QA exposed reusable .NET build servers surviving nested proof execution. The consumer verifier now disables node reuse, the MSBuild server and shared compilation in its owned child environment. A regression checks every proof command. Fresh end-to-end verification of this fix remains required; the earlier successful capture used the same settings supplied externally.
- The unchanged coverage attempt4 completed all56 projects:55 passed, one PackageIndex port assertion failed after it released ownership of the ephemeral port. Aggregate coverage was95.07% line/89.00% branch; ASCOV120 stopped the script before the gate. A separate diagnostic measured89.21% Codecov patch line/87.06% patch branch. Neither is a gate pass. See [attempt4 analysis](issue-806-coverage-attempt4-analysis.md).
- The next focused enhancement holds an owned bound socket for the listening probe and adds meaningful archive, staging, native ownership/authentication/version/log/marker and generated-host failure tests. The acquired PostgreSQL16.5 tools are available as a real prerequisite for the existing opt-in OS-adapter test. No coverage policy changes are authorized.
- The required append-only release entry is present. Its five release-page screenshots were refreshed and both affected visual tests passed without the update flag; unrelated raster noise was discarded after pixel comparison.
- A private [developer trial bundle](/private/tmp/issue806-developer-trial-12d048d8.zip) contains the exact local candidate feed, hashes, isolated NuGet mapping and instructions. This prepares the human handoff; it is not evidence that a person completed the trial. Human/adopter/doctor coordination remains unanswered. No push or PR has occurred.

## Final local candidate checkpoint — 2026-10-04

- Integrated focused PackageIndex validation passed706/706 and the complete template repository/host suite passed79/79. New tests cover archive inventory/inflation/staging, native setup/authentication/version/PID/process/log/marker/root cleanup, child environment isolation, CLI input and real native-tool prerequisites, cache bounds, serial timing identities and release bindings. The final targeted suite passed618/618; the last timeout-disappearance repair separately passed2/2. Formatting and `git diff --check` passed.
- The full2,017-case PackageIndex run passed2,012, skipped three existing manual/filesystem cases and failed only two Git-clone fixture setups with `No space left on device`. Task-owned disposable caches, stale clone and native build objects were removed while preserving source, installed tools and safe reports. The interrupted archive was discarded and rebuilt; ZIP CRC checks and current HostContract source-byte equality passed. See [coverage analysis](issue-806-coverage-attempt4-analysis.md).
- The two-shard failed-run diagnostic improved Codecov patch line to94.7784% and branch to93.2045%. It is not the required fresh56-project solution gate. The configured95/85 thresholds and0.5-point tolerance remain unchanged.
- Three scoped independent review passes found and repaired a test timeout race and retained test root; the final static recheck reports no remaining actionable finding in those remedies. It does not establish final coverage or user-facing QA. The source and tests are now frozen for a clean QA baseline, final native CLI replay and unchanged solution gate.
- The final private trial feed contains nine exact archives and recalculated producer hashes. Its human instructions/source revision will be bound to the clean baseline. The outside-checkout human assignment remains required before completing the OS matrix. Creating a draft PR currently triggers that matrix, so draft authorization does not by itself resolve the approved ordering. Skoolit before-release and doctor before-case-closure obligations remain distinct. No push or PR has occurred.

## Current prerequisite blocker — 2026-10-04

- Clean baseline `e81c756c` passed actual final native CLI QA with all three outer build-server isolation settings unset:30 phases, native PostgreSQL16.5, authored/archive/feed compilation/format/tests, exact graph/content/SQL binding, ordinary startup, sample replacement and cleanup. The unchanged trial install command using implicit NuGet.config and standalone creation also pass. [Final QA report](/private/tmp/issue806-qa-e81c756c/report.md) retains all nine captures with overall BLOCKED verdict; both private template installations were uninstalled and absence verified.
- The actual Docker quickstart selected one test and failed safely during PostgreSQL fixture setup. No final Docker checkpoint is credited. Docker Desktop's log confirms host disk exhaustion, later start only reported already running, and bounded restart failed to stop helper processes. GUI inspection reported a locked Mac. An async human request asks for unlock, quit/reopen Desktop and confirmation of a running engine. This is the first recorded goal-turn prerequisite blocker; the goal remains active and incomplete.
- [Coverage attempt5](issue-806-coverage-attempt5-analysis.md) completed all56 projects,51 pass/five fail, exit1/ASCOV120. Docker caused487 failures; the one test-path policy violation is corrected with the required helper and verified by15 policy and103 receipt tests. Current-tree coverage is still required after this test change. No thresholds, exclusions or gate arguments were weakened.
- The private [trial bundle](/private/tmp/issue806-developer-trial-e81c756c.zip) binds nine exact archives, source revision, canonical commands, replacement assignment and blank human report. It is ready for an outside developer, not completed adoption evidence. Doctor#801 and case#806 remain OPEN. Actual hosted3OS/timing, human trial and distinct before-release/before-closure coordination remain outstanding. No push or PR has occurred; ship has not begun because the hard gate is unsatisfied.
