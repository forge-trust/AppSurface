# #779 Make It So execution record

Goal: implement the [approved EvidenceHost plan](../designs/issue-779-evidencehost-trust-boundary.md) through a validated **draft** PR. Preserve the shared admission contract, zero protected secrets, mandatory stop/join-or-fatal ordering, and all [57 acceptance groups](issue-779-evidencehost-test-plan.md).

## Current state

- Dedicated branch: `codex/make-it-so-evidencehost-779`, initially from `5dc141db81009348538d3f6b7634a99ccb34cd99`, fast-forwarded to `origin/main` at `fd0b124e16a35b85ee16acde799834af407e18de`. Existing approved plan, test-plan and `TODOS.md` changes belong to #779 and are preserved.
- GitHub access: repository `forge-trust/AppSurface`, default branch `main`, viewer permission `ADMIN`; issue #779 remains open. This does not confer a recorded CI-owner acceptance.
- Durable Make It So goal exists. The user has resolved the prior ownership blocker; work continues toward the same objective. The preparatory implementation is committed; no PR exists yet.
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
- Draft PR only, Conventional Commit title, `Fixes #779`, attach the created PR to this chat. No push or PR before required gates pass.

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
