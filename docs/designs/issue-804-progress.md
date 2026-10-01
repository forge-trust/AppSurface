# Issue 804 implementation progress

Approved scope: [external activation plan](issue-804-external-activation.md).
Feature branch: `codex/make-it-so-804-external-activation`, initially based on `fd0b124e`.
The current base is `4aa8329c`. Four pre-existing plan-review artifacts were preserved byte-for-byte at `/private/tmp/issue-804-preserved-plan-review-8YsHpT` so task commits exclude generated review output.
The [completion audit](../plans/issue-804-plan-completion-audit.md) maps B01–B31 to implementation and proof sources.

- Provider contracts, service, per-invocation ownership, passive registration, cancellation, failure, DI and telemetry verification are implemented. The full Provider suite passed 82/82 with zero skips in `/private/tmp/issue-804-provider-full-rerun.log`.
- The reference host has four routes, out-of-band `accept-demo-work`, explicit response DTOs, and mandatory authentication alongside its named policy. Registration and acceptance use the same captured `DurableWorkDefinition`; the lifecycle test asserts that definition's DI identity.
- Restricted PostgreSQL lifecycle, persisted Work/readiness/export, drain, outage, lost-response recovery, and concurrent generic-wake receipt inspection tests are present. The fresh PostgreSQL package suite passed 545 tests with one mixed-version test skipped because its previous-package input was absent, in `/private/tmp/issue-804-postgresql-final-cycle4.log`. The packed verifier must supply that input and prove the release contract without skips.
- The HTTP owner patched transport/observer ordering in the two test files after a 136/137 run exposed the actual Kestrel disconnect regression. The focused slow/chunked loopback pair passed 2/2. A sandboxed full run stalled during real PostgreSQL CLI acceptance; the unchanged full suite then passed 137/137 with zero skips outside the sandbox in `/private/tmp/issue-804-host-final-cycle4-unsandboxed.log` (20 seconds), including all 20 compiled documentation-example cases. This is the current host evidence.
- Source/listener/exporter separation, flush/removal controls, CLI acceptance/inspection, all ten adoption outcomes, and all seven permitted `PumpFailed` codes are implemented. Source test success remains separate from exact packed API execution.
- The [canonical guide](../../Durable/external-activation-v1.md), API/operator/migration/recovery docs, and [executable first-start README](../../examples/durable-external-activation/README.md) are present. Actual first-start command/output verification remains pending. Fresh focused docs harvest/render verification passed 1/1 after the wording changes, as reported by the sole runner.
- Official MarkdownSnippets verification passed: `/private/tmp/issue-804-snippet-verify.log` reports `Markdown snippets are up to date.`
- Enhance completed four cycles: entry-budget origin fixed; catalog and no-listener proofs strengthened; mandatory endpoint authentication added; shared typed Work definition adoption completed. The invocation stopped at its outside checkpoint. The read-only checkpoint found no concrete remaining runtime mismatch and advised closing fresh evidence before further broad review. That advice is recorded separately from a clean review.
- Package chooser inputs were updated; generation, verify, and the fresh package gate passed (58 manifest entries and 3,351 source files). Wording and targeted historical/wire-format allowlist repairs resolved the previous false-positive stale-brand rejection.
- Scoped whitespace formatting covered 29 changed C# paths with separate argv entries, changed zero files, and passed `--verify-no-changes`. Fresh test builds report no introduced compiler/analyzer warnings. Clean baseline, Standard CLI/API QA, exact source-linked packed proof, final coverage, and draft PR remain pending.

`COVERAGE_GATE=./scripts/coverage-solution.sh`; repository thresholds remain 95% line and 85% branch,
both aggregate and patch against `origin/main`. Run the exact command after final production/test changes
and again immediately before ship. Current result: not yet run. The activation example PostgreSQL suite
is an exclusive coverage project. No coverage or completion claim is made.

Hypatia remains the sole runner for build/test/format/package/coverage and runtime QA commands.
The HTTP owner's two files are stable after the focused retest and fresh full host pass.

Existing PostgreSQL health reports `NotStarted` with `ASDUR404` before its first heartbeat. Validation
accepts that initial assessment; only observed `Stale` retains stale codes in activation results.
Provider health semantics and the approved activation result matrix are preserved.
