# Issue 867 execution progress

Plan: [approved design](../designs/issue-867-evidence-readiness.md). Verification requirements: [test plan](issue-867-readiness-test-plan.md).

## Scope and branch

- Branch: `codex/make-it-so-867-evidence-readiness`; base: `main`.
- Approved design captured in `a55bc385`; initial integration of `origin/main` at `5c87b13c` through `2a2e5f0e`. Final integration and verification are recorded below.
- One neutral BCL-only package; typed fixture and Evidence.Aspire adapters reuse their existing owners. Bootstrap relocation, whole-host cleanup changes, publication and downstream adoption remain outside this change.
- Pinned Skoolit equivalents are acceptance fixtures, not actual adoption or author feedback. No external messages are authorized or sent.

## Verification ledger

| Check | Result |
| --- | --- |
| Neutral deterministic suite | 77/77 passed. The completed solution collection measured 202/205 lines (98.53%) and 90/95 branches (94.74%) for the neutral package. |
| Typed fixture suite | 45/45 focused tests passed, including ownership, original deadline, cancellation, null build, primary-error preservation and reentry. |
| Evidence adapter and public wrappers | 32 adapter tests and 2 wrapper tests passed. The focused collector run including the host boundary passed 35/35; adapter coverage was 246/247 lines and 71/74 branches. |
| Real Aspire resource proof | All 4 example cases passed in the second solution run, including default/custom probe clients and the EvidenceHost health → HTTP → producer → manifest → cleanup path. |
| Packed neutral and typed programs | Standard functional QA passed at clean `e91ab9e8`, package `0.1.0-issue867.20261016042507.80091`. The neutral package has no dependencies; the typed program verified first-party archive/cache hashes and two real Aspire/DCP startup equivalents. The committed immutable Git-export replay also passed at clean `bda92c18`, package `0.1.0-issue867.20261016050750.86571`, preserving both real Aspire startup equivalents. |
| Two pinned gate equivalents | Passed both startup equivalents against pinned Skoolit `a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e`. No downstream adoption or browser/business claim. |
| Package catalog and artifacts | At `e91ab9e8`, catalog verify and gate passed (61 entries; 3,634 source paths), and `verify-packages` passed all 50 artifacts for `0.0.0-ci.issue867`. |
| Formatting and diff whitespace | C# whitespace formatting passed. Final proof-script syntax and merge-base diff whitespace checks pass; plan metadata hard-break whitespace was removed. |
| Enhance review and QA | Initial two-cycle review ended clean before its checkpoint. The second pass added failure-path tests and found a package-proof source-attribution race, repaired with an immutable Git export. Focused native rereview completed; its custom artifact-directory hypothesis was disproved in disposable Git repositories because Git ignores the newly created empty directories. The required solution gate remains pending. Standard functional SDK QA passed; no browser UI is introduced. |
| `./scripts/coverage-solution.sh` | Attempt 2 completed all 56 projects at frozen `e91ab9e8`: aggregate 95.37% lines / 89.49% branches, but exit 1 due to three RazorWire integration failures. Two release-page baselines still contain preview.13 content; the approved branch renders preview.14 plus new release navigation. Five prepared release-only candidates passed both visual tests privately with update mode off; the user approved applying these exact five candidates (D1, choice B). A dialog navigation timeout passed its isolated rerun with the original timeout unchanged. The exact full command must pass after the final changes. |
| Coverage policy | Unchanged hard gate: aggregate 95% lines / 85% branches; patch 95% lines / 85% branches against `origin/main`, Codecov mode. A passing measurement alone does not make a failed test run pass. |
| Draft PR | Pending final checks and ship workflow. The screenshot disposition is approved. |

Solution restore completed successfully; native macOS assets were restored after regenerating the Linux example lock.

## Review findings addressed during implementation

- Preserve the original caller token through each startup phase, rather than returning the linked work token.
- Release owned probe clients before a successful startup return. Check cancellation again after response/client disposal.
- Invoke lifecycle cleanup callbacks outside locks and join one shared completion.
- Preserve primary readiness errors if client/builder cleanup also fails; retain only a fixed safe secondary-cleanup category.
- Carry the new dependency into the existing packed-testing closure and both macOS/Linux example lock files.

## Measurement limits

Human onboarding time, cold/warm restore observations, reliability improvement and latency improvement are unmeasured. Controlled handlers establish retry, cancellation and ownership behavior; only real-resource runs establish native startup/allocation/HTTP integration. No package has been published.

## Final verification repairs

- The package proof now records exact nuspec package/source identities and checks restored first-party raw SHA-512 against both the local archive and isolated cache. Evidence.Aspire must retain exactly Contracts and Readiness as first-party dependencies, the supported Aspire.Hosting range, and no Aspire.Hosting.Testing edge; central transitive third-party pins remain permitted. An initial over-strict assertion was reproduced before correction.
- The packaged readiness README uses one canonical chooser/release-hub link pair inside its generated guidance region, as required by the artifact gate.
- Added fixture checks for a null build result and caller exceptions whose diagnostic storage is unavailable. The typed fixture now carries the same safe allowlisted cleanup type as the Evidence adapter; all 45 focused fixture tests passed.
- Adapter coverage work targets thrown phase cancellation, cancellation/deadline during client disposal, safe secondary metadata failure, nested reentry, and combined stop-callback/lease cleanup failure. Unreachable catch wrappers around a private result-only drain completion and owned cancellation-source disposal were removed.
- The proof harness refuses dirty inputs, exports the captured commit with `git archive`, and builds every producer and consumer fixture from that export with explicit source identity. Retained failure paths include the source export and generated consumers. Physical temporary-directory paths avoid macOS `/var` alias inconsistencies in transitive project restore.
- A private rehearsal of the immutable-export harness passed all eight package builds and both consumers. The final committed public-script QA replay passed at `bda92c18`; full solution verification, documentation audit and draft PR remain pending. No failing or interrupted run is counted as passing evidence.
