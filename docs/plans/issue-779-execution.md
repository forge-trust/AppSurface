# #779 Make It So execution record

Goal: implement the [approved EvidenceHost plan](../designs/issue-779-evidencehost-trust-boundary.md) through a validated **draft** PR. Preserve the shared admission contract, zero protected secrets, mandatory stop/join-or-fatal ordering, and all [57 acceptance groups](issue-779-evidencehost-test-plan.md).

## Shared integration checkpoint (2026-10-02)

The [shared producer and host checkpoint](../evidence/issue779-consumer-acceptance.md#shared-producer-and-host-integration-checkpoint-2026-10-02)
records the completed physical coverage extraction, internal callback lease,
captured concrete host registrations, tracked readiness and joined cleanup guard.
The focused validation covers 457 distinct cases across successful scoped runs;
it is macOS metadata/procedure/lifecycle evidence and supplies no native or
coverage improvement claim. Historical failed assertion and fixture runs are
preserved. The production catalogue and consumer-proof resolver remain closed.

The [frozen v11 native outcomes](../evidence/issue779-consumer-acceptance.md#frozen-v11-native-outcomes-and-next-corrections-2026-10-02)
retain the failed production handshake and Aspire 0/5 result, their immutable
source/run/artifact bindings and the narrow next corrections. The corrected
control fixture, subject completion flow and additional startup diagnostics
require a fresh snapshot and native execution. The root v2 application handler,
actual shared application/producer execution, protected downstream proof, all 57
acceptance groups, current-main integration and fresh unchanged coverage gate
remain required before updating the draft feature PR.

## Current recovery checkpoint — native gate reached (2026-10-02)

The [native solution and systemd startup records](../evidence/issue779-consumer-acceptance.md#native-solution-gate-and-systemd-startup-result-2026-10-02)
bind each observation to its private source snapshot. Native v4 passed CLI
**338/338** and Aspire **81/81**, then the unchanged exact coverage wrapper
failed `ASCOV120` on one test path-policy assertion. Its merged measurement was
**94.88% line / 88.52% branch**; numerical and patch gates were not reached.
Focused Enhance cycle 1 remains in Verify with this concrete failure. The next
correction uses the repository's shared path helper and meaningful behavior
tests for changed uncovered execution paths; no threshold, selection or base
change is authorized or needed.

Runtime v5 reached worker submission but `systemd-run` returned **1**, without a
completed runtime proof. Source-build and publish logs are warning-free. Closed
startup diagnostics and actual mount visibility must distinguish rejection from
service-start failure before selecting a runtime fix.

The standalone protected gate consumer has a warning-free locked restore/build
and **11/11** external-process portable controls. Its coherent synthetic JSON
positive verifies public gate behavior and supplies no authenticated channel or
Trusted authority. The provisional Aspire child fixture builds a real pinned
Aspire/DCP payload and exercises a real native HTTP resource in portable controls.
Native Aspire/DCP/systemd execution, shared-host integration, protected-base/fork workflows and downstream acceptance
remain required. The preliminary draft PR has not been updated from these
candidate observations.

The subsequent fixture correction closes the scoped watchdog/pump source findings:
private PID acknowledgement precedes launch; watchdog liveness remains required
through clean disarm; both joined pumps must acknowledge error-free EOF and exact
final byte totals. Main-only TERM gives a fresh five-second grace before whole-unit
force, and normal/cancel require exit zero. **33/33** portable controls passed,
including real fork/Pipe and four ordinary resource-DLL HTTP controls. A separate
read-only review found no remaining P1/P2 issue in those three fixes or their
private native wrapper. Native execution remains pending.

The production broker had the same joined-thread gap. It now rejects failed or
missing pump EOF, latches the lease closed, and checks command/job received-byte
accounting before registering artifact results. Portable launcher controls passed
**56/56** and driver controls **20/20**, exit zero. Bounded worker startup status
diagnostics were added; namespace guards remain unchanged pending actual failure
evidence. The path-helper and legacy-option migration corrections passed **26/26**
focused tests, with warning-free source builds and unchanged bytes after both
scoped formatters. These observations require a new immutable native snapshot.

The [full-snapshot startup diagnosis](../evidence/issue779-consumer-acceptance.md#full-snapshot-startup-diagnosis-2026-10-02)
now records 2725 source files at `cdadf95bb91e46c21d46dc72a8317950e9fd5da4`.
Runtime v6 failed namespace setup with systemd status **226**, without a terminal
manifest. Aspire v1 passed the factory-stall control and failed the other four
before DCP/resource execution because its embedded SDK store path was not
writable. All five Aspire controls confirmed owned exit, empty cgroups and exact
error-free pump accounting. The narrow workspace and store-path corrections
retain the existing guards and require new native runs. Native v5b is rerunning
the same full snapshot after correcting the harness's required `--consumer`
argument; its current-source coverage acceptance remains pending.

## Current state

- Dedicated branch: `codex/make-it-so-evidencehost-779`, initially from `5dc141db81009348538d3f6b7634a99ccb34cd99`, fast-forwarded to `origin/main` at `fd0b124e16a35b85ee16acde799834af407e18de`. Existing approved plan, test-plan and `TODOS.md` changes belong to #779 and are preserved.
- GitHub access: repository `forge-trust/AppSurface`, default branch `main`, viewer permission `ADMIN`; issue #779 remains open. This does not confer a recorded CI-owner acceptance.
- Durable Make It So goal exists. The user has resolved the prior ownership blocker; work continues toward the same full objective. [Draft PR #850](https://github.com/forge-trust/AppSurface/pull/850) contains the committed preliminary stage at `4dd992ec`; the shared implementation remains local and requires fresh verification before the draft is updated.
- Read the actual workflow, existing host/CLI entry points and registered claim/parser paths. [Consumer acceptance record](../evidence/issue779-consumer-acceptance.md) records the existing unsupported workflow and required proof.
- The user selected **A** on 2026-09-30: AppSurface's GitHub Actions workflows are the first consumer, and Andrew is the CI owner. The next step is actual process/filesystem/supervision proof; ownership confirmation alone admits no platform. A provisional Linux proof is now prepared; see the consumer acceptance record.
- [Gate inventory](../evidence/issue779-gate-inventory.md) is complete. External consumers remain unknown. Existing CLI targeted/no-evidence gates, Aspire observation resources and public builder status-enum authority require coordinated migration; no protected downstream manifest gate currently exists in this repository.
- [Coverage gate audit](../evidence/issue779-coverage-gate.md) is complete. It establishes the command, existing thresholds/tolerance, prerequisites and missing packed EvidenceHost proof; it does not assert coverage measurements.
- Bounded canonical ingestion is implemented in Contracts: caller-owned counted streams, 20 MiB ceiling and optional lower limit, recursive duplicate/case-collision rejection, named enum validation, explicit supported plan/manifest versions, safe exceptions and unchanged canonical round-trips. CLI policy/plan/manifest reads now consume that stream API through an intentionally internal file-open seam. These pure input checks do not select or freeze a supervisor/provider API.
- Doctor always reports protected execution facts as `unverified` for targeted and release profiles, independent of `GITHUB_ACTIONS`. Tests check the actual `Satisfied` field, planning-only filesystem effects and diagnostic canary secrecy. Focused successful runs: eight doctor/neighboring-release cases, 27 shared JSON cases, then 58 CLI input/planner/doctor cases. They overlap; do not sum them as distinct coverage.
- Caller tests cover actual file replacement at open, exact 20 MiB and one byte over for policy/plan/manifest, invalid/ambiguous input, unsupported schemas and secret-safe read failures. An initial caller fixture used an empty conservative profile and correctly failed `ASEVD105` before exercising input; it was corrected with a nonempty fallback and explicit docs-only rule. The subsequent 58-case run exited zero.
- Actual local CLI smoke passed `init --sample`, four doctor environment variants, conservative explain and malformed-policy rejection (`ASEVD205`), all with expected exits and no canary leak. Machine report: `/private/tmp/issue779-planning-qa.json`; temporary trial: `/private/tmp/issue779-planning-tvxk2o11`. Commands took 4.422 seconds with prerequisites and a built CLI already present; this is not a fresh-user installation/comprehension timing or an execution/admission/packed proof.
- Initial and final scoped `dotnet format` exited zero. The final combined test log reports 86 passed, zero failed and zero skipped, with no compiler/analyzer/XML warnings in that log. The verification delegate did not retain the numeric test-process exit code; the terminal test summary is the direct evidence. `git diff --check` exited zero after formatting. A separate native read-only review found no actionable issue in this independent input/doctor slice; it is not the full post-implementation `$enhance` review. All 62 local link destinations checked across touched references and the coverage/execution records exist.

## Validation attempt record

| Check | Command or evidence | Result | Scope |
| --- | --- | --- | --- |
| Doctor focused | `dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore --filter 'FullyQualifiedName~EvidenceDoctorTrustTests\|FullyQualifiedName~CliWorkflow_ShouldDescribeExternalAndTrustedPrerequisitesForAReleaseProfile' --logger 'console;verbosity=minimal'` | exit 0; 8 passed | Environment flags, selected scopes, planning side effects, canary diagnostics and existing release neighbor. |
| Shared JSON | `dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore --filter FullyQualifiedName~EvidenceJsonInputTests` | worker reported exit 0; 27 passed | Counted nonseekable streams, limits, cancellation, safe read failures, schema/enum/property strictness and additive/canonical compatibility. |
| CLI input/planner/doctor | `dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore --filter 'FullyQualifiedName~EvidenceCliJsonInputTests\|FullyQualifiedName~EvidencePlannerTests\|FullyQualifiedName~EvidenceDoctorTrustTests' --logger 'console;verbosity=minimal'` | exit 0; 58 passed | Caller JSON limits/replacement/diagnostics plus existing planner and doctor regressions; log `/private/tmp/issue779-cli-input-tests.log`. |
| Actual planning CLI | `python3 /private/tmp/issue779-planning-qa.py` | exit 0; passed | Actual built CLI process, temporary starter, planning output and malformed-policy rejection. |
| Final combined regression | `dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore --filter 'FullyQualifiedName~EvidenceJsonInputTests\|FullyQualifiedName~EvidenceCliJsonInputTests\|FullyQualifiedName~EvidenceDoctorTrustTests\|FullyQualifiedName~EvidencePlannerTests\|FullyQualifiedName~EntryPoint_ShouldRunTheNoEvidenceEvidenceHostJourneyAndReportCoveragePrerequisites' --logger 'console;verbosity=minimal'` | terminal summary: 86 passed / 0 failed / 0 skipped; numeric exit not retained | After final formatting; log `/private/tmp/issue779-input-final-tests.log`. Existing entry-point journey remains a regression only; its old gate behavior is scheduled for admission migration. |
| Full coverage | `./scripts/coverage-solution.sh` | not run; unverified | Required after complete implementation, before ship. |

## Current required input

Resolved by user option A on 2026-09-30. Consumer: `forge-trust/AppSurface` GitHub Actions. CI owner: Andrew. This explicit reply supplies the identity missing from the earlier audit; no reapproval is required. Consumer/platform proof and the remaining draft-PR objective remain required. The host's last reported goal status was `blocked`; user input resumes work without replacing the goal or resetting cumulative usage.

### Prior blocked audit (resolved)

Blocked audit, continuation 1: the prior goal turn made concrete progress on ingestion/doctor and verification, then reached this required input. This continuation rechecked the authoritative worktree, acceptance record and approved implementation order; ownership is still unconfirmed and no provider/platform proof exists. The automatic continuation is not a human answer. No tests were rerun and no unsupported supervisor/provider API was selected. The same blocker has now appeared in two consecutive goal turns; the goal remains active under the three-turn blocked threshold.

Blocked audit, continuation 2: the preceding continuation was no progress, not a verified process/job wait. This turn rechecked the current worktree, the still-unconfirmed consumer acceptance record and the approved prerequisite order. No human selection or external proof has arrived. The same required-input condition has now appeared in three consecutive goal turns, so the host goal must be marked `blocked`. No further independent implementation is selected ahead of the required real boundary proof; the original draft-PR objective and all remaining facets are preserved. Resume after the named consumer/CI-owner selection is supplied.

## Required checks

- COVERAGE_GATE: `./scripts/coverage-solution.sh`, unchanged. It requires 95% aggregate line / 85% aggregate branch and 95% patch line / 85% patch branch coverage against `origin/main` locally; patch-line mode is `codecov`, with the existing 0.5-point gate tolerance. Run the unchanged gate on final code, rerun after coverage-affecting changes, and obtain a fresh pass immediately before ship.
- Enhancement: one scoped `$enhance` cycle after implementation; focused additional coverage loops only from concrete gate evidence.
- QA: feature is CLI/SDK/CI, so browser QA is inapplicable. Execute real planning/mode/help/worker/packed CLI+SDK flows and documented snippets.
- Formatting, analyzers/XML docs, hostile/neighboring unit tests, isolated fatal workers, real consumer/platform proof and controlled downstream gate are required.
- Draft PR only, Conventional Commit title, attach the created PR to this chat. The approved
  [preliminary scope](issue-779-preliminary-proof.md) references #779 without closing it; the complete
  implementation adds `Fixes #779` after all acceptance requirements pass. No push or PR before
  the applicable required gates pass.

## Remaining facets

1. Confirm consumer ownership and prove the minimal supervisor/process/filesystem/output seam before freezing public API.
2. Shared admission/claim capability, strict counted JSON ingestion, exact protected policy/catalogue/verifier/plan binding.
3. CLI and Aspire actual caller integration, explicit mode/legacy migration and pre-start guard.
4. Owned work and immutable terminal failure, independent deadlines, stop/join/reverse cleanup or unsuccessful fatal termination.
5. Aggregate artifact/process-output limits and monotonic conservative job-time reserves.
6. Complete paired hostile matrix, 57-group mapping, schema/packed/downstream proof, docs/diagnostics/migration and release acceptance.
7. Enhance, clean baseline commit, user-facing QA, final unchanged coverage gate, ship draft PR.

## Evidence limitations

No successful consumer/platform proof, new coverage result, changed branch coverage or full implementation completion is asserted. The focused doctor result above validates only that slice. The admission/claims, lifecycle and caller migration are still outstanding: existing unsupported entry behavior described in the gate inventory has not yet been repaired. Supported Trusted registration must remain excluded until proof; this is an implementation requirement, not a claim that current code already enforces it. Observation still needs an independently armed supported launcher; a legacy Boolean cannot supply authority.

## Resumed mechanism preparation (2026-09-30)

- Prepared explicit C#/Python/Linux C mechanism fixtures and a candidate Ubuntu 24.04 workflow. They have
  no public provider API or runtime authority. Linux/systemd/UID/allocation selection remains provisional
  until the real consumer run; Windows/macOS remain excluded.
- Added a launcher activation barrier: callback entry waits until the launcher verifies actual PID 1
  service identity, armed deadline, control-group membership and no-new-privileges. Root-owned read-only
  tool files are group-readable by the worker and denied to the subject. PrivateTmp compatibility is
  handled by a fresh root-owned `/run` anchor.
- Local .NET worker build: exit 0, zero warnings/errors. Formatting: exit 0. Real worker controls: success
  exit 0, cooperative stop exit 2, FailFast signal exit -6, correct markers and no fatal unwind/publication.
- Local Linux UID-only subject validation: exit 0; twelve denial/environment and allowed-input checks pass.
  This container is a test environment only and supplies no systemd/GitHub/provider admission evidence.
- Linux allocation probe: compiler exit 0, probe exit 0, 14/14 observed expected outcomes. This is
  retained-descriptor/content-equality mechanism evidence only; full writer cryptographic hashing remains.
- Native host rejection: supervision script exit 1, combined launcher exit 2. The runner proof is pending,
  not skipped/passed. No protected catalogue, admission capability, full lifecycle migration or downstream
  gate implementation is declared complete.
- Delegated the exact unchanged `./scripts/coverage-solution.sh` to a native watcher; current full-lane
  result pending. No thresholds, patch base or environment requirement were changed.
- Pending async user choice: preliminary mechanism-proof PR after coverage passes, or owner-provided real
  Linux VM run. This sequencing decision follows the plan's proof-before-public-API rule and Make It So's
  no-PR-before-coverage rule. Existing ownership authorization remains resolved.

### Provisional fixture review and verification

Native bounded read-only review found one P2 verifier defect: accepting a nonempty truthy subject-result
map could omit a required observation. Fixed it to require the exact twelve-key set and Boolean `true`.
Four Python verifier regression tests passed, including every omitted key, false values, numeric/string
truthy values, extra keys and wrong result shapes. This is not the full post-implementation Enhance pass.
The reviewer reported no confirmed Ubuntu execution blocker from static review; actual systemd execution
is still unverified. It did not certify production API integration, pipe backpressure or full branch coverage.

Prepared task files: `tests/evidencehost-consumer/`, `scripts/verify-evidencehost-linux-mechanism.sh`,
and `.github/workflows/evidencehost-mechanism-proof.yml`. A real CI run and public API freeze are dependent
on the pending runner-proof choice. User ownership approval has not been asked again.

The final fixture checks PID 1's actual worker User/Group, RuntimeMaxUSec, TimeoutStopUSec, SendSIGKILL,
KillMode, no-new-privileges, empty capability sets and protected control-group hierarchy before activation.
Successful workers remain loaded only long enough to verify their exited state and empty group; teardown
is deferred to the common exit-confirming cleanup. Python verifier tests and diff checks pass after this
change. Actual systemd property representation and the runner execution remain explicitly unverified.

### Required-input handoff

The runner-route question remains unanswered. Native coverage watcher `01a0f57d-6331-7902-996d-c4f2af2c4a2e`
owns the running unchanged full gate and `/private/tmp/issue779-coverage-attempt1.log`; no terminal exit or gate
measurements have been returned. Do not rerun it concurrently or claim a pass. Wait for its terminal result
on continuation. The allocation/review agents are complete and closed. No commit, push, PR, public API freeze
or Trusted enablement occurred during this preparation. User choice A authorizes a staged preliminary proof
PR only after coverage passes; choice B supplies an owner-run Linux VM proof. Neither reduces the remaining
full #779 objective or converts candidate fixture observations into protected runtime authority.

## Staged proof route approved (2026-10-01)

User option A resolves the runner-route decision: stage a preliminary draft mechanism-proof PR after the
unchanged coverage gate passes, use its CI observations, then continue the full #779 implementation. This
approval does not waive coverage, admit Trusted, freeze public provider APIs or declare #779 complete.
The preliminary PR should reference #779 without closing it; the final complete implementation owns closure.

Coverage attempt 1 reached all 54/54 project entries and built with zero warnings/errors, then exited 1
with ASCOV120 because four test projects failed. The wrapper did not reach its threshold/patch gate.
Raw aggregate Cobertura: 95.07% line (135978/143029), 89.00% branch (44922/50472), not a gate pass.
Failures: AuthAspNetCoreDevAuthExample.Tests 21/61, RazorWire.IntegrationTests 2/193 visual comparisons,
Cli.Tests 1/1667 stale doctor assertion, Docs.Tests 6/3083 prerequisite script timeouts. The failed lane
log is `/private/tmp/issue779-coverage-attempt1.log`. Native watcher completed and is closed.

Started a focused Enhance invocation on these exact gate candidates (four-cycle budget). Main fixes the
stale doctor command assertion and uncovered JSON input paths; three disjoint native workers investigate
Auth, Docs and visual failures. No threshold/diff-base/sandbox requirement is changed. New JSON tests
exercise span limits, all sanitized stream error categories, stream ownership, invalid arguments,
undefined enum serialization and sibling/empty property scopes through the public API.

### Focused Enhance cycle 1, continued

- Corrected the stale `EvidenceCommands_ShouldExposeSuccessAndRecoverySemanticsWithoutEntrypointInfrastructure`
  assertion: successful structural doctor output now expects `ready_with_external_prerequisites`, an
  `unverified trusted-envelope` and explicit no-admission text. Malformed/missing/ambiguous input assertions
  remain active. The focused Evidence/command run passed 36/36 tests, exit 0, log
  `/private/tmp/issue779-evidence-gate-fixes.log`.
- Authentication failures did not reproduce. Coverage-collected verifier contracts passed 56/56 and the
  whole auth example passed 61/61, both exit 0. No auth files or gate policy changed; the initial cause is
  unconfirmed. Investigation: `/private/tmp/issue779-auth-readiness.log`.
- Fixture verifier regression tests passed 4/4, exit 0; shell syntax and `git diff --check` passed.
- The [patch gate](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) compares committed
  `HEAD` against `origin/main`; an uncommitted task diff is not patch evidence. Commit the intentional,
  formatted and locally verified slice before the next unchanged full wrapper run. Do not substitute an
  empty diff or claim a dirty-base aggregate measurement verifies the patch.
- Docs and visual investigations, native adversarial review, final formatting/CLI QA and the fresh full
  coverage gate remain pending. This cycle is not complete or a clean full review.

Final scoped formatting completed with exit 0; the parent recovered its terminal result after the validation
worker could not access that process ID. The combined Evidence/planner/command regression passed 95/95,
exit 0, with no compiler/analyzer/XML warning or error matches. Actual planning CLI QA passed all seven flows
(six exit 0, malformed policy exit 1 with ASEVD205) without a canary leak. Logs:
`/private/tmp/issue779-format-gate-fixes.log`, `/private/tmp/issue779-evidence-gate-final.log`, and
`/private/tmp/issue779-planning-qa.json`. All 135 checked local documentation link destinations exist.

### Native review and remaining gate repairs

The native adversarial pass found one P2: null/missing required contract members could deserialize and then
escape the stable CLI diagnostic path as a null dereference. The shared reader now uses the framework's
required-constructor and nullable-member enforcement, plus typed validation for null collection elements
(which nullable annotations do not cover). Optional defaults/nullable members and additive unknown fields
remain supported. Focused schema/caller regression validation is pending. Test/fixture payloads were reviewed
summary-only by this adversarial reviewer; the parent reviewed the mechanism fixture source separately.
The optional Claude Code review is unavailable because its CLI is not installed; no substitute outside
completion is claimed.

Docs prerequisite contract class passed 11/11, exit 0. Its full restricted suite stalled and was cancelled
after over eight minutes, exit 1; no source defect or supported code repair was established. The next full
wrapper run must use its required unrestricted execution environment, preserving all tests and timeouts.

Two visual failures reproduced in unrestricted focused runs. Parent inspection of the Light and Graphite
release screenshots confirmed the existing preview.10-to-preview.11 and release-outline content changes,
with unchanged layout, styling and update text. Deliberately refresh only affected release-route baselines
and verify with refresh disabled; preserve original pixel/noise limits. This is an existing content-drift
repair required by the gate, not a new UI design. Logs: `/private/tmp/issue779-visual-light-repro-escalated.log`
and `/private/tmp/issue779-visual-graphite-repro.log`. The other routes preceding release comparisons passed.

The deliberate baseline repair updated five release-route PNGs (Light desktop and the four Graphite
system/preference variants). Three incidental home/search PNG differences were restored to their original
bytes. Both the refresh run and the subsequent refresh-disabled comparisons passed 2/2, exit 0. Logs:
`/private/tmp/issue779-visual-refresh.log` and `/private/tmp/issue779-visual-refresh-verify.log`. No tolerance,
skip, UI source or production style was changed.

The new schema regression file covers complete nested round-trips, required/missing/null members, all 21
known collection-item cases, optional/default/additive compatibility and actual CLI Doctor/Verify diagnostic
mapping. The initial worker handed off an unverified file; the parent corrected its compilation defects and
added the original `Profiles: null` / `Rules: null` cases. The entire CLI Evidence filter passed **153/153**,
exit **0**, with no warning/error matches: `/private/tmp/issue779-json-shape.log`. A fresh native scoped
adversarial pass found no actionable Contracts/CLI defect after the deserializer-only repair; no outside
review or full runner/lifecycle completion is implied. Final formatting and committed full-gate verification
follow this checkpoint.

## Committed preparatory slice and coverage attempt 2

- Commits: `c3b80e8b` (five inspected release screenshots), `13e14c30` (bounded JSON, planning doctor,
  tests/docs and candidate Linux fixtures), and `2d27b0d5` (approved-plan whitespace). The working tree
  was clean when the unchanged full gate began against `origin/main` at `fd0b124e`.
- Coverage watcher `01a0f5f2-d68b-7771-b1b9-3884662da9de` owns attempt 2 and its terminal exit:
  `/private/tmp/issue779-coverage-attempt2.log`. Do not run another full gate concurrently.
- Final native review found a P3 serialization compatibility regression: installing the strict enum
  converter in shared options changed `Serialize` for undefined enums. Restore its established
  `JsonStringEnumConverter` output, keep the strict reader exclusively in deserializer options, and
  verify both canonical numeric bytes and input rejection through the public API. The source/test/docs
  repair is written while attempt 2 executes previously built assemblies; no rebuild was started.
  Thus attempt 2 binds the preceding committed candidate, and cannot validate this repair.
- Attempt 2 reproduced four auth verifier timeouts (three HTTP failure controls and the leading-zero
  readiness timeout). Isolated earlier 61/61 success does not diagnose their cause. Dedicated read-only
  investigations now own authentication and docs process paths; no timeouts or tests are relaxed.
- Packed CLI/SDK QA worker `01a0f5f5-5c8e-7431-967a-84e325ac135c` owns the isolated package trial
  `/private/tmp/issue779-packed.RItPkkt8`. Await its observed terminal outcomes. Affected package
  probes must be refreshed after the serialization repair.

The user's staged route remains approved. Public admission/provider API freeze and full #779 completion
still depend on actual Linux runner proof followed by the remaining protected integration.

### Attempt 2 terminal result

The unchanged wrapper exited **1** with `ASCOV120` after all **54/54** project entries completed.
Build: **zero warnings / zero errors**. Only AuthAspNetCoreDevAuthExample.Tests failed: **4 failed,
57 passed**. Docs.Tests passed **3083/3083** and RazorWire.IntegrationTests passed **193/193**.
Raw merged coverage was **95.09% line / 89.01% branch**; the wrapper did not reach its threshold or
patch gate, so these figures are not a pass. Numeric exit: `/private/tmp/issue779-coverage-attempt2.exit`.
The enum repair was written after this run's build and is excluded from its code-validation claim.

The next evidence-backed action is to retain bounded auth fixture timeout output/events, then reproduce
those same four cases without changing their deadlines. Refresh affected enum/unit/packed evidence after
formatting. A third full wrapper run is required on the final committed candidate after focused fixes.
Packed package checks at `2d27b0d5` passed with separate process exits/streams in
`/private/tmp/issue779-packed.RItPkkt8/report.json`; these validate the old snapshot and need enum refresh.

### Coverage repair review progress

The final review's P3 shared enum-converter regression was accepted and corrected. Serialization retains the
prior `JsonStringEnumConverter` behavior, and deserializer options exclusively use the strict input converter.
The public regression verifies exact numeric output for an undefined enum and rejection when those bytes
are consumed. Formatting exited **0**, and diff checks passed. A fresh CLI Evidence run and package refresh
are delegated; no fresh pass is asserted until their terminal results arrive.

The initial packed trial also emitted `NU5118` for a duplicated Contracts README package item. The package
project's redundant `None Include` was removed because the repository's `Directory.Build.targets` already
updates and packs that README. The refreshed archive must include exactly one README and have no NU5118.

Docs investigation found no established cause for the earlier hang, and current full-suite success means
no speculative Docs source repair is selected. Its possible unbounded post-timeout helper waits are not
claimed as this failure's cause. Authentication diagnostics/reproduction remain the concrete gate work.

The [preliminary proof scope](issue-779-preliminary-proof.md) records the approved staged deliverables and
post-PR runner checks, while preserving the full acceptance objective and excluded Trusted platforms.

### Fresh focused fixes and reproduction

- Native review identified one additional P3: `File.Exists` prechecks could classify inaccessible inputs as
  missing before the actual open seam. Both CLI readers now classify missing-file/directory exceptions
  at open time as `ASEVD204`/`ASEVD208`, and map denied/invalid/other reads to `ASEVD205`/`ASEVD209`.
  Six missing-file/directory controls and deleted physical paths with injected access denial verify these
  outcomes. The reviewer accepted the repaired hunks; fresh caller validation is pending.
- Post-enum-fix CLI Evidence run passed **153/153** (6 seconds; numeric exit **0** reported by its process
  record). This predates the six new caller cases and is not final validation. A .NET workload-verification
  host warning was observed, separate from compiler/analyzer/XML warnings.
- Auth fixture diagnostics now retain only bounded stage/event categories and owned PID states on timeout,
  excluding raw responses and environment values. The first diagnostic compile failed on a local name
  collision; a follow-up rename overreached the parameter guard. Both errors were corrected in place.
  The third focused collector run passed all **4/4** original failure controls with numeric exit **0** and
  unchanged deadlines (HTTP controls approximately 1 second each; leading-zero control approximately
  9 seconds): `/private/tmp/issue779-auth-timeout-repro3.log`. No causal auth source fix is inferred.
- Final formatting exited **0**. The fresh CLI Evidence run passed **159/159**, with numeric exit **0**:
  `/private/tmp/issue779-enum-refresh-attempt2-20261001/logs/cli-evidence-attempt2.meta.json`.
  The next full wrapper must measure all committed fixes and diagnostics.

### Refreshed package and preparatory review checkpoint

The exact local package version `0.1.0-issue779-proof.20261001.2` was installed into an owned temporary
tool directory. Help, starter initialization, doctor with `GITHUB_ACTIONS=true`, and targeted explain
exited **0** with their expected output and owned artifacts. Duplicate-property and null-profile policies
exited **1** with `ASEVD205`. Doctor remained `unverified` and created no execution manifest.

The SDK consumer restored Contracts from the local feed alone and built with zero warnings/errors.
Its counted nonseekable stream, additive/case-compatible round-trip, required/null member and item,
optional default, unsupported-version and undefined-enum compatibility checks all passed. Undefined enum
serialization retained numeric output and deserialization rejected it. Both refreshed packages contain
exactly one root README; neither pack log has `NU5118`. The parent retrieved the overall process's numeric
exit **0**. Commands, separate streams, package hashes and consumed source hashes are recorded in
`/private/tmp/issue779-enum-refresh-attempt2-20261001/packed-refresh-report.json`.

A fresh native preparatory review reported no actionable finding, with test/fixture payloads explicitly
reviewed in summary mode only. Outside Claude Code remains unavailable. This is scoped review evidence;
full coverage, ship review and actual Ubuntu mechanism proof remain required. Python verifier controls
passed **4/4**, and the mechanism script passed shell syntax validation. No Trusted platform is admitted.

### Base integration before coverage attempt 4

Functional `/qa` Standard on committed `4a74a6b5` passed seven CLI/SDK contracts with separate captured
streams/exits and six observation checkpoints. Report:
`/private/tmp/issue779-qa-final-4a74a6b5/qa-report-issue779-2026-10-01.md`.

Coverage attempt 3 began at `4a74a6b5`, built with zero warnings/errors, then was intentionally interrupted
when a fetch advanced `origin/main` from `fd0b124e` to `4aa8329c` (#844). The watcher observed terminal
exit **130**; cancellation interrupted the shell's exit-file write. The log is
`/private/tmp/issue779-coverage-attempt3.log`. No gate pass or complete child-exit proof is claimed from
that run; its active dotnet child was reported forcefully terminated. Subsequent process inspection
found no surviving process with that reported PID, while unrelated work was left alone.

The merge preserves both independent TODO additions. Newer `main` release baselines replace the initial
five-snapshot repair: parent inspection confirmed preview.11 plus the newly merged dialog release content.
The final preliminary diff therefore changes no PNG. Current-base integration precedes a new unchanged
full coverage command and fresh ship review.

### Property encoding repair during attempt 4

Installed CLI observation `capture008` at `cf6d3aa7` exposed a raw decoder exception for a policy with
an invalid UTF-8 property name, instead of the documented `ASEVD205` diagnostic. Native review
confirmed the P2 at `Utf8JsonReader.GetString()` in the uniqueness preflight. The repair catches
`InvalidOperationException` only around that property decoding operation and translates it to the
existing generic `JsonException` boundary without retaining the original exception or input.

Public regressions cover invalid UTF-8, UTF-8 encoded surrogate bytes, lone high and low escaped
surrogates, counted chunked streams, policy/plan/manifest CLI mappings, and valid Unicode/paired
surrogate controls. The scoped native re-review reported no actionable finding (static review only).
An isolated build under `/private/tmp` compiled but seven neighboring coverage-fixture tests could
not locate repository files. Repeating the same filter with isolated artifacts inside the checkout
passed **170/170**, exit **0**, with no compiler/analyzer/documentation warning matches:

```bash
dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj \
  --artifacts-path TestResults/issue779-encoding-validation \
  --filter 'FullyQualifiedName~Evidence' --verbosity minimal
```

Log: `/private/tmp/issue779-encoding-validation-repo.log`. Default build assemblies were not replaced,
so coverage attempt 4 continues to measure the pre-repair committed `cf6d3aa7` candidate only. It has
already reproduced **6/61** auth fixture deadline failures; their bounded diagnostics do not prove
a common fixture or verifier cause. Deadlines, gate command and thresholds remain unchanged.
An isolated auth reproduction and fresh packed CLI/SDK encoding proof are delegated. A final
committed full gate must validate this repair before publication.

Regression proof used the new test assembly in the isolated checkout artifact directory, temporarily
substituting only its Contracts DLL with the original committed DLL. All **10/10** malformed-property
cases failed with the old raw `InvalidOperationException`, exit **1**. Restoring the repaired DLL and
running those cases plus the valid Unicode control passed **11/11**, exit **0**. Repository default
assemblies were unchanged. Logs: `/private/tmp/issue779-encoding-red.stdout.log`,
`/private/tmp/issue779-encoding-red.json` and `/private/tmp/issue779-encoding-green.log`.
Scoped whitespace formatting and diff checks exited **0**. Python verifier controls passed **4/4**;
the mechanism launcher passed shell syntax validation and all 14 checked documentation destinations exist.

### Coverage attempts 4 and 5, and invalid-path repair

Attempt 4 completed all **54/54** project outcomes at committed `cf6d3aa7` against `origin/main`
`4aa8329c`. The build had zero warnings/errors; 53 projects passed, while the auth fixture suite had
**6 failed / 55 passed**. The unchanged wrapper exited **1 / ASCOV120** before the official aggregate
and patch gates. Raw merged coverage was **95.11% line / 89.03% branch**, which is not a gate pass.
Preserved evidence: `/private/tmp/issue779-coverage-attempt4-evidence/summary.json` and its copied
project log. The same failed auth controls plus an early-exit neighbor passed **7/7**, exit **0**,
in both isolated unsandboxed and sandboxed collector trials with unchanged deadlines. Those trials
do not explain the full-run stalls or establish a causal production fix.

Attempt 5 started on clean committed `2bb8f1f6` with the same base and command, then the user
interrupted the turn. Its terminal exit file is absent and its execution handle is no longer present.
Process inspection found no surviving coverage or test process for this checkout; other checkouts'
work was left alone. Its last log heartbeat was at elapsed 690 seconds in the RazorWire integration
project. This attempt is **interrupted and unverified**, with no invented numeric exit or gate result.

A subsequent native review identified raw `ArgumentException` for an empty policy path. Actual CLI
reproduction confirmed the leak. The two file readers now map empty or file-API-rejected paths to
generic `ASEVD204` (policy) or `ASEVD208` (plan/manifest), without the invalid path or inner exception.
Six public workflow regressions cover empty and NUL-containing paths for all three inputs. The
fresh Evidence filter passed **176/176**, exit **0**, with no warning/error matches; whitespace
formatting and diff checks exited **0**. Actual rebuilt CLI `doctor --policy ''` exited **1** with
only the expected `ASEVD204` diagnostic. Logs: `/private/tmp/issue779-path-validation2.log`,
`/private/tmp/issue779-path-format.log` and `/private/tmp/issue779-empty-policy-fixed.stderr.log`.
The next full gate must measure this final committed repair; fresh packed CLI/SDK QA is in progress.


### Final preliminary reviews, packed preparation and coverage attempt 6

The unchanged wrapper ran on clean committed `fc56d8b9899294a268925e134bf41f95bc17d60a`
against `origin/main` at `4aa8329c76f4279ad319747f9f3c14b9b8de51ef`. All **54/54** project
entries finished; **53 passed**, and the CLI suite reported **14 failed / 1710 passed**. Every
failure was a Durable schema container case after Testcontainers' initial Docker availability
check timed out on both local Unix sockets. The wrapper exited **1 / ASCOV120** before its
aggregate/patch gate. The build had **zero warnings / zero errors**. Auth **61/61**, Docs
**3083/3083** and visual integration **193/193** passed. Evidence is retained in
`/private/tmp/issue779-coverage-attempt6.log` and its numeric `.exit` file.

The separate diagnostic threshold command used the generated attempt-6 report and unchanged
95/85 aggregate and patch thresholds, the existing 0.5-point tolerance, `origin/main`, and
`codecov` patch-line mode. It exited **0**, measuring **95.10% line / 89.03% branch** and
**98.33% patch line / 100.00% patch branch**. This diagnostic does **not** turn the failed
full wrapper into a pass. It establishes that no uncovered changed-code path currently fails
those thresholds; a successful fresh unchanged full wrapper remains mandatory.

Docker subsequently returned its server version and HTTP 200 `/_ping` on both sockets.
The exact formerly failing prune-signature theory, rerun without building and with the
coverage collector, passed **3/3**, exit **0**, in 17 seconds with no stderr. Command:
`dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --configuration Debug --no-build --no-restore --filter FullyQualifiedName~Retention_preflight_rejects_prune_routine_signature_drift --collect:"XPlat Code Coverage" --results-directory /private/tmp/issue779-docker-repro/test-results`.
Evidence: `/private/tmp/issue779-docker-repro/exit.json`. This supports a fresh full-run retry;
it does not prove the earlier outage's cause. No Docker restart, prune, test skip, timeout
increase, gate override or source repair was selected for that transient failure.

Generated-doc checks at `fc56d8b9` exited **0**: PackageIndex `verify` confirmed both generated
indexes and release guidance, and MarkdownSnippets `verify` confirmed snippets. Each direct
existing-Debug-DLL invocation was bounded at 180 seconds; logs and numeric metadata are under
`/private/tmp/issue779-generated-doc-verify/`. Changed Markdown had no snippet markers.

The native adversarial review raised an **INVESTIGATE** about planning work proportional to
policy rules times changed paths. Parent inspection confirms that `EvidencePlanner.Resolve`
and its per-path full-rule scan are unchanged from `origin/main`; policy cardinality has no
new bound in this staged implementation. The [preliminary scope](issue-779-preliminary-proof.md)
adds a counted byte ceiling and strict ingestion, preserving existing planner semantics. It
makes no claim that this ceiling bounds CPU work. Introducing rule/path limits would change
existing valid-policy semantics and requires a separately specified compatibility decision;
there is no confirmed regression in the current patch. Record this as a non-blocking existing
planner limitation for the full implementation's budget review, with no speculative count cap.
The review inspected tests/fixtures in summary mode only and ran no processes. The separate
mechanism review read the fixture sources and found no actionable static defect; actual Ubuntu
execution remains required. Final scoped review and packed CLI/SDK terminal results remain pending.


### Nested typed-contract validation repair

Final scoped review confirmed that `Deserialize<EvidencePlan[]>` could return an unsupported
`2.0` plan because version/collection validation previously examined only the root object.
Public regression proof on the isolated checkout artifacts reproduced the bypass: the new
invalid-container test failed, while its supported-container neighbor passed, exit **1**.

Deserialization now attaches validation to each typed Contracts object via
`DefaultJsonTypeInfoResolver.OnDeserialized`. Array, dictionary and caller-defined wrapper
roots therefore apply the same supported-version and known collection checks as direct
roots. Raw `JsonElement` stays untyped; no authentication or admission authority is added.
The [Contracts reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input)
and public XML contract document this behavior. Regressions include plan arrays, manifest
dictionaries, a caller wrapper, null producer items inside a profile array, a streamed
manifest list and canonical/structural-verification positive controls.

After the repair, the fresh Evidence filter passed **178/178**, numeric exit **0**, with
no compiler/analyzer/documentation warnings matched in the log. Command:
`dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --artifacts-path TestResults/issue779-wrapped-validation --filter FullyQualifiedName~Evidence --verbosity minimal`.
Red/green logs: `/private/tmp/issue779-wrapped-red.log` and
`/private/tmp/issue779-wrapped-green.log`. Scoped whitespace format exited **0**;
`git diff --check`, four Python verifier controls and launcher shell syntax passed.
The watcher was instructed to stop the just-started attempt 7 before code replacement;
its exact terminal result remains pending. A new committed full run and refreshed packed
Contracts/SDK proof are required for this repair before publication.


### Coverage attempt 8 and final packaged preliminary QA

The unchanged `./scripts/coverage-solution.sh` completed all **54/54** project slots on
clean `118c3855cbaaad9f479747f742b8af8da18d27ca` against `origin/main` at
`4aa8329c76f4279ad319747f9f3c14b9b8de51ef`. The build reported **zero warnings/errors**.
The wrapper exited **1 / ASCOV120**, with **11 failed tests across four projects**:
Config (1), the auth verifier fixture (2), Durable local proof scripts (2), and Docs
prerequisite scripts (6). All CLI tests **1726/1726** and RazorWire integration tests
**231/231** passed. Raw merged coverage was **95.11% line / 89.04% branch**; the official
aggregate/patch gate was not reached, so this is not a gate pass. The complete preserved
log, exit marker, 54 project logs/JUnit files, timings and Cobertura are under
`/private/tmp/issue779-coverage-attempt8-evidence/`. Process inspection found none of
the recorded attempt-8 owner, runner, build or failure-process PIDs present.

The attempt-7 interruption record is now retained at
`/private/tmp/issue779-coverage-attempt7-interrupted-evidence/termination-summary.txt`.
It records shell status **130**, no wrapper exit marker, and the recorded owner/dotnet
PIDs absent. No numeric wrapper exit result or coverage pass is claimed for that run.

The failures were child startup deadlines. Auth diagnostics stop at `PREFLIGHT,BUILD`
before the first shim event; Docs diagnostics stop after command-presence checks.
The exact Config scalar validation control passed with its collector, **1/1**, exit **0**,
in 10 seconds. A first unchanged Durable two-control collector trial reproduced both
failures. A temporary instrumentation trial subsequently passed but emitted zero trace
records, so it establishes no stage diagnosis. After the read-only load watch observed
a reduction, the unchanged two-control trial passed **2/2**, exit **0**, in 14.041 seconds.
No test deadline, coverage threshold/base, skip or production source was changed.
Focused evidence is retained in `/private/tmp/issue779-config-startup-repro/` and
`/private/tmp/issue779-durable-startup-recovered/`. The host's 14 logical CPUs and
concurrent load readings are an observed condition, not a proved common cause.

The refreshed packed CLI at **0.1.0-issue779-proof.20261001.6** sets both `Version` and
`PackageVersion`. The earlier `.5` `--version` mismatch came from setting only
`PackageVersion` in the verification build; it did not establish a product defect.
The `.6` archive audit and local-feed-only installation passed, and installed
`--version` exactly prints the package version. Pack exit **0** had no compiler/package
warnings, one root README and `RepositoryCommit` matching `118c3855`. Archive SHA-256:
`3f66e5bfb8e1b5042eb4f0ffac7f07ed6b97ba3a12865ebd18553ff34089f74a`.

Functional [QA Standard](issue-779-preliminary-proof.md#verification-and-expected-outcomes)
passed **20/20 current CLI contracts**, preserving separate exits/stdout/stderr and
19 preceding-observation checkpoints: version/help/init, CI-flag doctor remaining
unverified, explain with plan/summary but no manifest, and safe duplicate/null/property,
enum-value, empty/missing-path and malformed plan/manifest diagnostics. The report and
materialized evidence are `/private/tmp/issue779-packed-final-20261001/qa-06/qa-report-current.md`
and its `evidence.json`; the parent retrieved the overall process exit **0**.

The exact Contracts **.5** local-feed SDK consumer also passed, exit **0** in 3.089 seconds,
with no warnings or stderr. It verifies nested typed array/dictionary/wrapper values,
null collection rejection, counted streams, valid/default/additive/enum compatibility,
malformed property names and four malformed enum-string span/stream controls. The latter
returned the exact generic `JsonException`, no inner exception and no canary; no new
source repair was selected. Contracts package SHA-256:
`13a3e8c89f36fb0ad4cca5abb9b238fce03a31501d856885bbc12b790b03779b`. Packaged, cached and
consumer-output Contracts DLL hashes match
`fd260ffd0e468bad63c3be2fceb8032d7a283a92c39b6c443d7ef6d7c5169193`.

Generated documentation checks at `118c3855` passed: existing Debug DLL PackageIndex
`verify` exit **0** in 24.979 seconds and MarkdownSnippets `verify` exit **0** in 0.233 seconds.
Logs and numeric metadata are retained under `/private/tmp/issue779-generated-doc-final-118c/`.
The final narrow core and native adversarial repair reviews found no actionable static
defect; they do not replace the required full coverage or actual Ubuntu mechanism proof.
A fresh unchanged full wrapper remains mandatory before any draft publication.

The six failed Docs prerequisite controls then passed **6/6**, exit **0**, in 6.366
seconds, and the auth HTTP theory passed **3/3**, exit **0**, in 6.126 seconds. Both
used the existing Debug assemblies, collector and unchanged deadlines/environment.
Their owned process groups were absent after completion. Evidence is under
`/private/tmp/issue779-docs-startup-recovered/` and
`/private/tmp/issue779-auth-startup-recovered/`. This supports one new unchanged full
wrapper attempt after recovery; it does not establish a causal source repair.

A diagnostic-only threshold check on the preserved attempt-8 Cobertura at the same
production/test source exited **0**, measuring aggregate **95.11% line / 89.04% branch**
and patch **98.42% line / 100.00% branch**, using the unchanged thresholds, tolerance,
`origin/main` and `codecov` mode. It writes only to
`/private/tmp/issue779-coverage-attempt8-threshold-only/`. The failed full wrapper remains
failed. No publication or coverage exception follows from this diagnostic result.


### Coverage attempt 10 and process-stage diagnosis

The unchanged `./scripts/coverage-solution.sh` ran at clean `c236fadae5424feeaefbdc28a193055bc7761980`
against `origin/main` at `4aa8329c76f4279ad319747f9f3c14b9b8de51ef`. All **54/54** slots
completed; the build had **zero warnings/errors**. The wrapper exited **1 / ASCOV120**
with **8 failed tests**: seven AdoptionMetrics Git-process controls and one RazorWire
`SeparateVisit_ArbitratesTurboBarWithPendingFrameForm(useFallback: False)` navigation wait.
JUnit totals are **15,020 passed / 8 failed / 4 skipped**. Raw merged coverage is
**95.09% line / 89.03% branch**; the official aggregate/patch gate was not reached.
The immutable archive is `/private/tmp/issue779-coverage-attempt10-evidence/`; both recorded
wrapper owner PIDs were absent at the terminal check. This attempt is failed.

AdoptionMetrics had passed **57/57**, exit **0**, in 13 seconds directly before attempt 10.
The recurring failures report the initial checkout timeout, before the expected source
probe, error output or heartbeat marker. A diagnostic-only copy of the verifier in
`/private/tmp/issue779-adoption-stage-trace/` reproduced this with both direct and CliWrap
nested launches: `Process.Start` and ownership attachment completed in about 20 ms,
then `WaitForExitAsync` consumed the original one-second deadline without fixture entry.
A fresh script's sampled process tree showed Bash and its `/bin/sh` child alive before
the first statement. Later unchanged launches recovered. Bash tracing, explicit interpreter
and a longer sampling deadline were temporary diagnostic variants only; none is a production
repair or repository validation pass. One sampler reached no usable stack before child exit.
This narrows the observed failure to pre-entry startup but establishes no exact cause.

The actual CLI coverage runner then passed the **unchanged entire AdoptionMetrics project
57/57**, exit **0**, in **19.693 seconds**, with its collector, JUnit and default hang policy.
Command: `dotnet Cli/ForgeTrust.AppSurface.Cli/bin/Debug/net10.0/ForgeTrust.AppSurface.Cli.dll coverage run --test-project tools/ForgeTrust.AppSurface.Durable.AdoptionMetrics.Tests/ForgeTrust.AppSurface.Durable.AdoptionMetrics.Tests.csproj --output /private/tmp/issue779-adoption-native-runner/output-owned --configuration Debug --no-build --no-restore --test-results junit --slow-test-diagnostics`.
The first diagnostic launch mistakenly placed receipt files in the output root and exited
`ASCOV109` before testing; its corrected launch used an absent child root. Receipts are
under `/private/tmp/issue779-adoption-native-runner/`. No environment override was used;
`BASH_ENV` was absent, stdin was `/dev/null`, no TTY was attached, and the owned process
group was absent afterward. This tests the actual runner path, not the full-solution gate.

The two RazorWire navigation theory controls passed **2/2**, exit **0**, in **9.385 seconds**:
`dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj --no-build --no-restore --filter FullyQualifiedName~SeparateVisit_ArbitratesTurboBarWithPendingFrameForm --collect:'XPlat Code Coverage' --results-directory /private/tmp/issue779-turbobar-repro/results --logger 'trx;LogFileName=focused.trx' --logger 'console;verbosity=normal'`.
The original failure occurred after all loading/bar assertions, while waiting for navigation
load; no retained screenshot/trace establishes a product race or a test synchronization defect.
Read-only findings are `/private/tmp/issue779-turbobar-failure-review.md`; no suggested wait
replacement was applied. The focused process group was absent afterward.

No product/test, deadline, skip, environment requirement, coverage threshold or comparison
base was changed. These completed recovery checks support one fresh unchanged full attempt 11;
they do not waive its gate. Preliminary draft creation and all post-Ubuntu integration
remain pending as defined in the [staged scope](issue-779-preliminary-proof.md).

### Coverage attempt 11 and focused startup diagnosis

The unchanged `./scripts/coverage-solution.sh` ran at clean
`0b451392f3517ef84ccf3a367b3f2b5bfafd54f7`, against `origin/main` at
`4aa8329c76f4279ad319747f9f3c14b9b8de51ef`. All **54/54** projects completed;
the build reported **zero warnings/errors**. The wrapper exited **1 / ASCOV120**,
with **17 failed / 44 passed** in AuthAspNetCoreDevAuthExample.Tests and the other
53 projects successful. JUnit totals are **15,011 passed / 17 failed / 4 skipped**.
Raw merged coverage is **95.11% line / 89.04% branch**. The official aggregate and
patch gates were not reached; their exit and patch measurements remain unavailable.
Duration was 1,442.952 seconds. The terminal archive is
`/private/tmp/issue779-coverage-attempt11-evidence/`; the wrapper's owned process group
and the fixture child PIDs named in its diagnostics were absent at the final check.

Most auth failures reached `PREFLIGHT,BUILD` but emitted no fixture `build` event.
Some reached `LAUNCH,READINESS` or later stages. These observations locate the
failures more narrowly than a general timeout, but do not establish a shared cause.
The full wrapper remains failed. A fresh scoped Enhance invocation, with the default
four-cycle budget, is investigating this exact evidence through the actual CLI
collector runner and owned-process sampling. No production or test repair has been
selected, and no deadline, skip, environment requirement or gate policy was changed.

Private preparation also repaired the saved-artifact consistency verifier against
an observed GitHub API response: an unqualified workflow path is accepted, the PR
head is distinct from the workflow merge checkout, and the exact downloaded ZIP
digest and extracted report bytes are checked. Synthetic controls exited **0**;
these fabricated fixtures establish neither CI execution nor runtime admission.
The verifier and invocation notes are under
`/private/tmp/issue779-proof-acceptance-preparation/`. Actual Ubuntu observations,
preliminary publication and all remaining protected integration are still required.


### Coverage attempt 12 and collector recovery

The unchanged `./scripts/coverage-solution.sh` ran at clean
`50b4d533dd5ae02e5fa3421e5c8072bc55dda56d`, against `origin/main` at
`4aa8329c76f4279ad319747f9f3c14b9b8de51ef`. All **54/54** projects completed;
the build reported **zero warnings/errors**. All tests passed: **15,028 passed,
0 failed, 4 skipped**. The wrapper nevertheless exited **1 / ASCOV115**, because
Coverlet threw `EndOfStreamException` while collecting RazorWire.Tests coverage
and that project produced zero Cobertura files. The official aggregate and patch
gates were not reached. Duration was 1,180.447 seconds; the owned process group
was absent at completion. The immutable archive is
`/private/tmp/issue779-coverage-attempt12-evidence/`, with manifest SHA-256
`832fc5a9279740b50336ec1d9ee3812c39a6948738fca67bd203d82011711e1b`.

The retained RazorWire hit file was zero bytes. Upstream Coverlet 10.0.1
[`Coverage.CalculateCoverage`](https://github.com/coverlet-coverage/coverlet/blob/v10.0.1/src/coverlet.core/Coverage.cs#L424)
reads its initial integer header at the stack's failing line. This establishes the
immediate missing-data condition, but not why the file was empty. A copy and metadata
are retained under `/private/tmp/issue779-collector-source-diagnosis/`; neither the
original file nor the collector package was changed.

One unchanged whole-project recovery through the actual CLI collector runner passed
**545/545**, exit **0**, in **28.929 seconds**, with raw, normalized and merged
Cobertura reports and no surviving owned processes. Command:
`dotnet Cli/ForgeTrust.AppSurface.Cli/bin/Debug/net10.0/ForgeTrust.AppSurface.Cli.dll coverage run --test-project Web/ForgeTrust.RazorWire.Tests/ForgeTrust.RazorWire.Tests.csproj --output /private/tmp/issue779-razorwire-collector-recovery/output-owned --configuration Debug --no-build --no-restore --test-results junit --slow-test-diagnostics`.
Receipts and the archive are under `/private/tmp/issue779-razorwire-collector-recovery/`.
This recovery does not replace the failed full wrapper.

The preparatory coverage and documentation audits identified two bounded corrections:
four public-boundary theory cases now exercise the exact 64-level limit and reject
65 levels for both object and array nesting, using span and one-byte nonseekable
stream inputs; the Contracts reference and XML exception comment now name the four
stream exception categories actually normalized. Other custom-stream exceptions
propagate unchanged. The depth and exception contracts have a fresh scoped core and
native adversarial review; rebuilt focused validation and a new unchanged full wrapper
remain required. No coverage threshold, comparison base, skip, deadline or environment
requirement was changed. Actual Ubuntu proof and the remaining protected integration
are still pending under the [approved preliminary scope](issue-779-preliminary-proof.md).


Scoped formatting exited **0** and preserved the reviewed source bytes. The rebuilt
CLI Evidence filter then passed **182/182**, exit **0**, with no compiler, analyzer
or XML documentation warnings. Commands: `dotnet format
Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore
--include Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceJsonInputTests.cs
Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs`, then
`dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj
--no-restore --filter FullyQualifiedName~Evidence --logger 'console;verbosity=minimal'`.
Receipts are under `/private/tmp/issue779-depth-focused-validation/` and
`/private/tmp/issue779-depth-focused-parent/`. The scoped adversarial review report is
`/private/tmp/issue779-depth-adversarial-review.md`. These checks cover the four new
boundary controls and existing Evidence behavior; the full coverage gate remains required.

### Coverage attempt 13 and packed depth/exception proof

The exact unchanged `./scripts/coverage-solution.sh` completed at
`75f5f876adc1423357892f61057b2a4919180ee9`, against
`origin/main` `4aa8329c76f4279ad319747f9f3c14b9b8de51ef`, with exit **0** at
2026-10-01T12:23:37Z. The build reported **0 warnings and 0 errors**;
**15,032 tests passed, 0 failed and 4 skipped**. The official gate passed with
aggregate line/branch coverage **95.112% / 89.0425%** and committed-patch
line/branch coverage **98.419% / 100%**. Thresholds, tolerance, comparison base,
non-sandbox requirement and configured skips were unchanged.

The archive is `/private/tmp/issue779-coverage-attempt13-evidence/`; all **288**
manifest entries were verified against their SHA-256 hashes. The original terminal
receipt records thirteen orphaned MSBuild nodes. After inspection confirmed they
belonged to the task's process group **6400**, the parent stopped that group and
confirmed it absent. The separate cleanup receipt is
`/private/tmp/issue779-coverage-attempt13/owned-group-cleanup.json`; archived evidence
was preserved unchanged.

The isolated packed Contracts consumer also passed at this revision using exact
version `0.1.0-issue779-proof.20261001.7`. It verified span and nonseekable-stream
depth boundaries, all four documented normalized stream failures, cancellation
and custom-exception propagation, package/README/XML identity and matching
package/cache/consumer DLL bytes. Pack and consumer restore/build/run exited **0**
with no compiler warnings. The final receipt is
`/private/tmp/issue779-packed-contracts-current-75f5/attempt-04/receipts/final-proof-receipt.json`.
Earlier harness failures and corrected audits remain retained; they were not
product failures or passing proof. The scoped enhancement loop completed after
two of four available cycles, before its outside-review checkpoint.

The preliminary draft still requires its fresh Ship audits, reviews, documentation
audit and installed CLI checks. This local gate and packed proof do not establish
Ubuntu supervision or protected runtime admission. The actual Ubuntu run and all
remaining shared admission, lifecycle, caller and acceptance work remain required
under the [staged plan](issue-779-preliminary-proof.md).


### Preliminary Ship verification before publication

Fresh Ship coverage and plan audits found **84% value-weighted / 92% any-test**
coverage across 25 behavior groups and **4/4 preliminary implementation items**
complete. Two assertion-quality weaknesses and two actual-Linux execution gaps remain
explicit; no generated test or observed red/base-proof result is claimed. The unchanged
full repository gate above remains the required measured coverage proof.

The isolated installed CLI candidate at exact package version
`0.1.0-issue779-proof.20261001.7` passed **20 CLI cases** covering initialization,
help/version, planning doctor, explain, duplicate/invalid UTF-8/enum/null input and
safe policy/plan/manifest diagnostics. The strict Linux subject-result verifier passed
**4 Python tests**. The packaged Contracts consumer passed **37 observations**.
All package, shim, source and command receipts were independently hash-checked;
those checks do not substitute for an actual Ubuntu run.

Fresh core, six specialist and native adversarial reviews completed. Their binding
snapshots match. Optional enum-name caching and historical compatibility wording were
retained as advisory notes. The native review retained one investigation: verify the
actual runner and credential scope before relying on PR-controlled root proof or merging.
The workflow uses a standard Ubuntu hosted label, read-only repository permission and
non-persisted checkout credentials. Repository-level runner inventory reported zero
self-hosted runners; organization-level inventory was inaccessible. The actual run
metadata remains to be inspected after draft creation. The native fixture review was
limited to summaries; the configured outside Claude Code command was unavailable.
Neither gap is represented as a completed outside review or protected admission.

The [preliminary scope](issue-779-preliminary-proof.md), actual Ubuntu proof and full
[57-group continuation](issue-779-evidencehost-test-plan.md) remain distinct milestones.
The draft references #779 without closing it. Public reader behavior is documented in
[Contracts](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input),
and release guidance follows the existing append-only unreleased entry.


### Resume on 2026-10-02

On resume, the earlier `/private/tmp/issue779-*` evidence directories were absent.
The interrupted coverage/replay agent handles were missing, and no matching coverage
or CLI replay process was live. Prior results above remain historical observations;
the missing archives cannot support current receipt reuse. Fresh verification is being
retained under the ignored `TestResults/issue779-recovery-20261002/` workspace directory.

The two Ship-owned documentation audits returned incomplete: full reads of the long
design, execution history and CLI reference were not completed. The adoption/status
finding was corrected: the start guide and CLI reference explicitly mark protected
PR/release gating unadmitted. Public design references now use repository-relative
source paths and historical archive filenames. No runtime, test or gate policy changed.
The Ship documentation attempt limit is exhausted; publication requires the specific
named documentation-risk decision while all unwaivable validation gates remain required.
Actual Ubuntu proof and the full #779 continuation remain pending.

### Verified mechanism and shared implementation continuation (2026-10-02)

[Draft PR #850](https://github.com/forge-trust/AppSurface/pull/850) now exists and is attached to the task. The parent independently verified the actual Ubuntu mechanism run and immutable artifact bindings recorded in the [consumer acceptance record](../evidence/issue779-consumer-acceptance.md#actual-ubuntu-mechanism-observation-2026-10-02). The source head remains `4dd992ec`; the successful mechanism run admits no platform or runtime. Prior documentation-cap decisions D1 and D2 were answered A and their final targeted audit accepted; no publication question remains pending from that preparatory pass.

Shared internal implementation is now in progress: explicit modes, non-public admission minting, exact protected plan/catalogue binding, capability activation and single-use completion, and legacy Boolean/status-enum builder rejection. The first focused admission run passed 20 tests. This is a partial local result; the old callers and their tests are being migrated. New source changes invalidate reuse of the previous green solution coverage result. The unchanged coverage gate must pass again before updating the published implementation.

Disjoint workers own monotonic and byte budgets, Linux retained-handle storage, tracked callback stop/join-or-fatal lifecycle, protected Linux launcher/broker and Aspire caller migration. The parent owns shared admission/provider integration and CLI integration. The broader preliminary PR build has a terminal failure under investigation; no retry or waiver has been inferred. All full acceptance work remains required.

The broader preliminary build failure was traced to `VerifierContractTests.RepeatedSignal_IsMaskedUntilTheOwnedChildIsReaped` for SIGINT in the dev-auth example. Its 12-second process timeout expired with two owned processes still running; fixture cleanup then observed verifier exit. The failing step was the unchanged `./scripts/coverage-solution.sh`, with `ASCOV120` as its summary code. All other 53 project summaries passed. The test and verifier script are unchanged by the preliminary slice; this observation does not determine whether scheduling or a latent signal defect caused it. A focused Linux reproduction is required before repair or any rerun claim.

### Full integration recovery and focused results (2026-10-02)

The current CLI Evidence focused run exited **0**, **289 passed / 0 failed / 0 skipped**.
The neighboring coverage process-runner checks exited **0**, **11 passed / 0 failed / 0 skipped**
against that built candidate. Five isolated lifecycle cases also passed, including stalled
callbacks, tracked write/pump work and disposal fail-stop, plus cooperative failure collection.
The real Linux C# control client passed **16 protocol mechanism cases** with process/container
exits **0/0**, including root peer credentials, broker PID replacement, declaration/response
limits and multi-chunk reports. The libc `SO_PEERCRED` call replaces an unsupported .NET socket
option; this is client mechanism evidence, not systemd or consumer admission.

The Aspire focused run exited **1**, **35 passed / 4 failed / 0 skipped**. Its four failures
cover failure-manifest collection, first verifier diagnostic and invalid producer output;
repairs are in progress. Initial broker authentication now has its own thirty-second maximum
linked to caller cancellation. These subsequent edits invalidate reuse of focused and coverage
results until rerun. Detailed current logs and hash receipt are retained under
`TestResults/issue779-recovery-20261002/integration-pass2/`.

The full production Observation fixture and candidate runtime workflow are being completed.
They cannot supply protected-base/fork acceptance or enable Trusted support. The production
allowlists remain empty, and application startup still requires the proved restricted child
and capability map. The entire 57-group plan, final enhancement, unchanged coverage gate,
packed caller QA, documentation verification and validated draft update remain required.

### Shared enhancement cycle 1 and coverage attempt 18 (2026-10-02)

The unchanged `./scripts/coverage-solution.sh` completed all 54 projects, exited **1 / ASCOV120**,
and recorded **15,178 passed / 7 failed / 4 existing skips**. Its owned process group 57995 was
empty at completion. Raw aggregate coverage was **94.3816% line / 87.8938% branch**; the wrapper
did not reach its aggregate/patch gate. No current gate pass is asserted. The archived reports and
279-file checksum manifest are retained under the ignored `TestResults/issue779-recovery-20261002/coverage18/`.

Six current fixture-path constructions were corrected to the repository's shared `TestPathUtils.PathUnder`.
The ignored generated dev-auth checkout had also been harvested by package, lockfile, path-policy and
documentation tests. That directory alone was moved, preserving its inode and contents, to
`/private/tmp/issue779-recovery-20261002-dev-auth-repro-repo`; a relocation receipt remains beside
the original fixture location. Both failed documentation controls then passed unchanged, **2/2**, exit **0**.
The path-policy controls passed **15/15**, exit **0**. Native locked restore with the existing NuGet audit
policy passed, exit **0**, after requesting native cache access; the earlier cache warnings were not waived.

Review cycle 1 corrected two concrete defects: the protected Aspire start allowance now reaches every
shared start stage, and monotonic completion checks reject late callbacks, tracked writes, collection,
stop acknowledgements and disposal despite delayed timer delivery. Timer notifications round fractional
milliseconds upward while acceptance retains the exact original deadline. New controls include one tick
before, exactly at, and one tick after stage/collection/stop/disposal boundaries. Lowered Aspire start
limits of 1, 60 and 120 seconds pass; 0 and 121 reject before configuration. The combined Aspire Evidence
filter passed **71/71**, exit **0**, with no warning diagnostics. CLI Evidence plus the failed lockfile control
passed **309/309**, exit **0**, with no warning diagnostics. Scoped formatting and `git diff --check` passed.
The pure Python launcher suite passed **35/35**, exit **0**; driver source binding and launcher-budget
validation also passed. These results overlap earlier runs and are not distinct acceptance-group totals.

The scoped native deadline review found no correctness regression. Actual isolated fatal-process, current
Ubuntu production Observation, protected/fork downstream, packed callers and all remaining acceptance
groups remain required. Linux-only paths account for substantial uncovered source in the macOS report;
an honest Linux verification lane is being prepared. No threshold, comparison base, exclusion, skip or
production support allowlist was changed. The shared implementation remains uncommitted and unpushed;
the existing draft remains the preliminary mechanism slice until the required current gates pass.

A second ignored generated Linux harness was also discovered by PackageIndex and preserved outside the repository
at `/private/tmp/issue779-managed-linux-preserved`, with a relocation receipt. Fresh package tests then exposed
two real missing manifest expectations: Coverage now depends on Contracts for shared received-byte accounting,
and Aspire now depends on Planner for protected re-resolution. The expected dependency lists and generated
readiness document were corrected; the corresponding package documentation was updated. PackageIndex generate,
verify and policy gate exited **0**, and all three previously failed package controls passed **3/3**, exit **0**.
The current isolated lifecycle worker built with **0 warnings/errors** and passed **5/5** actual POSIX worker
checks, exit **0**; PGID 93094 was absent after completion. These include forbidden hash/manifest/disposal markers
on fatal cases and ordered cooperative failure collection. This is not a systemd consumer acceptance run.

### Current retained-root and proof-driver verification (2026-10-02)

The corrected restricted-coverage failure suite passed **10/10**, exit **0**, and the combined CLI
Evidence plus lockfile controls passed **320/320**, exit **0**, with zero warning diagnostics.
Scoped formatting verified both changed test files unchanged. These runs include the Mac unsupported
platform control; they do not execute the Linux retained-root regression or new direct broker caller cases.
Receipts and source hashes are retained in the ignored
`TestResults/issue779-recovery-20261002/enhance-shared-cycle1/restricted-coverage-failure-validation-20261002124201/`.

A further concrete filesystem finding is fixed: a failed later component in `OpenParent` could leak
an already opened intermediate directory descriptor. Exceptional exits now release that ownership,
and the next SafeFileHandle is retained until successful transfer. The new Linux test performs sixteen
nested symlink rejections, checks descriptors naming its unique intermediate directory, then writes and
verifies a neighboring valid artifact. Actual current x64 Linux results remain pending.

The candidate runtime proof now requires exact `ASEVD407` Trusted missing-proof rejection with exit 1,
empty stdout, and no output anchor. Other launcher exceptions retain the safe generic diagnostic.
Its successful Observation path additionally invokes the actual published CLI's structural plan/manifest
verification after protected collection. The launcher unit suite passes **37/37**, and the proof-driver
suite passes **2/2**, including five exit/diagnostic rows and an output-anchor rejection, all exit **0**.
These are local proof-control results; no new actual Ubuntu acceptance is asserted. The private x64
verification image is prepared, and direct production CLI/Aspire broker tests are being added while the
unchanged full coverage wrapper remains required. No threshold, exclusion, schema authority, or Trusted
allowlist has been weakened. Current shared code remains local and the published draft remains preliminary.


### Single-use admission recovery and Linux SDK diagnosis (2026-10-02)

A scoped reviewer confirmed that an Aspire authentication or admission failure could leave
`State` as `Created`, permitting a second call on the same host and stopped control lease.
The host now claims its single execution attempt under `_execution` before authentication
or shared admission. Failure consumes the attempt; null request validation and cancellation
before acquiring execution ownership do not. The broker also rejects `ready` after its
work closure or exit latch while preserving stopped-work artifact collection.

The focused Aspire Evidence filter passed **76/76**, exit **0**, with **zero failures,
skips, or warnings**. It excludes only the still-unverified new production broker fixture
class from this focused run. New deterministic cases cover authentication/admission failure
retry, pre-ownership cancellation, a held producer with concurrent Run/Dispose, and disposal
before execution. The portable launcher suite passed **39/39**, exit **0**, including
handler-level ready/stop/wait/exit and post-stop artifact checks. A narrow independent
re-review found no defect in the host guard. These are library/protocol results, not the
full gate or actual systemd consumer acceptance.

The prepared Linux x64 SDK lane stopped before tests with `MSB4184` in
`Microsoft.NET.Publish.targets`. A minimal SDK project reproduced the failure and an
observed QEMU segmentation fault on the ARM64 Docker engine. Disabling runtime hardware
intrinsics and ReadyToRun did not repair the tiny-project evaluation. The existing x64
runtime is being tested with host-built AnyCPU assemblies; no current Linux pass, unchanged
full-gate pass, source publication, or Trusted enablement is inferred. The receipt and
source hashes are retained under ignored `TestResults/issue779-recovery-20261002/`;
SDK logs are under `TestResults/issue-779/linux-verification-preparation/`.


The direct Linux VSTest alternative also failed before a test result: xUnit discovery
raised a NullReferenceException, followed by AccessViolationException and a QEMU abort,
exit **1**. No pass/skip count was produced. The SDK-only diagnostic then also aborted
inside runtime exception handling, exit **134**. These are infrastructure failures,
not failed EvidenceHost assertions. Completing the Linux checks requires a working
native x64 runner or equivalent stable environment. Microsoft's [.NET 10 support
notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md#notes)
exclude QEMU execution. The user has been asked for native Linux access or an owner-run
bundle; local formatting, packaged caller preparation and documentation continue while
that environment input remains pending.


The current native macOS CLI Evidence filter compiles the new broker caller class and
passes **327/327**, with no failures/skips or warning diagnostics. Linux-only cases
exercise explicit unsupported-platform rejection on this host; their successful Linux
consumer paths remain unverified. The broker fixture forwards the four named sandbox
markers unchanged and clears supplementary groups unless the owner explicitly names
positive disjoint groups. This permits a native runner to execute the unchanged wrapper
without disabling its environment guard. The fixture's synthetic cgroup and subject
results remain explicitly documented.


### Current packaged CLI checks and native validation handoff (2026-10-02)

Release package `0.1.0-issue779-current.20261002.1` built and installed from an
isolated local feed, both exit **0**, with no compiler warnings or disabled NuGet
audit. Its installed tool passed seven checks: Evidence help/run help/worker help,
sample generation, doctor with synthetic `GITHUB_ACTIONS=true`, and two negative
run cases. Doctor kept trust unverified; omitted mode rejected with `ASEVD401` and
Observation without authenticated control rejected with `ASEVD402`, both exit **1**,
empty stdout and no fixture output. Package, README, assembly and source hashes
are in the ignored `current-packed-cli/completion.json`. These Darwin arm64
package results are preparation, not a completed clean-baseline QA or Linux/SDK
acceptance decision.

The native validation handoff preserves the current dirty source in an isolated
Git snapshot solely so the unchanged patch gate can compare it with the recorded
`origin/main`. The feature branch and preliminary draft remain unchanged. A source
hash verifier and receipt-preserving runner execute locked restores, required web
dependencies and current Linux fixtures before the exact
`./scripts/coverage-solution.sh`. Its local policy remains aggregate 95% line/85%
branch and patch 95% line/85% branch with the existing tolerance; no base, skip,
threshold, exclusion or sandbox requirement is relaxed. Native Linux execution
is pending. Actual current systemd/consumer Observation, protected/fork/downstream
acceptance, restricted Aspire resources, packed SDK proof and all remaining
approved groups remain required even after a green native gate.


The final current native macOS Aspire Evidence filter passed **81/81**, exit **0**,
with zero failures, skips or warnings, including the protected broker class. Scoped
whitespace formatting of the host and caller regression tests exited **0**. The
parent also recovered the current CLI filter process exit **0** from the runtime
record (the watcher had only reported its 327-pass log). These results validate
compilation, shared behavior and unsupported-platform rejection; Linux positive
paths and the exact solution coverage gate remain pending.


### Native GitHub validation and first fixture corrections (2026-10-02)

The previously confirmed AppSurface CI ownership provides a native Linux route.
A separate `codex/issue779-native-validation-20261002` branch contains a validation
harness and an isolated source snapshot; PR850 remains at `4dd992ec`. The harness
checks all source hashes, preserves the recorded comparison base and runs the
unchanged solution wrapper. It is validation work, not a feature publication or
a clean enhancement conclusion.

[Native run37016357414](https://github.com/forge-trust/AppSurface/actions/runs/37016357414)
failed before the gate: 38 launcher cases passed and one fixture error rejected
the unresolved `/usr/bin/dotnet` alias. The fixture now uses a canonical approved
path for both configuration and request; the production guard remains unchanged.
The launcher suite passes 39 locally. The source before/after checks matched on
that run; artifact11229473438 retains the actual logs. No C# or coverage pass was
produced by this run. The earlier request for a separate native host no longer
blocks this validation route.

A portable descriptor regression exposed and repairs an independent fixture
NameError: the broker constructor now retains its unique root/scenario run ID
rather than referencing `main`'s local variable from a method. Descriptor creation
is verified for every configured scenario and authenticated peer/root binding.
The CLI's authenticated caller-mode check is moved inside owned execution cleanup
so rejected mode conflicts stop and join before returning `ASEVD401`. A paired
production broker regression requires `ready -> stop -> wait` with no subject or
artifact operations and no output allocation. Native execution remains pending.

Three touched Evidence package projects duplicated the implicit README item
already packed by `Directory.Build.targets`, producing existing `NU5118` warnings.
Their redundant Include item groups are removed; the shared packing rule and
PackageReadmeFile remain. Fresh exact-version SDK package/README verification is
in progress without warning suppression or disabled NuGet audit. These changes
invalidate the first source bundle and require a fresh candidate and native gate.


After the CLI cleanup correction, scoped whitespace formatting passed and the
current native macOS CLI Evidence filter passed **328/328**, exit **0**, with no
failures, skips or warnings. This includes the new mode-conflict fixture class
under the explicit unsupported-platform guard; the Linux operation sequence
remains pending. The descriptor regression passes one test across all twelve
scenario rows. A revised Linux runner executes focused CLI/Aspire consumer checks
under a fresh root broker before the longer exact solution gate, which gets its
own new broker. Focused success does not replace the unchanged gate.

### Native caller failures and focused recovery (2026-10-02)

[Native run 37018837795](https://github.com/forge-trust/AppSurface/actions/runs/37018837795)
verified all 2707 captured source files before and after execution. It passed 39 launcher,
two runtime-driver, one twelve-scenario descriptor, five POSIX lifecycle and sixteen
control-protocol mechanism cases. The CLI Evidence filter recorded **316 passed / 12 failed**,
including passing native retained-root and mode-conflict cleanup regressions. Aspire and
the unchanged solution gate were not reached. Artifact `11231723272` retains the logs.

Nine failures reproduced with the broker fixture's execute-only temporary ancestors:
the shared coverage output lease opens each ancestor with `O_RDONLY | O_DIRECTORY`.
The same built tests passed **9/9** with readable ancestors and failed **9/9** with
execute-only ancestors. The fixture now grants the worker group read/search on its
root-owned parent and worker-root directories without granting write or subject access.
The protected report data and production path guards are unchanged. The output-quota
assertion now requires the host's discarded terminal callback result, no closed obligation
and no gate eligibility. Bounded synthetic diagnostics make future caller failures visible.

A confirmed manifest defect required absent reports even after producer failure, making
otherwise valid unsuccessful runs `Invalid`. Structural construction now permits missing
required slots for unsuccessful outcomes and reports `Incomplete`; returned partial
metadata remains fully validated. Public artifact validation and successful producer
completeness remain strict. The focused planner suite passed **46/46**, exit **0**, with no
warnings, failures or skips, including missing-report and malformed-partial regressions.

[Current systemd run 37019675947](https://github.com/forge-trust/AppSurface/actions/runs/37019675947)
stopped before launch because publish did not emit the build summary required by the
proof driver. The driver now verifies an explicit warning-as-error, zero-warning CLI
build before publishing that exact configuration without rebuilding, and retains six
raw build/publish streams with their combined digest. The driver's portable controls
pass **9/9**, exit **0**. Its generated private parent becomes root-owned and traversable
before launch; protected tool/output children retain their narrower launcher permissions.
The disposable runner retains that private workspace for diagnosis. A fresh source
snapshot, native caller run, actual systemd Observation and unchanged solution gate are
still required. No current acceptance decision or Trusted support is asserted.

The direct broker's empty-packages Cobertura fixture exposed an independent positive-control
error: ReportGenerator exited zero but recalculated zero valid items, which the actual CLI
numeric gate rejected with `ASCOV006`. A private measured-class candidate with one covered
line and a fully covered two-way branch passed the exact pinned reporter and actual gate,
both exit **0**, at **100% line / 100% branch**. The meaningful report replaces only synthetic
fixture data. Separately, that measured report still rejects execute-only ancestors with
`ASCOV019`/`ASCOV109`, confirming that numerical and permission failures are distinct.

The native build also exposed a pre-existing Tailwind warning: ANSI SGR styling prevented
the stderr classifier from recognizing its informational version banner. Classification
now ignores only complete SGR sequences, preserving raw output and error severity for
real warnings and unknown or incomplete controls. Focused process/classifier tests pass
**40/40**, exit **0**, with no warnings or skips. No warning suppression or coverage
policy change was used.

### Native candidate results and invalid-outcome regression (2026-10-02)

[Native run 37024942137](https://github.com/forge-trust/AppSurface/actions/runs/37024942137)
used private source snapshot `43e826b11ece70845ca1eabdec79679a9865d797` and verified
all 2707 captured source files before and after execution. The launcher controls
passed **39/39**, runtime-driver controls **9/9**, descriptor controls **3/3**,
POSIX lifecycle controls **5/5**, and control-protocol controls **16/16**. Its CLI
Evidence filter reported **336 passed / 1 failed**, with no skips. The positive
broker case produced a Passed Observation manifest, completed cleanup, closed its
obligation, and passed the numeric coverage gate at **100% line / 100% branch**.
Its remaining failure was the test's physical report lookup: artifact metadata is
relative to a producer directory, so the lookup must include `ProducerId`. This
does not change the writer layout or manifest schema. Artifact `11235321545`
retains the actual failure and source checks. The focused Aspire selection and
the exact `./scripts/coverage-solution.sh` gate were **not reached**.

[Systemd run 37025006612](https://github.com/forge-trust/AppSurface/actions/runs/37025006612)
used the same source snapshot. All six subject-build, CLI-build and publish
streams contained zero warnings or errors. The production Observation launcher
then failed with the generic `launcher-failed` diagnostic; no completed runtime
proof was produced. Artifact `11234757444` retains those actual logs. A bounded
private host-category diagnostic and a pre-reserved private structural-verification
directory are being added to make the next failure actionable without publishing
subject output or exception text. This failed run establishes no runtime acceptance.

Review found a separate structural regression after allowing absent reports on
unsuccessful producers: an explicit `Invalid` outcome could become `Incomplete`
when finalization removed invalid artifact metadata. The structural builder now
preserves explicit `Invalid`, and both the public builder and existing Aspire
artifact-tampering regression assert `Invalid` with `None` claim and eligibility.
Source-built macOS planner tests passed **47/47** and the exact Aspire tampering
test passed **1/1**, both exit **0**, with no warnings, failures or skips. Scoped
formatting exited **0** without changing the Aspire test bytes. The combined
validation and formatting took **22.602 seconds**, without timeout. The local-only
receipt is `TestResults/issue779-recovery-20261002/native-validation/invalid-outcome-validation/receipt.json`.

Packed candidate `0.1.0-issue779-current.20261002.2` passed 67 modern SDK assertions,
two legacy assertions, its expected two internal-access compiler errors, and
seven installed CLI checks. All six packed README files matched source; SDK and
tool Evidence assemblies matched. The legacy build produced only its expected
unsuppressed obsolete-API warning. These checks bind their earlier captured source;
the subsequent Contracts regression fix requires fresh packages. Current Linux
caller execution, the unchanged solution gate, the actual systemd runtime proof,
restricted Aspire resources and protected downstream acceptance remain required.

The runtime diagnostic fix is source-clear: portable launcher controls passed
**45/45** and runtime-driver controls **15/15**, both exit **0**. The optional
private receipt permits only fixed host categories and bounded numeric systemd
status. It uses exclusive no-follow creation beneath a pinned protected parent;
the driver publishes only validated fields. Default public launcher diagnostics
remain unchanged. The structural-verification child is created privately before
root ownership of its parent, then checked before writing fresh collected copies.
These portable controls do not substitute for the next actual systemd run.

The [full-source native gate](https://github.com/forge-trust/AppSurface/actions/runs/37037592365)
reached the unchanged solution gate: 15269 tests passed, none failed and two
existing tests were skipped. Aggregate coverage was 94.88% lines and 88.522%
branches, but patch coverage was 82.852% lines and 76.6374% branches. The
required gate exited **1**, `ASCOV020`. Measured worker, budget, protected
producer and host gaps are being addressed through meaningful behavior tests;
the gate policy and comparison base remain unchanged.

The next complete snapshot, `2b7da72fa2eba5d15300052efc5818fb868d7f9d`,
failed [systemd Observation](https://github.com/forge-trust/AppSurface/actions/runs/37039914626)
after namespace setup, with worker exit status 1 and an incomplete protocol.
It also failed all five [Aspire mechanism controls](https://github.com/forge-trust/AppSurface/actions/runs/37039916803).
The store correction reached DCP configuration, revealing the fixture's
incorrect option section. The corrected `DcpPublisher` arguments now build
without warnings, and forty portable child controls pass. A bounded private
identity diagnostic preserves the original startup rejection and its kernel
facts. Fresh source-bound native execution remains required for both paths.

An internal root completion context now retains the authenticated descriptor,
output identities and directory handles for same-parent collection. Portable
collector controls cover actual bytes, links, replacement, modes and bounds;
account lifetime and cleanup ownership are now repaired and reviewed. Launcher
controls passed **72/72** and runtime-driver controls passed **30/30**. A failed
cleanup closes both retained descriptors, and successful collection reserves
the run accounts until completion closes. A fixed root-owned journal is retained
as a bounded private archive while the original failure remains unchanged.
These are portable checks, with no admission authority. Native completion verification, the immutable
controlled release fixture, shared Aspire resource integration and the complete
protected consumer matrix remain required. See the
[acceptance record](../evidence/issue779-consumer-acceptance.md) for the exact
failed runs and their limits.

The complete frozen snapshot `8a964188e85561d57119133d26b552f9412e2445`
also failed [systemd Observation](https://github.com/forge-trust/AppSurface/actions/runs/37047555055)
and [Aspire mechanism execution](https://github.com/forge-trust/AppSurface/actions/runs/37047560583).
All 2725 frozen hashes and dispatched workflow bindings matched. The runtime
worker reached readiness and completed its wait, then reported `ASEVD409`;
the erased internal allocation/activation exception still needs a closed private
diagnostic. Aspire reached DCP and resource startup, where thread creation failed;
task and memory counters are needed to distinguish the cause. Its factory-stall
identity guard correctly rejected an observed root UID, without establishing
factory execution. The [acceptance record](../evidence/issue779-consumer-acceptance.md#frozen-source-allocation-and-resource-startup-failures-2026-10-02)
preserves these failed results and their limits. The [unchanged coverage run](https://github.com/forge-trust/AppSurface/actions/runs/37047553718)
subsequently exited **1** (`ASCOV120`) because the fixture-path policy rejected
a dynamic `Path.Join` in the Bootstrap artifact test. Its measured aggregate
was 94.98% line / 88.79% branch; the patch gate was not reached. There were
15,393 passes, one failure, two existing skips and zero compiler warnings.
The source correction uses `TestPathUtils.PathUnder`, followed by the exact
existing policy regression and a new complete frozen native run.
