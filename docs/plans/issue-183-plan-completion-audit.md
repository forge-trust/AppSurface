# Issue #183 implementation and verification audit

The [approved design and decision record](../designs/issue-183-concept-preserving-version-switching.md) governs this implementation of [issue #183](https://github.com/forge-trust/AppSurface/issues/183). This audit distinguishes implementation from observed verification. Pending entries are work remaining, not a waiver.

| Facet | Implementation | Verification status |
| --- | --- | --- |
| Stable authored concept IDs, merge precedence and invalid-input rejection | Implemented | Focused authoring/parser tests pass |
| Canonical-winner concept sidecar before archive hashing | Implemented | Two real exports and exact CLI archive verification passed |
| Combined BCL-only identity index, pinned bridges and quarantine | Implemented; edge tests in progress | Focused resolver tests pass at intermediate checkpoints |
| Hosted guide control, plain-link choices, validated incoming notices | Implemented | Focused hosted and presentation tests pass at intermediate checkpoints |
| Exact packaged runtime hash/SRI, no connection access | Implemented | Embedded byte/hash tests pass; real browser proof pending |
| Selected version/locale, verified section fallback and fragments | Implemented; integration in progress | Resolver and hosted fragment cases pass; browser parity pending |
| Whole-artifact budgets and hosted readable-guide/503 recovery | Implemented | Focused fitting/nonfitting recovery tests pass |
| Static serving output, separate verification inputs and inventory | Implemented | 89 release/workflow tests passed; walkthrough checked113derived files and unchanged originals; completebrowserparitypending |
| Public, internal, package and repository documentation | Implemented | 315local links,11fencedblockschecked; all8walkthroughshellblocks executed successfully |
| Clean packed Docs consumer and Release dependency boundary | Harness implemented | Harness tests pass; final unchanged-HEAD package proof pending |
| Enhance review loop | Pending stable implementation | Preliminary independent integration review in progress |
| Standard browser QA | Pending | Real running hosted/static app required |
| Full solution coverage gate | Pending | Required command: `./scripts/coverage-solution.sh`; unchanged 95% line / 85% branch aggregate and patch thresholds |
| Draft PR and latest-commit CI | Pending local gates | No PR created yet |

Package proof and coverage must run separately on the final unchanged review candidate. The feature branch is `codex/make-it-so-concept-version-switching`. The publication remains a draft PR until separately authorized otherwise.

Observed later review repairs include strict static visibility, private bridge-case filtering, known nonlocalized bridge memberships, nested-heading runtime notice insertion, hidden-control specificity, and relative canonical URL preservation. The real static browser replay after repair produced one move notice, retained `#configure`, focused that anchor, and attached one description. Full Standard QA and fresh solution coverage remain mandatory.
