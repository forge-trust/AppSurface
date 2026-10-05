# Issue #859 implementation checkpoint

Case: https://github.com/forge-trust/AppSurface/issues/859
Base: 3bce0bdbef5e7ae3137f0bb90dbd3d0ff9f08183
Branch: codex/make-it-so-859-evidencehost-cleanup

This checkpoint precedes final solution verification and draft publication. The PR records the final gate result.

## Scope and implementation

The consumer receives bounded terminal output or the original execution failure with a safe cleanup diagnostic. Separate execution and cleanup budgets, tracked callback joins, explicit stop/join, dependency retention, persistent direct-disposal failure and enrolled-process exit are implemented. One reference-identity ownership graph combines producer and resource dependencies, preserves intermediary readiness-only registrations, orders dependents before prerequisites, and retains cycles and their prerequisites while allowing unrelated cleanup.

Startup, upload, detached-process discovery and runner recovery remain consumer responsibilities. Delivery is scoped to a validated draft PR and a locally packed candidate proof. Public NuGet publication and released-package consumer adoption remain release-owner work. Skoolit PR #1213 stays separate; no deployment is requested.

## Validation at this checkpoint

- Controlled baseline: caller cancellation during gated disposal left RunAsync pending until the fixture released the gate. All fixture work was subsequently released and joined. This is diagnostic evidence, not a base-green regression claim.
- Focused current suite: 115 Aspire tests passed, no failures or skips, after the ownership, intermediary and process-tree failure corrections, including the cycle-prerequisite retention assertion.
- Regression controls: two earlier lifecycle failures, two shared-role ordering/cycle cases, and the readiness-only intermediary retention assertion failed before their respective corrections and passed after. New APIs have no base-green control claim.
- The final packed candidate passed all four consumer scenarios after the process-tree failure and verifier interruption corrections. The standalone consumer uses one direct Aspire PackageReference, with no source or project references. This does not establish a public package release.
- A process-tree termination failure regression failed before adding the documented AggregateException handling and passed afterward. Other enrolled handles are still stopped and joined; the initiating termination failure remains visible. The verifier interruption fixture confirmed both stage and child survived SIGINT/SIGTERM before repair; interruption and deadline expiry now kill the owned group and reap its leader.
- Four real process tests passed on macOS arm64. A supplemental Linux arm64 run passed 25 cleanup/process tests before the final ownership correction; it is not final Linux verification.
- Six doctor leak assertions used generic canaries colliding with the checkout path; distinct synthetic canaries corrected that fixture. Five release screenshot baselines lacked an existing upstream navigation heading and were deliberately refreshed. Production UI was not changed.
- Formatting and matching-input Markdown/generated-package-doc checks passed. Final matching-input checks and the unchanged full gate remain required.

## Remaining verification and publication gate

Run the exact unchanged ./scripts/coverage-solution.sh on one frozen commit and clean tree: aggregate and patch line/branch targets are 95/85, against origin/main. Prior changing-HEAD, canceled, and overlapping coverage attempts are not final verification. A private single-owner guard prevents concurrent report writers without changing the gate command or thresholds.

Finish bound native code and adversarial reviews. The documentation audit used both permitted attempts and was current at 45344b2f; later tests, the consumer nullable annotation, the host/README ownership correction, the process-tree termination repair, and the verifier interruption implementation/fixture/README make its selected-input snapshot stale. No third documentation audit was run. The ship workflow requires an explicit operator decision about that exact documentation freshness risk before publication. No exception has been inferred.
