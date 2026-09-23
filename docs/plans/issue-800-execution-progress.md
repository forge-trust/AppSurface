# #800 implementation progress

Approved scope: [design](../designs/issue-800-typed-work-definitions.md), [plan](issue-800-typed-work-definitions.md), [verification groups](issue-800-typed-work-definitions-test-plan.md).

- Branch: `codex/make-it-so-800-typed-work`; approved documentation commits preserved; current upstream merged without conflicts on 2026-09-10.
- Implementation underway: shared codec/Work snapshots, definition requests, all three bindings, catalog-first defaults and indexed registry aggregation.
- Independent native `combo/sub` workers: definition/request guards, binding/registry invocation tests, Flow compatibility, PostgreSQL proof, negative packed harness, positive examples/documentation.
- `COVERAGE_GATE=./scripts/coverage-solution.sh`: current repository requires 95% line and 85% branch coverage for aggregate and changed code, default diff base `origin/main`. Gate has not run.
- Runtime prerequisites verified: SDK 10.0.102, Docker available, pinned PostgreSQL 16.5 image present.
- Initial sandboxed build could not write existing Core XML output (CS0016); retry with normal local filesystem permission initiated. Retry with `-p:UseSharedCompilation=false` succeeded; stale compiler-server permissions were the cause. One ambiguous XML cref was corrected. Tests still in progress.
- Remaining: integrate and verify all 20 groups, API baseline/release documentation, enhance review loop, clean baseline commit, SDK/package user-facing QA, fresh coverage gate, ship draft PR.
- Browser QA: not applicable to this library API; packed consumer execution and real PostgreSQL terminal completion are the user-facing validation.

## Validation evidence

- `dotnet build Durable/ForgeTrust.AppSurface.Durable/ForgeTrust.AppSurface.Durable.csproj --no-restore -p:UseSharedCompilation=false`: succeeded; initial ambiguous XML cref fixed subsequently.
- PostgreSQL new definition proof: 4 passed, 0 failed, 0 skipped; `/tmp/issue800-postgresql-focused-final.xml`. Covers persisted fields, equivalent replay, changed defaults/override, rollback and terminal result.
- Package chooser generated successfully from manifest; no unrelated release-guidance regions changed.
- API baseline generation blocked temporarily by compile errors in concurrently authored test/example files; owners correcting their files. No baseline claimed verified.

- First executable full unit pass: 284 tests, 280 passed, 4 failed. Found and fixed raw-descriptor-before-view metadata recapture; aligned legacy null-output regression with the approved shared guard exception. Two new Flow test graphs used an invalid next-node fixture and are being corrected.
- Public API baseline updated: 23 added lines, zero removed; exactly five approved types and three overloads, no internal snapshot exposure.

- Unit integration rerun: 284/284 passed, 0 skipped. PostgreSQL full Release 411/411, Provider Release 27/27, no skips. Four new PostgreSQL tests now compare every stored field after every attempt.
- Packed proof completed all three executable consumers, 8 positive controls and 8 exact negative controls on SDK10.0.102; codes CS0311/CS0452/CS1061 required no extra allowlist. Positive example source subsequently expanded to execute all migration examples/default request; final proof rerun required.

## Review integration

- Shared legacy/typed codec-pair capture now has one implementation with explicit typed capture delegates. Existing tests caught an intermediate untyped-view regression before commit; fixed.
- Codec construction now validates duplicate contracts in O(n), retaining ordinal-minimum diagnostics and source-conflict precedence.
- Review-owned tests strengthen exact getter counts for both sources, registry ordering/effective overrides, and a composed activity-result/wait/resume Flow path.
- Red-team/security/API/simplification reviewers found no additional issues. Independent adversarial and plan audit pending.
- README and migration managed snippets verify on the current sources. Local links in all changed Markdown resolve. Added migration snippet verification to all three applicable CI workflows, including Code Quality.
- Repo release policy is append-only unreleased entries with coordinated version preparation; feature PR uses the required Conventional Commit title and does not invent a standalone VERSION bump.

- Final affected unit/API pass after cycle1: 287/287, zero skips. Local Durable coverage collection measured 97.48% lines / 93.26% branches; this is not the required solution gate.
- `./scripts/coverage-solution.sh` started with `UseSharedCompilation=false` environment and normal filesystem permissions; unchanged 95/85 aggregate and patch thresholds, default origin/main diff base.
- Plan audit found all11 implementation items present, with three source-proof gaps: explicit invalid identifier/control/type matrix, coordinated concurrent factory requests, and an external legacy subclass. Those tests plus measured raw Flow codec guards are being completed; final coverage rerun required.
- Current Debug PackageIndex verify passes. Older Aug20 Release binary failed discovery; no production source change was needed.

## Coverage gate attempt 1

`UseSharedCompilation=false ./scripts/coverage-solution.sh` built the solution successfully and reached test execution. Two RazorWire screenshot tests failed with 695 changed pixels in the release page table of contents: the newly added typed Work release entry is now present. Main visually inspected expected and actual AppSurface Light screenshots; the new entry is the intended content change. The runner will regenerate through the existing explicit baseline-update workflow, inspect changed captures, and rerun with update mode disabled. Thresholds and test selection for the final gate remain unchanged. The first attempt is still collecting other project outcomes; no aggregate/patch gate pass claimed.

Attempt1 completed with 51 project runs; aggregate 94.7236% line / 88.1792% branch. Collection exited unsuccessfully because of two intended release-page screenshot changes. A diagnostic gate-only invocation confirms the repository's existing default tolerance is 0.5 percentage points (effective 94.5/84.5), so the aggregate satisfies policy without changing settings. Patch report at this stage has zero measurable lines and is not usable proof: final commit + fresh collection/gate must include all new production files and nonzero measurable patch coverage.

The zero-measurable patch report was traced to the existing gate's `git diff origin/main...HEAD` behavior: only the approved plan commits were committed at that point. It is expected for this pre-commit diagnostic, not a source-path issue. Final validation follows a complete feature commit and must measure the new .NET code. Fresh origin/main fetch found no new upstream commits (branch five commits ahead, zero behind).

## Final regression validation and baseline readiness

The cycle3 metadata-guard defect is fixed by a shared internal view-selection helper used in Flow evaluation and PostgreSQL startup. The exact source selected by the registry remains the invocation source; captured guards cannot be dropped by selecting raw. Fresh tests: Durable Flow compatibility 21/21, PostgreSQL Flow definition 6/6, visual baseline verification 2/2 with update mode disabled. All changed/new C# files formatted and `git diff --check` passes. Only five intentional release PNG changes remain; home PNG noise removed. Main inspected Light and Graphite release captures, whose differences are bounded to 695 TOC pixels each. Current package index, three managed snippet documents, SARIF checker self-tests and changed Markdown links pass.

Enhance invocation1 completed four review cycles; the final review found no actionable findings. Outside checkpoint pending. Baseline commit and then fresh packed-consumer execution/full solution gate are next. No draft PR or push yet.

Outside checkpoint complete: no blocking defects; no better implementation path identified. Enhancement invocation ended without applying additional advice. Proceeding with the already-authorized clean baseline and executable validation sequence.


## Final required gate: PASS

- Baseline commit: `2872507c3fecc9b57c5288820fd587a2f6df8af4`, clean working tree throughout collection.
- `bash Durable/verify-packed-consumers.sh`: exit 0; three executable consumers, eight positive controls and eight exact negative fixtures, SDK 10.0.102.
- `UseSharedCompilation=false ./scripts/coverage-solution.sh`: exit 0; 51 projects, 12,020 tests, zero failures/errors/skips. Affected suites: Durable 310, PostgreSQL 417, Provider 27. Overall 94.7345% lines / 88.2017% branches; patch 98.2955% lines / 94.3231% branches (519/528 Codecov-mode lines, 432/458 branches). Repository thresholds remain 95/85 with existing 0.5-point tolerance. All executable lines in the four new implementation files were hit.
- `dotnet format ForgeTrust.AppSurface.slnx --verify-no-changes --no-restore`: exit 0; no modifications.
- Evidence: `/tmp/issue800-packed-final.log`, `/tmp/issue800-coverage-final.log`, `/tmp/issue800-format-final.log`, and `TestResults/coverage-merged/coverage-gate.json` plus all 51 JUnit files.
- Fresh plan audit: all 11 implementation facets and 20 verification groups fulfilled after the terminal gate result. No missing implementation or concrete test-path gaps; historical timing/adopter-evidence TODO remains deferred by the approved plan.
- Current upstream already merged. Ship-stage reviews and draft PR publication remain; source/test edits would invalidate this coverage evidence and require the exact gate again.
