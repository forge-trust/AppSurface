# Issue859 implementation evidence

Case: https://github.com/forge-trust/AppSurface/issues/859
Base: 3bce0bdbef5e7ae3137f0bb90dbd3d0ff9f08183
Branch: codex/make-it-so-859-evidencehost-cleanup

## Scope

Consumer runs Evidence.Aspire host and receives bounded terminal output or the original execution failure with a safe cleanup diagnostic. Execution and cleanup budgets, callback join, explicit stop/join, dependency retention, persistent direct disposal failure, and enrolledprocess exit are implemented. Startup, upload, detached discovery and runner recovery remain independently supervised consumer responsibilities. No Skoolit changes or deployment; draftPR delivery only. Public package publication is release-owner work.

## Validation

- Baseline controlled diagnostic reproduced RunAsync pending after caller cancellation during gated disposal; allfixturegates were released and joined. Not a red regression claim.
- Enhance cycles1-2 found six lifecycle/process issues corrected; cycle3 reviewed lifecycle paths with no new findings, broader review scope incomplete. Adversarial review subsequently identified hidden error-unwind cleanup failure, corrected with public CleanupDiagnostic and join of already-failed callbacks while preserving the primary exception.
- Two cycle2 regressions failed on the pre-fix implementation and passed after correction. Diagnostic property-only implementation failed its nonnull cleanup assertion; the joined-callback correction passed.
- Current Aspire project:105passed,0failed,0skipped. Four process integration tests pass on macOS. Supplemental Linux proof pending prerequisite installation.
- Full solution build:0warnings/errors. Initial concurrent fulltest had auth timing failure, release screenshot drift and6path-collision leak assertions. Auth passed in serial coverage; five release baselines deliberately refreshed for upstreammain's existing navigation heading, and6leakchecks passed with distinctive canaries.
- Packed candidate initial current proof:3passingarms; diagnostic/fourtharm added afterward, final repeat pending. Candidate proof establishes packed artifact, not publicNuGet release.
- First coverageattempt cancelled and superseded after production repair; not valid gate evidence. Required unchangedgate ./scripts/coverage-solution.sh is running on committed candidate8d7ffbf6, 95/85 aggregate and95/85 patch againstorigin/main, no threshold or selection changes.

## Remaining

Final packedconsumer QA; current code/adversarial and documentation audits; fullcoveragegate and scopedfixes ifneeded; draftPR withFixes859 afterallrequiredgatespass.
