# Issue #183 implementation and verification audit

The [approved design and decision record](../designs/issue-183-concept-preserving-version-switching.md) governs this implementation of [issue #183](https://github.com/forge-trust/AppSurface/issues/183). This audit distinguishes implementation from observed verification. Pending entries are work remaining, not a waiver.

| Facet | Implementation | Verification status |
| --- | --- | --- |
| Stable authored concept IDs, merge precedence and invalid-input rejection | Implemented | Focused authoring/parser tests pass |
| Canonical-winner concept sidecar before archive hashing | Implemented | Two real exports and exact CLI archive verification passed |
| Combined BCL-only identity index, pinned bridges and quarantine | Implemented | Focused resolver edge tests pass; final aggregate and patch coverage pending |
| Hosted guide control, plain-link choices, validated incoming notices | Implemented | Focused hosted and presentation tests pass; actual HTTP browser parity passes |
| Exact packaged runtime hash/SRI, no connection access | Implemented | Embedded byte/hash tests and actual hosted/static CSP, SRI tamper and connection rejection browser cases pass |
| Selected version/locale, verified section fallback and fragments | Implemented | Resolver, hosted/static browser fragments, no-JS, history and reason-state cases pass |
| Whole-artifact budgets and hosted readable-guide/503 recovery | Implemented | Focused fitting/nonfitting recovery tests pass |
| Static serving output, separate verification inputs and inventory | Implemented | Release/workflow regression tests pass; walkthrough checked 113 derived files and unchanged originals; hosted/static browser parity passes |
| Public, internal, package and repository documentation | Implemented | 315local links,11fencedblockschecked; all8walkthroughshellblocks executed successfully |
| Clean packed Docs consumer and Release dependency boundary | Harness implemented | Harness tests pass; final unchanged-HEAD package proof pending |
| Enhance review loop | Completed scoped passes | Runtime notice, bridge privacy and publication repairs verified; final independent static/package review has no actionable findings at `61d389ee` |
| Standard browser QA | Passed current production code | All 16 native hosted/static cases passed; Standard browser exploration and screenshots recorded in the local QA report; synthetic partial-DOM lifecycle regression passes (1/1), and all browser cases pass (17/17) |
| Full solution coverage gate | Pending | Required command: `./scripts/coverage-solution.sh`; unchanged 95% line / 85% branch aggregate and patch thresholds. Superseded diagnostic cancelled after earlier fixture stalled; fresh final gate remains required |
| Draft PR and latest-commit CI | Pending local gates | No PR created yet |

Package proof and coverage must run separately on the final unchanged review candidate; both remain pending. The feature branch is `codex/make-it-so-concept-version-switching`. The publication remains a draft PR until separately authorized otherwise.

Observed later review repairs include strict static visibility, private bridge-case filtering, known nonlocalized bridge memberships, nested-heading runtime notice insertion, hidden-control specificity, and relative canonical URL preservation. The real static browser replay after repair produced one move notice, retained `#configure`, focused that anchor, and attached one description. Standard QA completed on the repaired production candidate: 16/16 native browser cases passed in 11.01 seconds, including actual hosted sandbox and static meta CSP, SRI tamper, connection rejection, no-JS, state distinctions, history and fragment focus. Search remains readable with server browse recovery under the existing script/connection policy; no broader script permission is claimed. The archived stylesheet is now served only after manifest/hash verification, and narrow layouts fit at 200% zoom. Fresh solution coverage and clean packed-consumer proof remain mandatory.
