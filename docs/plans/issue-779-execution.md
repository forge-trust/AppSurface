# #779 Make It So execution record

Goal: implement the [approved EvidenceHost plan](../designs/issue-779-evidencehost-trust-boundary.md) through a validated **draft** PR. Preserve the shared admission contract, zero protected secrets, mandatory stop/join-or-fatal ordering, and all [57 acceptance groups](issue-779-evidencehost-test-plan.md).

## Current state

- Dedicated branch: `codex/make-it-so-evidencehost-779`, initially from `5dc141db81009348538d3f6b7634a99ccb34cd99`, fast-forwarded to `origin/main` at `fd0b124e16a35b85ee16acde799834af407e18de`. Existing approved plan, test-plan and `TODOS.md` changes belong to #779 and are preserved.
- GitHub access: repository `forge-trust/AppSurface`, default branch `main`, viewer permission `ADMIN`; issue #779 remains open. This does not confer a recorded CI-owner acceptance.
- Durable Make It So goal exists. The user has resolved the prior ownership blocker; work continues toward the same objective. No implementation commit or PR exists yet.
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
