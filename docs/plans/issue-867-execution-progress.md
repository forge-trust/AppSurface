# Issue 867 execution progress

Plan: [approved design](../designs/issue-867-evidence-readiness.md). Verification requirements: [test plan](issue-867-readiness-test-plan.md).

## Scope and branch

- Branch: `codex/make-it-so-867-evidence-readiness`; base: `main`.
- Approved design captured in `a55bc385`; integrated `origin/main` at `5c87b13c` through `2a2e5f0e`.
- One neutral BCL-only package; typed fixture and Evidence.Aspire adapters reuse their existing owners. Bootstrap relocation, whole-host cleanup changes, publication and downstream adoption remain outside this change.
- Pinned Skoolit equivalents are acceptance fixtures, not actual adoption or author feedback. No external messages are authorized or sent.

## Verification ledger

| Check | Result |
| --- | --- |
| Neutral package `dotnet build` | Passed, zero warnings/errors during foundation implementation; final verification pending. |
| Neutral deterministic suite | Passed 77/77 after safe cleanup-type recipe additions (including 2 real loopback network). Focused collector: 195/198 lines (98.48%), 77/83 branches (92.77%). Solution gate still pending. |
| Typed fixture and existing builder suites | All 43 fixture tests pass, including application-before-activation cleanup, original deadline through all phases, late transfer, client cleanup failures and disposal reentry. Existing builder full suite pending solution gate. |
| Evidence adapter/host suite | All 23 targeted tests pass: native endpoint shapes, cancellation/deadline phases, concurrent waits, admission closure, shared cleanup failure/reentry, terminal task drain and host outer-deadline ignoring handler. |
| Real Aspire resource proof | Passed all 3 example tests: public fixture with real HTTP, existing native-only repeated build/disposal, and default EvidenceHost health→HTTP→producer→manifest→cleanup under its single lease. |
| Packed neutral and typed programs, closure | Passed isolated restore/build/run, 0 warnings/errors in both consumers; package `0.1.0-issue867.20261016034132.26022`, HEAD `2a2e5f0e` plus explicitly dirty working tree. One duplicate-README pack warning corrected by relying on existing shared pack metadata; final committed replay pending. |
| Two pinned gate equivalents | Passed both startup equivalences using real Aspire/DCP executable resources; pinned Skoolit `a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e`. No downstream adoption or browser/business claim. |
| Package index generate / verify / gate / verify-packages | Generate, verify and gate passed (61 entries). Tool suite passed 1,401 tests, 3 existing platform skips. Artifact proof pending. |
| Formatting and diff whitespace | Pending final implementation. |
| Enhance review and user-facing QA | Pending. |
| `./scripts/coverage-solution.sh` | Pending, hard gate. Aggregate 95% lines / 85% branches; patch 95% lines / 85% branches against `origin/main`, Codecov patch mode. Run outside sandbox without altering policy. |
| Draft PR | Pending all required checks. |

Solution restore completed successfully; native macOS assets were restored after regenerating the Linux example lock.

## Review findings addressed during implementation

- Preserve the original caller token through each startup phase, rather than returning the linked work token.
- Release owned probe clients before a successful startup return. Check cancellation again after response/client disposal.
- Invoke lifecycle cleanup callbacks outside locks and join one shared completion.
- Preserve primary readiness errors if client/builder cleanup also fails; retain only a fixed safe secondary-cleanup category.
- Carry the new dependency into the existing packed-testing closure and both macOS/Linux example lock files.

## Measurement limits

Human onboarding time, cold/warm restore observations, reliability improvement and latency improvement are unmeasured. Controlled handlers establish retry, cancellation and ownership behavior; only real-resource runs establish native startup/allocation/HTTP integration. No package has been published.
