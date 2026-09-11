# #800 enhance review

Invocation 1, cycle budget 4. Cycle 1 started 2026-09-10. Review uses the canonical gstack review checklist and plan-completion, specialist and adversarial sections. Base: `origin/main`. Target includes new untracked files as well as the branch diff. The approved plan remains authoritative.

Preamble: protocol 1, interactive, artifact sync off, continuous local checkpoints, push disabled. Skill analytics append was denied by sandbox; no uploaded telemetry claim. Native `combo/sub` review transport follows the user policy. Nested Codex CLI and Claude-labelled passes are not run; no model-family independence claimed.

Scope: all changes implement the approved #800 SDK plan. No UI/database schema/runtime authority changes. Existing #800 adoption timing TODO remains deferred per approved T1. Plan artifacts are present; executable verification is still underway and cannot yet be marked complete.

Specialists selected: testing, maintainability, security, performance, API contract, simplification; design and data migration skipped (no UI or schema). Fresh independent reviews pending available worker slots. Red-team and adversarial pass follow specialist results.

Primary critical pass underway: dictionary/index publication guarded by registry lock, provider facts/fingerprints untouched, fixed metadata errors contain no payload/key, negative compilation uses explicit local paths and checks exact compiler errors. SQL in new PostgreSQL tests is bounded and parameterized where variable values are supplied.

Verification so far: core build 0 warnings/errors; new real PostgreSQL tests 4/4; API baseline exactly five new types/three overloads and no removals. All tests/gates must be refreshed after fixes.

Cycle1 concrete primary findings addressed:
- P2 example migration methods registered legacy and new same Work identity together and did not execute all direct/factory cases. Changed to independent before/after collections, execute each migration proof, explicit request policies/due, assert default parity and exact Flow registration identity.
- P2 README provider link used nonexistent anchor. Linked canonical Accept Work section and complete source. Snippets regenerated.
- P2 public raw-descriptor-first constructor recaptured known snapshot metadata. Fixed with view discovery prepass; order counter regression included.

Affected full tests latest: Durable284, PostgreSQL411, Provider27 all passed without skips before final example additions. Packed proof all8 positive+8 negative passed before final example additions. Refresh pending.

Cycle1 specialist results:
- Testing: three P2 verification gaps accepted, covering explicit metadata getter counters for both sources, actual registry ordering/override lookup, and composed activity-result → wait/resume/context encoding. Owners are strengthening these tests.
- Maintainability: duplicated typed/legacy source-pair capture consolidated behind one internal helper. Explicit typed capture delegates preserve typed view factories; an intermediate incorrect untyped projection was caught by the existing suite and corrected before commit.
- Performance: codec aggregation changed from sorting groups to linear duplicate detection. The ordinal-minimum conflicting key retains deterministic errors, and source-snapshot conflicts still take precedence.
- Security, API contract, simplification: no findings.
- Red team, independent adversarial, plan-completion audit: pending.

Verification: full Durable rerun after typed-delegate correction reached 286 passed, one new composed-Flow counter assertion failed (initial fixture encoding was omitted from expected count). No remaining typed-view cast failures. Flow owner correcting its assertion. All changed Markdown local-file links resolved; `git diff --check` passed.

Cycle1 complete: full Durable suite 287/287, no skips. All concrete findings addressed; independent adversarial and red-team passes found no additional defects. Adversarial fixture coverage was summary-only; actual packed proof supplies executable evidence. A stale Release PackageIndex verifier failed discovery, while the current Debug verifier passed with no repository changes. Full solution coverage is running.

Cycle2 begins with the final integrated tree. Primary focus: verify the cycle1 fixes, shared snapshot/type preservation, deterministic linear codec collisions and substantive test assertions. Previously clean security/API/simplification surfaces remain in scope; final pre-ship review will refresh against committed code.

Cycle2 primary audit: no new production defect; add measured external-subclass and raw Flow guard tests and coordinate concurrent requests to complete the plan evidence. The negative SARIF checker self-test now also rejects missing files, malformed JSON and a mixed expected/unexpected diagnostic set (self-test passed). Public baseline preserves all old entries while adding exactly five approved types and three overloads. README and migration snippets, current Debug package index, and changed Markdown links verify.

Cycle2 P1: PostgreSQL Flow startup still used raw reference equality and decoded through the stored codec. Canonical registry views therefore passed the new Flow registry/evaluator checks but failed StartAsync before storage. Fixed StartAsync to verify same-source captured provenance plus exact type/name/version, decode through the selected codec and reject invalid decoded objects. Existing internal friend access is reused; no public API/provider SPI/schema change. A real PostgreSQL shared-definition Flow regression is being added. Full validation must refresh after this fix.

Gate-derived visual finding: the append-only release entry adds a table-of-contents heading to the rendered unreleased page. Main compared committed and actual screenshots; 695 changed pixels at the new TOC text, with no layout drift in the inspected desktop capture. Refresh via the repository baseline writer, then rerun without update mode and inspect the changed captures. This is part of the authorized documentation change, not a coverage-policy waiver.

Cycle2 fixes complete on disk: PostgreSQL Flow startup provenance/selected decode; exact external protected-constructor consumer witness; shared dual-interface mismatch guards; bounded concurrent request start gate; raw context/event null/wrong return guards. Three workers handed off and closed; central runner owns compilation and validation. Primary review corrected two test fixture construction mistakes (dual-interface return overloads; null override fallback) before compilation.

Cycle3 targeted review started: current PostgreSQL startup fix, new DB proof, shared snapshot guard paths and legacy/custom registry compatibility. No further scope expansion. Required checks still gate completion.

Cycle3 P1 (confidence 10/10): a custom registry could select the raw source of a definition-owned context/event view, bypassing captured metadata guards. The shared `RetainGuardedView` helper now keeps the captured view around that exact selected source after compatibility validation, while retaining a selected canonical view when available. Both PostgreSQL startup and Flow evaluation use it. No metadata is recaptured and no registration state changes. Six new test cases cover pre-decode rejection, post-encode rejection, event classification/retention, database rejection before persistence, and valid custom-registry startup. Documentation explains the boundary. Focused verification is running.

Cycle4 final pass started against the integrated tree. Scope: close the captured-view/raw-source regression and verify all new code paths against the approved contract. The configured four-cycle limit requires an outside-review checkpoint after this pass; its advice will be recorded separately from validation results.

Cycle4 completed: fresh routed reviewer found NO ACTIONABLE FINDINGS in the final production tree. Primary pass found one broken local Markdown link in the already-approved plan (`.md:185`); corrected it to the canonical registry section anchor, without changing scope. Current Markdown link checks, negative-checker self-tests, README/migration/PostgreSQL snippet checks and package-index verification pass. The four-cycle budget is exhausted; an outside-review strategy checkpoint follows, independently of the remaining make-it-so validation gates.

Outside checkpoint complete: no blocking defects; no better implementation path identified. The reviewer recommends ending this invocation and completing the already-planned baseline, packed proof and coverage sequence. Advice recorded; no implementation changes were made from checkpoint advice. Invocation1 ends at four cycles, final cycle clean, all concrete findings fixed, none dismissed or left open. Fresh focused validation: 21 Flow unit cases, 6 PostgreSQL cases and 2 visual tests passed; changed C# formatting and diff checks passed. Coverage remains the parent's required unchanged `./scripts/coverage-solution.sh` gate, not an enhance completion claim.

## Ship-stage review

Fresh ship specialists: testing, maintainability, security, performance, API contract and simplification all ended with no findings. API initially proposed re-polling a known raw source, then withdrew that finding after verifying the approved no-recapture rule and the two-argument-order regression; separately captured conflicting views are already rejected. No production or test changes resulted. Red-team final pass: no findings. Adversarial raised mutable raw-only Flow codecs; primary triage confirms this is unchanged baseline behavior outside the approved shared-codec scope, with raw mutation explicitly unsupported. The reviewer confirmed this distinction and withdrew the finding; final result NO FINDINGS. Tests/fixtures were summary-only in the adversarial pass; actual packed and unit/integration checks provide the executable proof. Final solution gate passed on commit `2872507c`: 12,020 tests, zero failures/skips; patch 98.30% line / 94.32% branch. Final solution formatting passed.
